using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Drawing.Imaging;
using CaptureRegionApp.Processing.Ai;
using CaptureRegionApp.Processing.Logging;
using CaptureRegionApp.Processing.Models;
using CaptureRegionApp.Processing.Ocr;

namespace CaptureRegionApp.Processing;

public sealed class CapturePipeline
{
    private sealed record OcrQueueItem(string ImagePath, string FileName, string OcrText, bool LowConfidence);

    private readonly IOcrService _ocrService;
    private readonly IOcrService? _fallbackVisionOcr;
    private readonly AiClient _aiClient;
    private readonly ProcessingSettings _settings;
    private readonly AppLogger _logger;
    private readonly ConcurrentQueue<string> _imageQueue = new();
    private readonly ConcurrentQueue<OcrQueueItem> _ocrJsonQueue = new();
    private readonly SemaphoreSlim _imageSignal = new(0);
    private readonly SemaphoreSlim _jsonSignal = new(0);
    private readonly CancellationTokenSource _queueCts = new();
    private readonly Task _imageWorker;
    private readonly Task _jsonWorker;
    private readonly List<OcrQueueItem> _ocrHistory = new();
    private readonly object _ocrHistoryLock = new();

    private static bool _hasShownTesseractWarning = false;
    private static readonly System.Collections.Generic.HashSet<string> _processedFiles = new();
    private static readonly HashSet<string> _contentHashes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _imageHashes = new(StringComparer.OrdinalIgnoreCase); // Hash của ảnh (SHA256) để check trùng ảnh
    private static readonly HashSet<string> _questionIds = new(StringComparer.OrdinalIgnoreCase); // Id câu hỏi lấy từ OCR ([123456])
    private static readonly HashSet<string> _questionNumbers = new(StringComparer.OrdinalIgnoreCase); // Số thứ tự câu hỏi (vd: 1/50, 12:33)
    private static readonly object _metaLock = new();

    public CapturePipeline(ProcessingSettings settings)
    {
        _settings = settings;
        _logger = AppLogger.Create(settings.GetOutputDirectory());
        _ocrService = CreateOcrService(settings);
        // Vision fallback: chỉ dùng khi OCR chính thiếu cấu trúc để tiết kiệm quota
        var visionKey = settings.GetGoogleVisionApiKey();
        _fallbackVisionOcr = string.IsNullOrWhiteSpace(visionKey) ||
                             settings.OcrProvider.Equals("googlevision", StringComparison.OrdinalIgnoreCase)
            ? null
            : new GoogleVisionOcrService(visionKey, _logger);
        _aiClient = new AiClient(settings, logger: _logger);

        // Khởi động 2 worker cho 2 hàng đợi (ảnh -> OCR, OCR JSON -> AI)
        _imageWorker = Task.Run(ProcessImageQueueAsync);
        _jsonWorker = Task.Run(ProcessJsonQueueAsync);
    }

    public async Task ProcessAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        EnqueueImage(imagePath);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void EnqueueImage(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return;
        }

        _imageQueue.Enqueue(imagePath);
        _imageSignal.Release();
    }

    private async Task ProcessImageQueueAsync()
    {
        while (!_queueCts.IsCancellationRequested)
        {
            try
            {
                await _imageSignal.WaitAsync(_queueCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (_imageQueue.TryDequeue(out var imagePath))
            {
                await HandleImageStageAsync(imagePath).ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessJsonQueueAsync()
    {
        while (!_queueCts.IsCancellationRequested)
        {
            try
            {
                await _jsonSignal.WaitAsync(_queueCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (_ocrJsonQueue.TryDequeue(out var item))
            {
                await HandleJsonStageAsync(item).ConfigureAwait(false);
            }
        }
    }

    private bool TryMarkProcessing(string imagePath)
    {
        if (!_settings.EnableAutoAnswer)
        {
            _logger.Log("AutoAnswer tắt - bỏ qua xử lý.");
            return false;
        }

        // Kiểm tra và đánh dấu file ngay lập tức để tránh xử lý trùng lặp
        lock (_processedFiles)
        {
            if (_processedFiles.Contains(imagePath))
            {
                return false; // Đã xử lý rồi, bỏ qua
            }
            
            // Đánh dấu file ngay khi bắt đầu để tránh race condition
            _processedFiles.Add(imagePath);
            
            // Tạo file marker ngay lập tức
            try
            {
                var markerFile = Path.Combine(_settings.GetOutputDirectory(), $"{Path.GetFileNameWithoutExtension(imagePath)}.processed");
                File.WriteAllText(markerFile, DateTime.UtcNow.ToString("O"));
            }
            catch
            {
                // Bỏ qua nếu không thể tạo marker file
            }
        }

        return true;
    }

    private async Task HandleImageStageAsync(string imagePath)
    {
        try
        {
            if (!TryMarkProcessing(imagePath))
            {
                return;
            }

            // Check 1: Trùng ảnh (pixel-based) - check TRƯỚC KHI OCR để tiết kiệm thời gian
            if (IsDuplicateImage(imagePath))
            {
                _logger.Log($"[Dedup] Bỏ qua ảnh trùng (hash): {imagePath}");
                TryDeleteFile(imagePath);
                
                // Xóa marker file
                var markerFile = Path.Combine(_settings.GetOutputDirectory(), $"{Path.GetFileNameWithoutExtension(imagePath)}.processed");
                TryDeleteFile(markerFile);
                
                return;
            }

            string ocrText = string.Empty;
            bool isLikely = false;

            // OCR tối đa 3 lần nếu chưa thấy cấu trúc câu hỏi/lựa chọn rõ ràng
            const int maxOcrAttempts = 3;
            for (int attempt = 1; attempt <= maxOcrAttempts; attempt++)
            {
                try
                {
                    // Tiền xử lý ảnh để chữ rõ hơn trước khi OCR (crop viền đen, grayscale, tăng tương phản)
                    var ocrSource = PreprocessImageForOcr(imagePath) ?? imagePath;
                    ocrText = await _ocrService.ExtractTextAsync(ocrSource, _queueCts.Token).ConfigureAwait(false);
                }
                catch (Exception ocrEx)
                {
                    // Kiểm tra xem Tesseract có tồn tại không
                    var tesseractPath = FindTesseractPath();
                    var tesseractExists = !string.IsNullOrEmpty(tesseractPath);
                    
                    _logger.Log($"OCR lỗi (attempt {attempt}/{maxOcrAttempts}): {ocrEx.Message}, Tesseract tồn tại: {tesseractExists}");
                    
                    // Chỉ hiển thị thông báo một lần duy nhất khi khởi động
                    lock (typeof(CapturePipeline))
                    {
                        if (!_hasShownTesseractWarning)
                        {
                            var warn = !tesseractExists
                                ? "Tesseract chưa được cài đặt hoặc không tìm thấy. Đang dùng dữ liệu mẫu."
                                : $"Tesseract gặp lỗi khi chạy: {ocrEx.Message}. Đang dùng dữ liệu mẫu.";
                            _logger.Log(warn);
                            _hasShownTesseractWarning = true;
                        }
                    }
                    
                    var mockOcr = new MockOcrService();
                    ocrText = await mockOcr.ExtractTextAsync(imagePath, _queueCts.Token).ConfigureAwait(false);
                }
                
                // Chỉ log vào file, không hiển thị trên console
                _logger.Log($"OCR xong (attempt {attempt}/{maxOcrAttempts}) {imagePath}, dài {ocrText.Length} ký tự.");

                // Đánh giá xem OCR có giống cấu trúc câu hỏi không
                isLikely = IsLikelyQuestion(ocrText) && HasEnoughOptionsForAi(ocrText);
                if (isLikely || attempt == maxOcrAttempts)
                {
                    if (!isLikely)
                    {
                        _logger.Log($"[OcrRetry] Sau {maxOcrAttempts} lần OCR, vẫn không nhận diện rõ cấu trúc câu hỏi cho {imagePath}. Dùng kết quả cuối cùng (LowConfidence).");
                    }
                    break;
                }

                _logger.Log($"[OcrRetry] OCR có vẻ không phải câu hỏi trắc nghiệm cho {imagePath}, sẽ thử lại (lần {attempt + 1}/{maxOcrAttempts})...");
            }

            // Validation: nếu OCR không giống cấu trúc câu hỏi, vẫn đưa vào hàng đợi nhưng đánh dấu low confidence để xử lý sau
            if (!isLikely)
            {
                _logger.Log($"[Validation] OCR thấp độ tin cậy (không rõ cấu trúc câu hỏi), vẫn xếp hàng xử lý: {imagePath}");
            }

            // Fallback sang Google Vision nếu OCR chính thiếu cấu trúc A/B/C/D hoặc low confidence
            if (_fallbackVisionOcr != null && (!isLikely || !HasEnoughOptionsForAi(ocrText)))
            {
                try
                {
                    _logger.Log("[OcrFallback] Dùng Google Vision do OCR chính thiếu cấu trúc câu hỏi.");
                    var visionText = await _fallbackVisionOcr.ExtractTextAsync(imagePath, _queueCts.Token).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(visionText))
                    {
                        ocrText = visionText;
                        isLikely = IsLikelyQuestion(ocrText) && HasEnoughOptionsForAi(ocrText);
                        _logger.Log($"[OcrFallback] Vision trả về {ocrText.Length} ký tự, isLikely={isLikely}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Log($"[OcrFallback] Google Vision lỗi: {ex.Message}");
                }
            }

            // Đưa kết quả OCR vào hàng đợi JSON (stage 2)
            var queuedItem = new OcrQueueItem(imagePath, Path.GetFileName(imagePath), ocrText, !isLikely);
            SaveIntermediateOcrJson(queuedItem);
            _ocrJsonQueue.Enqueue(queuedItem);
            _jsonSignal.Release();
        }
        catch (Exception ex)
        {
            _logger.Log($"Lỗi pipeline: {ex.Message}");
            
            // Nếu lỗi, xóa đánh dấu để có thể thử lại
            lock (_processedFiles)
            {
                _processedFiles.Remove(imagePath);
            }
            
            try
            {
                var markerFile = Path.Combine(_settings.GetOutputDirectory(), $"{Path.GetFileNameWithoutExtension(imagePath)}.processed");
                if (File.Exists(markerFile))
                {
                    File.Delete(markerFile);
                }
            }
            catch
            {
                // Bỏ qua
            }
        }
    }

    private async Task HandleJsonStageAsync(OcrQueueItem item)
    {
        try
        {
            // Nếu OCR text gần giống item trước đó → bỏ qua để tránh trùng (trừ khi low confidence để không mất dữ liệu)
            if (!item.LowConfidence && IsDuplicateOcrText(item))
            {
                _logger.Log($"[Dedup] Bỏ OCR gần trùng: {item.ImagePath}");
                TryDeleteFile(item.ImagePath);
                var markerFile = Path.Combine(_settings.GetOutputDirectory(), $"{Path.GetFileNameWithoutExtension(item.ImagePath)}.processed");
                TryDeleteFile(markerFile);
                var ocrJson = Path.Combine(_settings.GetOutputDirectory(), $"{Path.GetFileNameWithoutExtension(item.ImagePath)}_ocr.json");
                TryDeleteFile(ocrJson);
                return;
            }

            // Dedup dựa trên meta OCR (stt/id) để tiết kiệm token AI, ưu tiên dùng khi hệ thống thi đã hiện sẵn số câu & id
            var meta = ExtractQuestionMeta(item.OcrText);
            if (IsDuplicateByMeta(meta.QuestionId, meta.QuestionNumber))
            {
                _logger.Log($"[Dedup] Bỏ qua vì trùng stt/id OCR (id={meta.QuestionId ?? "-"}, stt={meta.QuestionNumber ?? "-"}) cho {item.ImagePath}");
                TryDeleteFile(item.ImagePath);
                var marker = Path.Combine(_settings.GetOutputDirectory(), $"{Path.GetFileNameWithoutExtension(item.ImagePath)}.processed");
                TryDeleteFile(marker);
                var ocrJsonPath = Path.Combine(_settings.GetOutputDirectory(), $"{Path.GetFileNameWithoutExtension(item.ImagePath)}_ocr.json");
                TryDeleteFile(ocrJsonPath);
                return;
            }
            // Ghi nhận meta đã thấy để các frame sau trùng stt/id sẽ bị bỏ qua
            RememberMeta(meta.QuestionId, meta.QuestionNumber);

            // HARD GATE: nếu OCR không hề có cấu trúc lựa chọn A/B/C/D → không gọi AI để tránh đoán bừa
            if (!HasEnoughOptionsForAi(item.OcrText))
            {
                _logger.Log($"[SkipAI] OCR không có đủ lựa chọn A/B/C/D cho {item.ImagePath}, bỏ qua AI và chỉ lưu OcrText.");
                var skippedResult = new AnswerResult
                {
                    FileName = item.FileName,
                    ImagePath = item.ImagePath,
                    QuestionNumber = string.Empty,
                    QuestionId = string.Empty,
                    Question = string.Empty,
                    Options = null,
                    Answer = string.Empty,
                    AnswerText = string.Empty,
                    RawAnswer = string.Empty,
                    OcrText = item.OcrText,
                    CreatedAt = DateTime.UtcNow
                };

                SaveResultToJson(item.ImagePath, skippedResult);
                ResultBus.Publish(skippedResult);
                return;
            }

            // Chuẩn hóa OCR cho AI: ưu tiên block câu hỏi đã lọc rác, nhưng vẫn đính kèm OCR gốc để tham chiếu
            var questionBlock = ExtractQuestionBlock(item.OcrText);
            var baseOcrForAi = string.IsNullOrWhiteSpace(questionBlock) ? item.OcrText : questionBlock;

            // Cho phép AI chạy tối đa 3 lần nếu các lần trước thiếu câu hỏi/đáp án
            AnswerResult? result = null;
            int maxAttempts = 3;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var ocrForAi = baseOcrForAi;
                // Đính kèm OCR gốc (đầy đủ, có thể chứa rác) để AI có ngữ cảnh nếu block bị thiếu
                if (!string.Equals(baseOcrForAi, item.OcrText, StringComparison.Ordinal))
                {
                    ocrForAi += "\n\n[OCR gốc (có thể chứa rác, chỉ dùng tham khảo):]\n" + item.OcrText;
                }

                if (attempt >= 2)
                {
                    // Từ lần 2 trở đi: thêm hint mạnh bắt buộc điền đủ JSON + answer A-D
                    ocrForAi += "\n\n[Lưu ý: Các lần trước bạn chưa trả về đầy đủ JSON (thiếu question hoặc answer). Lần này BẮT BUỘC trả về JSON đủ trường và chọn answer là đúng 1 chữ cái A/B/C/D.]";
                }

                var answer = await _aiClient.GetAnswerAsync(ocrForAi, _queueCts.Token).ConfigureAwait(false);

                var parsed = ParseAIResponse(answer);
                // Bổ sung QuestionNumber/QuestionId nếu AI chưa trả về nhưng OCR có
                if (string.IsNullOrWhiteSpace(parsed.QuestionNumber) || string.IsNullOrWhiteSpace(parsed.QuestionId))
                {
                    var metaFromOcr = ExtractQuestionMeta(item.OcrText);
                    if (string.IsNullOrWhiteSpace(parsed.QuestionNumber) && !string.IsNullOrWhiteSpace(metaFromOcr.QuestionNumber))
                    {
                        parsed.QuestionNumber = metaFromOcr.QuestionNumber;
                    }
                    if (string.IsNullOrWhiteSpace(parsed.QuestionId) && !string.IsNullOrWhiteSpace(metaFromOcr.QuestionId))
                    {
                        parsed.QuestionId = metaFromOcr.QuestionId;
                    }
                }
                // Nếu AI chỉ trả về mỗi đáp án (ví dụ: \"B. No\") mà không có question/options,
                // cố gắng dựng lại câu hỏi + lựa chọn từ chính OCR text.
                if (string.IsNullOrWhiteSpace(parsed.Question) ||
                    parsed.Options == null || parsed.Options.Count == 0)
                {
                    var recovered = TryRecoverFromOcr(item.OcrText, answer);
                    if (!string.IsNullOrWhiteSpace(recovered.Question))
                    {
                        parsed.Question = recovered.Question;
                    }
                    if (recovered.Options != null && recovered.Options.Count > 0)
                    {
                        parsed.Options = recovered.Options;
                    }
                    if (!string.IsNullOrWhiteSpace(recovered.Answer) &&
                        string.IsNullOrWhiteSpace(parsed.Answer))
                    {
                        parsed.Answer = recovered.Answer;
                        parsed.AnswerText = recovered.AnswerText;
                    }
                }

                result = new AnswerResult
                {
                    FileName = item.FileName,
                    ImagePath = item.ImagePath,
                    QuestionNumber = parsed.QuestionNumber ?? string.Empty,
                    QuestionId = parsed.QuestionId ?? string.Empty,
                    Question = parsed.Question,
                    Options = parsed.Options,
                    Answer = parsed.Answer,
                    AnswerText = parsed.AnswerText,
                    RawAnswer = answer,
                    OcrText = item.OcrText,
                    CreatedAt = DateTime.UtcNow
                };

                // Đánh giá mức độ "đủ thông tin" dựa trên độ bao phủ từ so với OCR
                // Dùng block câu hỏi đã lọc rác để tính coverage, tránh noise làm lệch tỉ lệ
                var ocrForCoverage = ExtractQuestionBlock(item.OcrText);
                if (string.IsNullOrWhiteSpace(ocrForCoverage))
                {
                    ocrForCoverage = item.OcrText;
                }

                var coverage = CalculateCoverageRatio(ocrForCoverage, result.Question, result.Options);
                var hasBasicFields = !string.IsNullOrWhiteSpace(result.Question) &&
                                     !string.IsNullOrWhiteSpace(result.Answer);
                var isRichEnough = coverage >= 0.10; // ít nhất 10% số từ OCR xuất hiện trong question+options

                // Nếu đã có câu hỏi + answer và độ bao phủ đủ → dùng luôn, không cần retry
                if (hasBasicFields && isRichEnough)
                {
                    break;
                }

                if (attempt < maxAttempts)
                {
                    _logger.Log($"[Retry] Kết quả thiếu thông tin cho {item.ImagePath} (hasBasicFields={hasBasicFields}, coverage={coverage:P0}), sẽ gọi AI lần {attempt + 1}...");
                }
            }

            // Sau khi retry tối đa, nếu kết quả vẫn quá xa OCR (coverage < 10%) → coi như không tin cậy, xóa Q/A để người dùng tự xử lý
            var finalOcrForCoverage = ExtractQuestionBlock(item.OcrText);
            if (string.IsNullOrWhiteSpace(finalOcrForCoverage))
            {
                finalOcrForCoverage = item.OcrText;
            }

            var finalCoverage = CalculateCoverageRatio(finalOcrForCoverage, result.Question, result.Options);
            if (finalCoverage < 0.10 || string.IsNullOrWhiteSpace(result.Question))
            {
                _logger.Log($"[RejectAI] Kết quả quá khác OCR cho {item.ImagePath} (coverage={finalCoverage:P0}), sẽ bỏ Question/Options/Answer và chỉ giữ OcrText.");
                result.Question = string.Empty;
                result.Options = null;
                result.Answer = string.Empty;
                result.AnswerText = string.Empty;
            }

            // Nếu sau khi xử lý vẫn null (không nên xảy ra) → tạo kết quả rỗng nhưng giữ OCR để chỉnh tay
            result ??= new AnswerResult
            {
                FileName = item.FileName,
                ImagePath = item.ImagePath,
                QuestionNumber = string.Empty,
                QuestionId = string.Empty,
                Question = string.Empty,
                Options = null,
                Answer = string.Empty,
                AnswerText = string.Empty,
                RawAnswer = string.Empty,
                OcrText = item.OcrText,
                CreatedAt = DateTime.UtcNow
            };

            // Validation: Kiểm tra xem kết quả có hợp lý không
            if (string.IsNullOrWhiteSpace(result.Question) || 
                (result.Options == null || result.Options.Count == 0) ||
                string.IsNullOrWhiteSpace(result.Answer))
            {
                _logger.Log($"[Validation] Kết quả không hợp lý (thiếu thông tin): {item.ImagePath}");
                _logger.Log($"  Question: '{result.Question}'");
                _logger.Log($"  Options count: {result.Options?.Count ?? 0}");
                _logger.Log($"  Answer: '{result.Answer}'");
                // Vẫn lưu để người dùng có thể chỉnh sửa sau
            }

            if (IsDuplicateByContent(result))
            {
                _logger.Log($"[Dedup] Bỏ qua ảnh trùng nội dung: {item.ImagePath}");
                
                // Xóa ảnh
                TryDeleteFile(item.ImagePath);
                
                // Xóa JSON nếu đã tạo
                var outputDir = _settings.GetOutputDirectory();
                var name = Path.GetFileNameWithoutExtension(item.ImagePath);
                var jsonPath = Path.Combine(outputDir, $"{name}_result.json");
                TryDeleteFile(jsonPath);
                
                // Xóa marker file
                var markerFile = Path.Combine(outputDir, $"{name}.processed");
                TryDeleteFile(markerFile);
                
                return;
            }

            SaveResultToJson(item.ImagePath, result);
            ResultBus.Publish(result);
        }
        catch (Exception ex)
        {
            _logger.Log($"Lỗi pipeline: {ex.Message}");
            
            // Nếu lỗi, xóa đánh dấu để có thể thử lại
            lock (_processedFiles)
            {
                _processedFiles.Remove(item.ImagePath);
            }
            
            try
            {
                var markerFile = Path.Combine(_settings.GetOutputDirectory(), $"{Path.GetFileNameWithoutExtension(item.ImagePath)}.processed");
                if (File.Exists(markerFile))
                {
                    File.Delete(markerFile);
                }
            }
            catch
            {
                // Bỏ qua
            }
        }
    }

    private void SaveIntermediateOcrJson(OcrQueueItem item)
    {
        try
        {
            var outputDir = _settings.GetOutputDirectory();
            Directory.CreateDirectory(outputDir);
            var name = Path.GetFileNameWithoutExtension(item.ImagePath);
            var jsonPath = Path.Combine(outputDir, $"{name}_ocr.json");

            var payload = new
            {
                item.FileName,
                item.ImagePath,
                OcrText = item.OcrText,
                LowConfidence = item.LowConfidence,
                EnqueuedAt = DateTime.UtcNow
            };

            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(payload, options));
        }
        catch
        {
            // ignore intermediate json errors
        }
    }

    private bool IsDuplicateOcrText(OcrQueueItem current)
    {
        var normalizedCurrent = NormalizeText(current.OcrText);
        lock (_ocrHistoryLock)
        {
            foreach (var existing in _ocrHistory)
            {
                var similarity = CalculateSimilarity(normalizedCurrent, NormalizeText(existing.OcrText));
                if (similarity >= 0.97) // nâng ngưỡng để ít bỏ sót do video có nhiều khung gần giống
                {
                    return true;
                }
            }

            _ocrHistory.Add(current);
            return false;
        }
    }

    private static string NormalizeText(string text)
    {
        var normalized = Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();
        return normalized.ToLowerInvariant();
    }

    // Jaccard dựa trên token để ước lượng độ giống nhau
    private static double CalculateSimilarity(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return 0;
        }

        var tokensA = new HashSet<string>(Regex.Split(a, @"\W+", RegexOptions.Compiled | RegexOptions.CultureInvariant).Where(t => t.Length > 1), StringComparer.OrdinalIgnoreCase);
        var tokensB = new HashSet<string>(Regex.Split(b, @"\W+", RegexOptions.Compiled | RegexOptions.CultureInvariant).Where(t => t.Length > 1), StringComparer.OrdinalIgnoreCase);

        if (tokensA.Count == 0 || tokensB.Count == 0)
        {
            return 0;
        }

        var intersection = tokensA.Intersect(tokensB, StringComparer.OrdinalIgnoreCase).Count();
        var union = tokensA.Union(tokensB, StringComparer.OrdinalIgnoreCase).Count();

        return union == 0 ? 0 : (double)intersection / union;
    }

    // Cố gắng trích ra block câu hỏi + đáp án, bỏ bớt text rác UI xung quanh
    private static string ExtractQuestionBlock(string ocrText)
    {
        if (string.IsNullOrWhiteSpace(ocrText))
        {
            return ocrText;
        }

        var rawLines = ocrText
            .Split(new[] { '\r', '\n' }, StringSplitOptions.None)
            .Select(l => l.TrimEnd()) // giữ nguyên nội dung, chỉ bỏ whitespace cuối
            .ToList();

        if (rawLines.Count == 0)
        {
            return ocrText;
        }

        // 1) Lọc bớt các dòng "rác" rõ ràng (header/footer UI của hệ thống thi)
        bool IsNoiseLine(string line)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
                return true;

            var lower = trimmed.ToLowerInvariant();

            // Các câu hệ thống, không liên quan nội dung câu hỏi
            if (lower.Contains("tôi muốn hoàn thành bài kiểm tra") ||
                lower.StartsWith("tôi muốn hoàn thành") ||
                lower.StartsWith("hoàn thành") ||
                lower.StartsWith("máy:") ||
                lower.StartsWith("student:") ||
                lower.StartsWith("máy chủ:") ||
                lower.Contains("thời lượng:") ||
                lower.Contains("thời gian còn lại") ||
                lower.Contains("tổng điểm:") ||
                lower.Contains("phông chữ:") ||
                lower.Contains("kích thước:") ||
                lower.Contains("kế tiếp") ||
                lower.Contains("nhiều lựa chọn") ||
                lower == "trả lời" ||
                lower.StartsWith("font:") ||
                lower.StartsWith("size:"))
            {
                return true;
            }

            // Dòng chỉ toàn số và khoảng trắng (ví dụ list 1 2 3 4 5 ...)
            if (Regex.IsMatch(trimmed, @"^[0-9\s]+$"))
            {
                return true;
            }

            // Dòng điều hướng / đánh dấu câu hỏi kiểu danh sách
            if (Regex.IsMatch(lower, @"^(\d+\s+){3,}\d+$"))
            {
                return true;
            }

            return false;
        }

        var filteredLines = rawLines.Where(l => !IsNoiseLine(l)).ToList();
        if (filteredLines.Count == 0)
        {
            // Nếu lọc quá tay, trả về text gốc
            return ocrText;
        }

        // 2) Xác định các dòng option (A. / A) / A:)
        bool IsOptionLine(string line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 2) return false;
            if (!char.IsLetter(trimmed[0])) return false;
            var ch = trimmed[1];
            return ch == '.' || ch == ')' || ch == ':';
        }

        var optionIndices = filteredLines
            .Select((l, idx) => (l, idx))
            .Where(t => IsOptionLine(t.l))
            .Select(t => t.idx)
            .ToList();

        if (optionIndices.Count == 0)
        {
            // Không tìm được dòng đáp án, trả về sau khi loại noise
            return string.Join(Environment.NewLine, filteredLines);
        }

        var firstOption = optionIndices.Min();
        var lastOption = optionIndices.Max();

        // 3) Chọn block: vài dòng trước đáp án đầu tiên đến hết đáp án cuối cùng
        var start = Math.Max(0, firstOption - 3); // chừa lại vùng câu hỏi phía trên
        var endExclusive = Math.Min(filteredLines.Count, lastOption + 1);

        var block = filteredLines.Skip(start).Take(endExclusive - start).ToList();
        if (block.Count == 0)
        {
            return string.Join(Environment.NewLine, filteredLines);
        }

        return string.Join(Environment.NewLine, block);
    }

    private (string? QuestionNumber, string? QuestionId, string Question, Dictionary<string, string>? Options, string Answer, string AnswerText) ParseAIResponse(string aiResponse)
    {
        try
        {
            // Tìm JSON trong response - tìm từ dấu { đầu tiên đến dấu } cuối cùng (hỗ trợ nested objects)
            var startIndex = aiResponse.IndexOf('{');
            if (startIndex >= 0)
            {
                var braceCount = 0;
                var endIndex = startIndex;
                
                for (int i = startIndex; i < aiResponse.Length; i++)
                {
                    if (aiResponse[i] == '{') braceCount++;
                    if (aiResponse[i] == '}') braceCount--;
                    if (braceCount == 0)
                    {
                        endIndex = i;
                        break;
                    }
                }

                if (endIndex > startIndex)
                {
                    var jsonText = aiResponse.Substring(startIndex, endIndex - startIndex + 1);
                    using var doc = JsonDocument.Parse(jsonText);
                    var root = doc.RootElement;

                    // Parse questionNumber và questionId - cho phép null nếu không có
                    string? questionNumber = null;
                    if (root.TryGetProperty("questionNumber", out var qn))
                    {
                        if (qn.ValueKind == JsonValueKind.Null)
                            questionNumber = null;
                        else
                            questionNumber = qn.GetString();
                    }
                    
                    string? questionId = null;
                    if (root.TryGetProperty("questionId", out var qi))
                    {
                        if (qi.ValueKind == JsonValueKind.Null)
                            questionId = null;
                        else
                            questionId = qi.GetString();
                    }
                    var question = root.TryGetProperty("question", out var q) ? q.GetString() ?? "" : "";
                    var answer = root.TryGetProperty("answer", out var a) ? a.GetString() ?? "" : "";
                    var answerText = root.TryGetProperty("answerText", out var at) ? at.GetString() ?? "" : "";

                    // Parse options
                    Dictionary<string, string>? options = null;
                    if (root.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Object)
                    {
                        options = new Dictionary<string, string>();
                        foreach (var prop in opts.EnumerateObject())
                        {
                            options[prop.Name] = prop.Value.GetString() ?? "";
                        }
                    }

                    return (questionNumber, questionId, question, options, answer, answerText);
                }
            }
        }
        catch
        {
            // Nếu parse JSON thất bại, thử fallback bằng text thuần
            return ParseFallbackPlainText(aiResponse);
        }

        // Nếu không tìm thấy JSON hoặc kết quả rỗng → fallback text
        var fallback = ParseFallbackPlainText(aiResponse);
        return fallback;
    }

    private static (string? QuestionNumber, string? QuestionId, string Question, Dictionary<string, string>? Options, string Answer, string AnswerText) ParseFallbackPlainText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, null, "", null, "", "");
        }

        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .ToList();

        string question = "";
        Dictionary<string, string>? options = null;
        string answer = "";
        string answerText = "";

        // Thử bắt theo format "Câu hỏi: ..." và "Trả lời: ..."
        foreach (var line in lines)
        {
            var qMatch = Regex.Match(line, @"^C(â|a)u h(o|ỏ)i[:\-]\s*(.+)$", RegexOptions.IgnoreCase);
            if (qMatch.Success)
            {
                question = qMatch.Groups[3].Value.Trim();
                continue;
            }

            var ansMatch = Regex.Match(line, @"^Tr(ả|a) l(ờ|o)i[:\-]\s*([A-D])\.?\s*(.*)$", RegexOptions.IgnoreCase);
            if (ansMatch.Success)
            {
                answer = ansMatch.Groups[3].Value.Trim().ToUpperInvariant();
                answerText = $"{answer}. {ansMatch.Groups[4].Value.Trim()}".Trim().Trim('.');
                continue;
            }
        }

        // Nếu chưa có question, lấy dòng đầu tiên chứa dấu ? hoặc dòng đầu tiên không rỗng
        if (string.IsNullOrWhiteSpace(question))
        {
            question = lines.FirstOrDefault(l => l.Contains("?")) ?? lines.FirstOrDefault() ?? "";
        }

        // Lấy options từ các dòng có dạng A./A)/A:
        foreach (var line in lines)
        {
            var optMatch = Regex.Match(line, @"^([A-Da-d])[.)]?\s+(.*)$");
            if (optMatch.Success)
            {
                options ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var key = optMatch.Groups[1].Value.ToUpperInvariant();
                var val = optMatch.Groups[2].Value.Trim();
                options[key] = val;
            }
        }

        // Nếu chưa có answer nhưng có answerText → suy ra chữ cái đầu
        if (string.IsNullOrWhiteSpace(answer) && !string.IsNullOrWhiteSpace(answerText))
        {
            var m = Regex.Match(answerText, @"^([A-D])", RegexOptions.IgnoreCase);
            if (m.Success) answer = m.Groups[1].Value.ToUpperInvariant();
        }

        // Nếu vẫn chưa có answer, nhưng có options và có chữ cái đơn lẻ trong text
        if (string.IsNullOrWhiteSpace(answer))
        {
            var shortAns = Regex.Match(text, @"\b([A-D])\b", RegexOptions.IgnoreCase);
            if (shortAns.Success) answer = shortAns.Groups[1].Value.ToUpperInvariant();
        }

        return (null, null, question, options, answer, answerText);
    }

    // Khi AI chỉ trả về đáp án (ví dụ: "B. No"), cố gắng dựng lại question/options từ OCR gốc.
    private static (string Question, Dictionary<string, string>? Options, string Answer, string AnswerText) TryRecoverFromOcr(string ocrText, string aiResponse)
    {
        if (string.IsNullOrWhiteSpace(ocrText) || string.IsNullOrWhiteSpace(aiResponse))
        {
            return ("", null, "", "");
        }

        // Lấy ký tự đáp án từ phản hồi AI
        var ansMatch = Regex.Match(aiResponse, @"\b([A-D])\b", RegexOptions.IgnoreCase);
        string answer = "";
        if (ansMatch.Success)
        {
            answer = ansMatch.Groups[1].Value.ToUpperInvariant();
        }
        else
        {
            // Thử bắt theo "B. No" ở đầu chuỗi
            var ansHead = Regex.Match(aiResponse.Trim(), @"^([A-D])[.)]?\s+", RegexOptions.IgnoreCase);
            if (ansHead.Success)
            {
                answer = ansHead.Groups[1].Value.ToUpperInvariant();
            }
        }

        var lines = ocrText
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .ToList();

        if (lines.Count == 0)
        {
            return ("", null, "", "");
        }

        // Tìm các dòng lựa chọn A./A)/A:
        bool IsOptionLine(string line) =>
            Regex.IsMatch(line, @"^[A-Da-d][.)]?\s+.+$");

        var optionIndices = lines
            .Select((l, idx) => (l, idx))
            .Where(t => IsOptionLine(t.l))
            .Select(t => t.idx)
            .ToList();

        if (optionIndices.Count == 0)
        {
            // Không tìm thấy lựa chọn → không đủ dữ liệu
            return ("", null, "", "");
        }

        var firstOpt = optionIndices.Min();

        // Question: các dòng trước dòng lựa chọn đầu tiên, ưu tiên dòng có '?'
        var beforeOptions = lines.Take(firstOpt).ToList();
        string question = "";
        if (beforeOptions.Count > 0)
        {
            question = beforeOptions.FirstOrDefault(l => l.Contains("?")) ??
                       string.Join(" ", beforeOptions);
        }

        // Options: parse từng dòng option
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var idx in optionIndices)
        {
            var line = lines[idx];
            var m = Regex.Match(line, @"^([A-Da-d])[.)]?\s+(.+)$");
            if (!m.Success) continue;
            var key = m.Groups[1].Value.ToUpperInvariant();
            var val = m.Groups[2].Value.Trim();

            // Gộp các dòng tiếp theo không phải option (tràn dòng)
            var extraIdx = idx + 1;
            while (extraIdx < lines.Count && !IsOptionLine(lines[extraIdx]))
            {
                val += " " + lines[extraIdx].Trim();
                extraIdx++;
            }

            options[key] = val;
        }

        if (string.IsNullOrWhiteSpace(question) && options.Count == 0)
        {
            return ("", null, "", "");
        }

        string answerText = "";
        if (!string.IsNullOrWhiteSpace(answer) && options.TryGetValue(answer, out var optText))
        {
            answerText = $"{answer}. {optText}";
        }

        return (question, options, answer, answerText);
    }

    // Tính tỷ lệ bao phủ từ của (question + options) so với OCR gốc.
    // Ví dụ: nếu OCR có 100 từ, mà question+options có 12 từ cùng nằm trong đó → coverage ≈ 12%.
    private static double CalculateCoverageRatio(string ocrText, string question, Dictionary<string, string>? options)
    {
        if (string.IsNullOrWhiteSpace(ocrText))
        {
            return 0;
        }

        // Tách token theo từ
        var ocrTokens = Regex.Split(ocrText, @"\W+", RegexOptions.Compiled | RegexOptions.CultureInvariant)
            .Where(t => t.Length > 0)
            .Select(t => t.ToLowerInvariant())
            .ToList();

        if (ocrTokens.Count == 0)
        {
            return 0;
        }

        var extractedParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(question))
        {
            extractedParts.Add(question);
        }

        if (options != null && options.Count > 0)
        {
            extractedParts.AddRange(options.Values.Where(v => !string.IsNullOrWhiteSpace(v)));
        }

        if (extractedParts.Count == 0)
        {
            return 0;
        }

        var extractedTokens = Regex.Split(string.Join(" ", extractedParts), @"\W+", RegexOptions.Compiled | RegexOptions.CultureInvariant)
            .Where(t => t.Length > 0)
            .Select(t => t.ToLowerInvariant())
            .ToList();

        if (extractedTokens.Count == 0)
        {
            return 0;
        }

        var ocrSet = new HashSet<string>(ocrTokens);
        int overlap = extractedTokens.Count(t => ocrSet.Contains(t));

        return (double)overlap / ocrTokens.Count;
    }

    /// <summary>
    /// Kiểm tra xem OCR có đủ cấu trúc lựa chọn trắc nghiệm (ít nhất 2 dòng A./B./C./D.) không.
    /// Dùng như một hard gate: nếu không đủ thì không gọi AI để tránh đoán bừa.
    /// </summary>
    private static bool HasEnoughOptionsForAi(string ocrText)
    {
        if (string.IsNullOrWhiteSpace(ocrText))
        {
            return false;
        }

        var lines = ocrText
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .ToList();

        if (lines.Count == 0)
        {
            return false;
        }

        int optionCount = 0;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            // Nhận diện lựa chọn: A. / A) / A: với ít nhất vài ký tự sau đó
            if (Regex.IsMatch(trimmed, @"^[A-Da-d][\.\):]\s*\S+"))
            {
                optionCount++;
            }
        }

        return optionCount >= 2;
    }

    /// <summary>
    /// Tiền xử lý ảnh để OCR dễ đọc hơn:
    /// - Crop bỏ viền đen trên/dưới (letterbox) nếu có.
    /// - Convert sang grayscale.
    /// - Tăng tương phản nhẹ.
    /// Trả về đường dẫn ảnh mới, hoặc null nếu có lỗi.
    /// </summary>
    private string? PreprocessImageForOcr(string imagePath)
    {
        try
        {
            if (!File.Exists(imagePath))
            {
                return null;
            }

            // Nếu file đã là ảnh tiền xử lý (_ocrprep) thì không tiền xử lý lại
            var nameNoExt = Path.GetFileNameWithoutExtension(imagePath);
            if (nameNoExt.EndsWith("_ocrprep", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            using var original = new Bitmap(imagePath);

            // 1. Crop viền đen trên/dưới nếu chiếm đa số pixel
            Rectangle cropRect = DetectVerticalContentArea(original);
            using var cropped = original.Clone(cropRect, PixelFormat.Format24bppRgb);

            // 2. Convert grayscale + tăng tương phản
            using var processed = new Bitmap(cropped.Width, cropped.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(processed))
            {
                g.DrawImage(cropped, new Rectangle(0, 0, processed.Width, processed.Height));
            }

            ApplyGrayscaleAndContrast(processed, 1.2f); // contrast ~1.2

            var dir = Path.GetDirectoryName(imagePath) ?? Path.GetTempPath();
            var fileName = Path.GetFileNameWithoutExtension(imagePath);
            var prepPath = Path.Combine(dir, $"{fileName}_ocrprep.png");
            processed.Save(prepPath, ImageFormat.Png);
            return prepPath;
        }
        catch
        {
            // Nếu có lỗi (không đọc được ảnh, v.v.) thì dùng ảnh gốc
            return null;
        }
    }

    /// <summary>
    /// Tìm vùng có nội dung chính theo chiều dọc, loại bỏ các dải đen trên/dưới.
    /// </summary>
    private static Rectangle DetectVerticalContentArea(Bitmap bmp)
    {
        int width = bmp.Width;
        int height = bmp.Height;

        bool IsRowMostlyBlack(int y)
        {
            const int sampleCols = 32;
            int step = Math.Max(1, width / sampleCols);
            int darkCount = 0;
            int total = 0;

            for (int x = 0; x < width; x += step)
            {
                var c = bmp.GetPixel(x, y);
                // Tính độ sáng (0-255)
                int brightness = (c.R + c.G + c.B) / 3;
                if (brightness < 20) darkCount++;
                total++;
            }

            // Nếu >90% sample là đen → coi là hàng đen
            return total > 0 && darkCount >= total * 0.9;
        }

        int top = 0;
        while (top < height - 1 && IsRowMostlyBlack(top))
        {
            top++;
        }

        int bottom = height - 1;
        while (bottom > top && IsRowMostlyBlack(bottom))
        {
            bottom--;
        }

        // Nếu gần như toàn ảnh là đen thì trả về full
        if (bottom <= top + height / 10)
        {
            return new Rectangle(0, 0, width, height);
        }

        int newHeight = bottom - top + 1;
        return new Rectangle(0, top, width, newHeight);
    }

    /// <summary>
    /// Áp dụng grayscale + tăng tương phản đơn giản cho bitmap.
    /// </summary>
    private static void ApplyGrayscaleAndContrast(Bitmap bmp, float contrast)
    {
        // contrast >1: tăng tương phản, ~1.0 là giữ nguyên
        contrast = Math.Max(0.5f, Math.Min(3.0f, contrast));

        int width = bmp.Width;
        int height = bmp.Height;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var c = bmp.GetPixel(x, y);
                // Grayscale
                int gray = (int)(0.299 * c.R + 0.587 * c.G + 0.114 * c.B);

                // Contrast adjust (scale around 128)
                int adjusted = (int)((gray - 128) * contrast + 128);
                adjusted = Math.Clamp(adjusted, 0, 255);

                var nc = Color.FromArgb(adjusted, adjusted, adjusted);
                bmp.SetPixel(x, y, nc);
            }
        }
    }

    private string FixCommonOcrErrors(string ocrText)
    {
        if (string.IsNullOrWhiteSpace(ocrText))
            return ocrText;

        var result = ocrText;
        
        // Sửa các lỗi OCR phổ biến
        var corrections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Lỗi ký tự đơn
            { "qun", "gun" },
            { "zor", "Razor" },
            { "ist", "first" },
            { "outine", "outline" },
            { "yal", "y'all" },
            
            // Lỗi từ phức tạp
            { "Pogetoda", "PageModel" },
            { "Catele", "Controller" },
            { "Vietfode", "ViewModel" },
            { "Adetabscosntty", "A database entity" },
            { "Pamswork", "Framework" },
            { "Areslacament", "A replacement" },
            { "esc", "described" },
            { "ic", "is" },
            { "ond", "and" },
            { "fer", "for" },
            { "TAL", "HTML" },
            
            // Lỗi từ thường gặp trong câu hỏi
            { "the ist", "the first" },
            { "qun restrictions", "gun restrictions" },
            { "qun laws", "gun laws" },
        };

        // Sửa từng lỗi (chỉ sửa từ đầy đủ, không sửa trong từ)
        foreach (var correction in corrections)
        {
            // Sửa cả chữ hoa và chữ thường, chỉ sửa từ đầy đủ
            result = Regex.Replace(result, @"\b" + Regex.Escape(correction.Key) + @"\b", correction.Value, RegexOptions.IgnoreCase);
        }

        return result;
    }

    private bool IsDuplicateByContent(AnswerResult result)
    {
        try
        {
            // Tạo key từ nội dung (question + options + answer)
            var contentKey = BuildContentKey(result);
            
            // Check 1: Trong memory (đã xử lý trong session này)
            lock (_contentHashes)
            {
                if (_contentHashes.Contains(contentKey))
                {
                    return true;
                }
            }

            // Check 2: Trong các JSON đã có trên disk (đảm bảo không trùng với kết quả cũ)
            var outputDir = _settings.GetOutputDirectory();
            if (Directory.Exists(outputDir))
            {
                var jsonFiles = Directory.GetFiles(outputDir, "*_result.json", SearchOption.TopDirectoryOnly);
                foreach (var jsonFile in jsonFiles)
                {
                    try
                    {
                        var json = File.ReadAllText(jsonFile);
                        var existing = JsonSerializer.Deserialize<AnswerResult>(json);
                        if (existing != null)
                        {
                            var existingKey = BuildContentKey(existing);
                            if (string.Equals(contentKey, existingKey, StringComparison.OrdinalIgnoreCase))
                            {
                                // Trùng nội dung với JSON đã có → trùng
                                return true;
                            }
                        }
                    }
                    catch
                    {
                        // Bỏ qua nếu không đọc được JSON
                    }
                }
            }

            // Không trùng → thêm vào memory và trả về false
            lock (_contentHashes)
            {
                _contentHashes.Add(contentKey);
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    // Check trùng ảnh bằng SHA256 hash (pixel-based)
    private bool IsDuplicateImage(string imagePath)
    {
        try
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var stream = File.OpenRead(imagePath);
            var hashBytes = sha.ComputeHash(stream);
            var hash = Convert.ToHexString(hashBytes);
            
            lock (_imageHashes)
            {
                if (_imageHashes.Contains(hash))
                {
                    return true; // Đã có ảnh giống hệt này
                }
                _imageHashes.Add(hash);
                return false;
            }
        }
        catch
        {
            return false; // Nếu lỗi, không coi là trùng
        }
    }

    // Heuristic: OCR có giống cấu trúc câu hỏi trắc nghiệm không
    private bool IsLikelyQuestion(string ocrText)
    {
        if (string.IsNullOrWhiteSpace(ocrText))
        {
            return false;
        }

        var text = ocrText.Trim();
        // Điều kiện 1: Có dấu hỏi
        var hasQuestionMark = text.Contains("?");

        // Điều kiện 2: Có tối thiểu 2 dòng lựa chọn A./B./C./D.
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        int optionCount = 0;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            // Nhận diện lựa chọn: A. / A) / A: (không bắt buộc có khoảng trắng sau)
            if (trimmed.Length >= 2 &&
                char.IsLetter(trimmed[0]) &&
                (trimmed[1] == '.' || trimmed[1] == ')' || trimmed[1] == ':'))
            {
                optionCount++;
            }
        }

        var hasOptions = optionCount >= 2;

        // Điều kiện 3: Độ dài tối thiểu để tránh rác (giảm xuống 20 để hỗ trợ câu ngắn hoặc ít lựa chọn)
        var hasMinLength = text.Length >= 20;

        return (hasQuestionMark && hasOptions) || (hasOptions && hasMinLength) || (hasQuestionMark && hasMinLength);
    }

    // Helper method để tạo content key từ AnswerResult
    private static string BuildContentKey(AnswerResult result)
    {
        var parts = new List<string>
        {
            (result.Question ?? string.Empty).Trim()
        };

        if (result.Options != null && result.Options.Count > 0)
        {
            foreach (var kv in result.Options.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                parts.Add($"{kv.Key}:{kv.Value}".Trim());
            }
        }

        var ansText = !string.IsNullOrWhiteSpace(result.AnswerText)
            ? result.AnswerText
            : result.Answer;
        parts.Add((ansText ?? string.Empty).Trim());

        var key = string.Join("|", parts)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        key = Regex.Replace(key, @"\s+", " ");
        return key;
    }

    // Trích QuestionNumber / QuestionId từ OCR nếu có sẵn (ví dụ "55:33", "[163119]")
    private static (string? QuestionNumber, string? QuestionId) ExtractQuestionMeta(string ocrText)
    {
        if (string.IsNullOrWhiteSpace(ocrText))
        {
            return (null, null);
        }

        string? questionNumber = null;
        string? questionId = null;

        // Mẫu câu số dạng "55:33" hoặc "12/50"
        var numberMatch = Regex.Match(ocrText, @"\b(\d{1,3}\s*[:：/]\s*\d{1,3})\b");
        if (numberMatch.Success)
        {
            questionNumber = numberMatch.Groups[1].Value.Replace(" ", string.Empty);
        }

        // Mẫu questionId dạng "[163119]"
        var idMatch = Regex.Match(ocrText, @"\[\s*(\d{3,})\s*\]");
        if (idMatch.Success)
        {
            questionId = $"[{idMatch.Groups[1].Value}]";
        }

        return (questionNumber, questionId);
    }

    private bool IsDuplicateByMeta(string? questionId, string? questionNumber)
    {
        // Nếu không có meta thì không dùng rule này
        if (string.IsNullOrWhiteSpace(questionId) && string.IsNullOrWhiteSpace(questionNumber))
        {
            return false;
        }

        // Check trong bộ nhớ
        lock (_metaLock)
        {
            if (!string.IsNullOrWhiteSpace(questionId) && _questionIds.Contains(questionId))
            {
                return true;
            }
            if (!string.IsNullOrWhiteSpace(questionNumber) && _questionNumbers.Contains(questionNumber))
            {
                return true;
            }
        }

        // Check trên các file kết quả đã lưu để tránh xử lý lại sau khi restart
        try
        {
            var outputDir = _settings.GetOutputDirectory();
            if (Directory.Exists(outputDir))
            {
                var jsonFiles = Directory.GetFiles(outputDir, "*_result.json", SearchOption.TopDirectoryOnly);
                foreach (var jsonFile in jsonFiles)
                {
                    try
                    {
                        var json = File.ReadAllText(jsonFile);
                        var existing = JsonSerializer.Deserialize<AnswerResult>(json);
                        if (existing == null) continue;

                        if (!string.IsNullOrWhiteSpace(questionId) &&
                            string.Equals(existing.QuestionId, questionId, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }

                        if (!string.IsNullOrWhiteSpace(questionNumber) &&
                            string.Equals(existing.QuestionNumber, questionNumber, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                    catch
                    {
                        // Bỏ qua file lỗi
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private void RememberMeta(string? questionId, string? questionNumber)
    {
        if (string.IsNullOrWhiteSpace(questionId) && string.IsNullOrWhiteSpace(questionNumber))
        {
            return;
        }

        lock (_metaLock)
        {
            if (!string.IsNullOrWhiteSpace(questionId))
            {
                _questionIds.Add(questionId);
            }
            if (!string.IsNullOrWhiteSpace(questionNumber))
            {
                _questionNumbers.Add(questionNumber);
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore
        }
    }

    private void SaveResultToJson(string imagePath, AnswerResult result)
    {
        try
        {
            var outputDir = _settings.GetOutputDirectory();
            Directory.CreateDirectory(outputDir);

            var name = Path.GetFileNameWithoutExtension(imagePath);
            var jsonPath = Path.Combine(outputDir, $"{name}_result.json");

            // Đảm bảo ImagePath và FileName trong result là chính xác và đầy đủ
            // ImagePath: đường dẫn đầy đủ tuyệt đối để đảm bảo không lấy nhầm ảnh
            // FileName: tên file để dễ dàng tìm lại
            result.ImagePath = Path.GetFullPath(imagePath); // Đảm bảo đường dẫn tuyệt đối
            result.FileName = Path.GetFileName(imagePath); // Đảm bảo tên file chính xác

            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            var json = JsonSerializer.Serialize(result, options);
            File.WriteAllText(jsonPath, json);

            // marker giữ nguyên hành vi cũ
            var markerFile = Path.Combine(outputDir, $"{name}.processed");
            if (!File.Exists(markerFile))
            {
                File.WriteAllText(markerFile, DateTime.UtcNow.ToString("O"));
            }

            // Nếu là ảnh từ video, di chuyển sang thư mục VideoCaptures để tách biệt với ảnh chụp tay
            try
            {
                var fileName = Path.GetFileName(imagePath);
                if (!string.IsNullOrWhiteSpace(fileName) &&
                    fileName.StartsWith("capture_video_", StringComparison.OrdinalIgnoreCase))
                {
                    var root = Program.GetProjectRoot();
                    var videoDir = Path.Combine(root, "VideoCaptures");
                    Directory.CreateDirectory(videoDir);
                    var dest = Path.Combine(videoDir, fileName);
                    if (!string.Equals(dest, imagePath, StringComparison.OrdinalIgnoreCase))
                    {
                        if (File.Exists(dest))
                        {
                            File.Delete(dest);
                        }
                        File.Move(imagePath, dest);
                        
                        // Cập nhật lại ImagePath trong JSON sau khi di chuyển ảnh
                        result.ImagePath = Path.GetFullPath(dest);
                        result.FileName = Path.GetFileName(dest);
                        var updatedJson = JsonSerializer.Serialize(result, options);
                        File.WriteAllText(jsonPath, updatedJson);
                    }
                }
            }
            catch
            {
                // ignore move errors
            }
        }
        catch (Exception ex)
        {
            _logger.Log($"Lưu kết quả JSON lỗi: {ex.Message}");
        }
    }

    private static string? FindTesseractPath()
    {
        // Tìm Tesseract ở các vị trí phổ biến
        var possiblePaths = new[]
        {
            @"C:\Program Files\Tesseract-OCR\tesseract.exe",
            @"C:\Program Files (x86)\Tesseract-OCR\tesseract.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tesseract-OCR", "tesseract.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tesseract-OCR", "tesseract.exe"),
        };

        foreach (var path in possiblePaths)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        // Thử tìm trong PATH
        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "where",
                    Arguments = "tesseract",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            
            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
            {
                var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (File.Exists(trimmed))
                    {
                        return trimmed;
                    }
                }
            }
        }
        catch
        {
            // Bỏ qua nếu không tìm được
        }

        return null;
    }

    private IOcrService CreateOcrService(ProcessingSettings settings)
    {
        var provider = settings.OcrProvider?.Trim().ToLowerInvariant();
        return provider switch
        {
            "paddle" => new PaddleOcrService(
                pythonPath: settings.PaddlePythonPath,
                scriptPath: settings.PaddleScriptPath,
                lang: settings.PaddleLang,
                useAngleCls: settings.PaddleUseAngleCls,
                logger: _logger),
            "googlevision" or "vision" or "gcv" => new GoogleVisionOcrService(
                apiKey: settings.GetGoogleVisionApiKey(),
                logger: _logger),
            _ => new CommandLineOcrService(settings.OcrCommand)
        };
    }
}

