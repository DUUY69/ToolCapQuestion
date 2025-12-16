using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CaptureRegionApp.Processing.Logging;
using System.Collections.Generic;
using System.Linq;

namespace CaptureRegionApp.Processing.Ai;

public sealed class AiClient
{
    private readonly ProcessingSettings _settings;
    private readonly HttpClient _httpClient;
    private readonly AppLogger _logger;
    private static int _keyIndex = 0; // Round-robin counter
    private static readonly object _keyLock = new object();
    
    // Track số câu hỏi còn lại phải skip cho mỗi key (0 = có thể dùng)
    private static readonly Dictionary<string, int> _keySkipCounters = new Dictionary<string, int>();
    private const int SkipCountAfterError = 6; // Bỏ qua 6 câu sau khi lỗi
    private static int _ollamaConsecutiveCount = 0; // Đếm số lần liên tiếp đã dùng Ollama

    public AiClient(ProcessingSettings settings, HttpClient? httpClient = null, AppLogger? logger = null)
    {
        _settings = settings;
        _httpClient = httpClient ?? new HttpClient();
        _logger = logger ?? AppLogger.Null;
    }

    public async Task<string> GetAnswerAsync(string ocrText, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ocrText))
        {
            return "Không tìm thấy nội dung từ OCR.";
        }

        var prompt = $"{_settings.PromptPrefix}\n\nNội dung OCR:\n{ocrText}";
        var geminiKeys = _settings.GetGeminiApiKeysOrdered();
        var geminiModels = _settings.GetGeminiModelsOrdered();

        if (geminiKeys.Count == 0)
        {
            _logger.Log("⚠ Không có Gemini keys, chuyển sang Ollama...");
            return await CallOllamaAsync(prompt, cancellationToken).ConfigureAwait(false);
        }

        // Giảm counter của tất cả keys đi 1 (mỗi câu hỏi mới)
        lock (_keyLock)
        {
            var keysToUpdate = _keySkipCounters.Keys.ToList();
            foreach (var key in keysToUpdate)
            {
                if (_keySkipCounters[key] > 0)
                {
                    _keySkipCounters[key]--;
                    if (_keySkipCounters[key] == 0)
                    {
                        var keyTail = key.Length > 4 ? key[^4..] : key;
                        _logger.Log($"🔄 Key ****{keyTail} đã hết thời gian skip, sẽ thử lại");
                    }
                }
            }
        }

        // Lọc ra các key có thể dùng (counter = 0 hoặc chưa có trong dict)
        List<string> availableKeys;
        lock (_keyLock)
        {
            availableKeys = geminiKeys.Where(key => !_keySkipCounters.ContainsKey(key) || _keySkipCounters[key] == 0).ToList();
        }

        // Nếu tất cả keys đều bị skip → chuyển sang Ollama
        if (availableKeys.Count == 0)
        {
            bool shouldUseOllama = false;
            lock (_keyLock)
            {
                _ollamaConsecutiveCount++;
                
                // Nếu đã dùng Ollama 6 lần liên tiếp → reset tất cả key counters để thử lại Gemini
                if (_ollamaConsecutiveCount >= 6)
                {
                    _logger.Log($"🔄 Đã dùng Ollama {_ollamaConsecutiveCount} lần liên tiếp, reset tất cả Gemini keys để thử lại...");
                    _keySkipCounters.Clear();
                    _ollamaConsecutiveCount = 0;
                    // Thử lại với tất cả keys
                    availableKeys = geminiKeys.ToList();
                }
                else
                {
                    shouldUseOllama = true;
                }
            }
            
            if (shouldUseOllama)
            {
                _logger.Log($"⚠ Tất cả {geminiKeys.Count} Gemini keys đều đang bị skip (sau lỗi), chuyển sang Ollama (lần {_ollamaConsecutiveCount}/6)...");
                try
                {
                    var ollamaResult = await CallOllamaAsync(prompt, cancellationToken).ConfigureAwait(false);
                    _logger.Log("✓ Ollama thành công");
                    return ollamaResult;
                }
                catch (Exception ex)
                {
                    _logger.Log($"✗ Ollama lỗi: {ex.Message}");
                    throw;
                }
            }
        }
        else
        {
            // Có keys khả dụng → reset Ollama counter
            lock (_keyLock)
            {
                _ollamaConsecutiveCount = 0;
            }
        }

        // Round-robin: bắt đầu từ key tiếp theo trong danh sách available
        int startKeyIndex;
        lock (_keyLock)
        {
            startKeyIndex = _keyIndex % availableKeys.Count;
            _keyIndex = (_keyIndex + 1) % availableKeys.Count;
        }

        // Với mỗi key, thử cả 2 model trước khi chuyển sang key khác
        foreach (var key in availableKeys)
        {
            // Tìm index của key trong danh sách đầy đủ
            var keyIndexInAll = -1;
            for (int i = 0; i < geminiKeys.Count; i++)
            {
                if (geminiKeys[i] == key)
                {
                    keyIndexInAll = i;
                    break;
                }
            }
            if (keyIndexInAll < 0) continue;

            // Thử tất cả models với key này trước khi skip
            bool keyShouldBeSkipped = false;
            bool keyHas403 = false;
            string? lastError = null;
            
            foreach (var model in geminiModels)
            {
                try
                {
                    var result = await CallGeminiAsync(prompt, model, key, cancellationToken).ConfigureAwait(false);
                    var keyTail = key.Length > 4 ? key[^4..] : key;
                    _logger.Log($"✓ Gemini thành công với model {model}, key ****{keyTail}");
                    
                    // Reset counter nếu key này đã từng bị skip (thành công rồi thì reset)
                    lock (_keyLock)
                    {
                        if (_keySkipCounters.ContainsKey(key))
                        {
                            _keySkipCounters.Remove(key);
                        }
                        // Reset Ollama counter vì đã có key thành công
                        _ollamaConsecutiveCount = 0;
                    }
                    
                    return result;
                }
                catch (GeminiRateLimitException)
                {
                    var keyTail = key.Length > 4 ? key[^4..] : key;
                    _logger.Log($"✗ Gemini 429 (Too Many Requests) với model {model}, key ****{keyTail}");
                    keyShouldBeSkipped = true;
                    lastError = "429";
                    await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                    // Tiếp tục thử model tiếp theo
                }
                catch (Exception ex)
                {
                    var keyTail = key.Length > 4 ? key[^4..] : key;
                    _logger.Log($"✗ Gemini lỗi với model {model}, key ****{keyTail}: {ex.Message}");
                    
                    // Nếu là lỗi 403 (Forbidden) → đánh dấu để skip sau khi thử hết models
                    if (ex.Message.Contains("403") || ex.Message.Contains("Forbidden"))
                    {
                        keyHas403 = true;
                        lastError = "403";
                        // Tiếp tục thử model tiếp theo
                    }
                    else
                    {
                        // Lỗi khác (timeout, network...) → tiếp tục thử model tiếp theo
                        lastError = ex.Message;
                    }
                }
            }
            
            // Sau khi thử hết tất cả models, nếu cả 2 đều 429 hoặc có 403 → skip key này
            if (keyShouldBeSkipped || keyHas403)
            {
                var keyTail = key.Length > 4 ? key[^4..] : key;
                lock (_keyLock)
                {
                    _keySkipCounters[key] = SkipCountAfterError;
                }
                var reason = keyHas403 ? "403 Forbidden" : "429 Too Many Requests";
                _logger.Log($"⏸ Key ****{keyTail} ({reason}) sẽ bị skip trong {SkipCountAfterError} câu hỏi tiếp theo");
            }
        }

        // Nếu đến đây nghĩa là tất cả available keys đều lỗi
        lock (_keyLock)
        {
            _ollamaConsecutiveCount++;
            
            // Nếu đã dùng Ollama 6 lần liên tiếp → reset tất cả key counters để thử lại Gemini
            if (_ollamaConsecutiveCount >= 6)
            {
                _logger.Log($"🔄 Đã dùng Ollama {_ollamaConsecutiveCount} lần liên tiếp, reset tất cả Gemini keys để thử lại...");
                _keySkipCounters.Clear();
                _ollamaConsecutiveCount = 0;
                // Không dùng Ollama nữa, trả về lỗi để caller biết (hoặc có thể retry)
                throw new Exception("Tất cả Gemini keys đều lỗi, đã reset và cần thử lại.");
            }
        }
        
        _logger.Log($"⚠ Tất cả Gemini keys khả dụng đều lỗi, chuyển sang Ollama (lần {_ollamaConsecutiveCount}/6)...");
        try
        {
            var ollamaResult = await CallOllamaAsync(prompt, cancellationToken).ConfigureAwait(false);
            _logger.Log("✓ Ollama thành công");
            return ollamaResult;
        }
        catch (Exception ex)
        {
            _logger.Log($"✗ Ollama lỗi: {ex.Message}");
            throw;
        }
    }

    private async Task<string> CallGeminiAsync(string prompt, string model, string apiKey, CancellationToken cancellationToken)
    {
        var resolvedModel = string.IsNullOrWhiteSpace(model) ? "gemini-2.5-flash" : model;
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{resolvedModel}:generateContent?key={apiKey}";

        var payload = new
        {
            contents = new[]
            {
                new
                {
                    parts = new[]
                    {
                        new { text = prompt }
                    }
                }
            }
        };

        using var resp = await _httpClient.PostAsJsonAsync(url, payload, cancellationToken).ConfigureAwait(false);
        
        if (!resp.IsSuccessStatusCode)
        {
            var errorBody = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                throw new GeminiRateLimitException($"Response status code does not indicate success: {(int)resp.StatusCode} ({resp.StatusCode}). Body: {errorBody}");
            }
            throw new HttpRequestException($"Response status code does not indicate success: {(int)resp.StatusCode} ({resp.StatusCode}). Body: {errorBody}");
        }
        var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("candidates", out var candidates) &&
            candidates.GetArrayLength() > 0 &&
            candidates[0].TryGetProperty("content", out var content) &&
            content.TryGetProperty("parts", out var parts) &&
            parts.GetArrayLength() > 0 &&
            parts[0].TryGetProperty("text", out var text))
        {
            return text.GetString() ?? "Không nhận được nội dung từ Gemini.";
        }

        return "Không nhận được nội dung từ Gemini.";
    }

    private async Task<string> CallOllamaAsync(string prompt, CancellationToken cancellationToken)
    {
        var endpoint = string.IsNullOrWhiteSpace(_settings.OllamaEndpoint)
            ? "http://localhost:11434/api/generate"
            : _settings.OllamaEndpoint;

        var model = string.IsNullOrWhiteSpace(_settings.OllamaModel)
            ? "qwen2.5:7b-instruct"
            : _settings.OllamaModel;

        _logger.Log($"Đang gọi Ollama: {endpoint}, model: {model}");

        var payload = new
        {
            model,
            prompt,
            stream = false
        };

        using var resp = await _httpClient.PostAsync(
            endpoint,
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            cancellationToken).ConfigureAwait(false);

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var doc = JsonDocument.Parse(body);

        if (doc.RootElement.TryGetProperty("response", out var response))
        {
            return response.GetString() ?? "Không nhận được nội dung từ Ollama.";
        }

        if (doc.RootElement.TryGetProperty("text", out var text))
        {
            return text.GetString() ?? "Không nhận được nội dung từ Ollama.";
        }

        return "Không nhận được nội dung từ Ollama.";
    }
}

// Custom exception để bắt 429 riêng
internal sealed class GeminiRateLimitException : Exception
{
    public GeminiRateLimitException(string message) : base(message) { }
}

