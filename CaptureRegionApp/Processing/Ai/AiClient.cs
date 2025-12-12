using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CaptureRegionApp.Processing.Logging;

namespace CaptureRegionApp.Processing.Ai;

public sealed class AiClient
{
    private readonly ProcessingSettings _settings;
    private readonly HttpClient _httpClient;
    private readonly AppLogger _logger;

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
        var geminiKey = _settings.GetGeminiApiKey();

        if (!string.IsNullOrWhiteSpace(geminiKey))
        {
            try
            {
                return await CallGeminiAsync(prompt, geminiKey, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Log($"Gemini lỗi, chuyển sang Ollama: {ex.Message}");
            }
        }

        return await CallOllamaAsync(prompt, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> CallGeminiAsync(string prompt, string apiKey, CancellationToken cancellationToken)
    {
        var model = string.IsNullOrWhiteSpace(_settings.GeminiModel) ? "gemini-2.5-flash" : _settings.GeminiModel;
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";

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
        resp.EnsureSuccessStatusCode();
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

