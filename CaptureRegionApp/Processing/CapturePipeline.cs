using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CaptureRegionApp.Processing.Ai;
using CaptureRegionApp.Processing.Logging;
using CaptureRegionApp.Processing.Ocr;

namespace CaptureRegionApp.Processing;

public sealed class CapturePipeline
{
    private readonly IOcrService _ocrService;
    private readonly AiClient _aiClient;
    private readonly ProcessingSettings _settings;
    private readonly AppLogger _logger;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();

    private static bool _hasShownTesseractWarning = false;
    private static readonly System.Collections.Generic.HashSet<string> _processedFiles = new();

    public CapturePipeline(ProcessingSettings settings)
    {
        _settings = settings;
        _logger = AppLogger.Create(settings.GetOutputDirectory());
        _ocrService = new CommandLineOcrService(settings.OcrCommand);
        _aiClient = new AiClient(settings, logger: _logger);
        
        // Tạo console window nếu chưa có
        try
        {
            AllocConsole();
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch
        {
            // Bỏ qua nếu không thể tạo console
        }
    }

    public async Task ProcessAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        if (!_settings.EnableAutoAnswer)
        {
            _logger.Log("AutoAnswer tắt - bỏ qua xử lý.");
            return;
        }

        // Kiểm tra và đánh dấu file ngay lập tức để tránh xử lý trùng lặp
        lock (_processedFiles)
        {
            if (_processedFiles.Contains(imagePath))
            {
                return; // Đã xử lý rồi, bỏ qua
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

        try
        {
            // Hiển thị thông báo đang xử lý
            var fileName = Path.GetFileName(imagePath);
            Console.WriteLine($"\n[ĐANG XỬ LÝ] {fileName}...");

            string ocrText;
            try
            {
                ocrText = await _ocrService.ExtractTextAsync(imagePath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ocrEx)
            {
                // Kiểm tra xem Tesseract có tồn tại không
                var tesseractPath = FindTesseractPath();
                var tesseractExists = !string.IsNullOrEmpty(tesseractPath);
                
                _logger.Log($"OCR lỗi: {ocrEx.Message}, Tesseract tồn tại: {tesseractExists}");
                
                // Chỉ hiển thị thông báo một lần duy nhất khi khởi động
                lock (typeof(CapturePipeline))
                {
                    if (!_hasShownTesseractWarning)
                    {
                        if (!tesseractExists)
                        {
                            Console.WriteLine($"[CẢNH BÁO] Tesseract OCR chưa được cài đặt!");
                            Console.WriteLine($"[CẢNH BÁO] Hệ thống đang dùng DỮ LIỆU MẪU - tất cả ảnh sẽ cho kết quả giống nhau.");
                            Console.WriteLine($"[CẢNH BÁO] Để đọc nội dung thật từ ảnh, vui lòng cài Tesseract:");
                            Console.WriteLine($"[CẢNH BÁO]   - Dùng Chocolatey: choco install tesseract");
                            Console.WriteLine($"[CẢNH BÁO]   - Hoặc xem chi tiết: INSTALL_TESSERACT.md\n");
                        }
                        else
                        {
                            Console.WriteLine($"[CẢNH BÁO] Tesseract đã được cài đặt nhưng gặp lỗi khi chạy: {ocrEx.Message}");
                            Console.WriteLine($"[CẢNH BÁO] Tạm thời dùng dữ liệu mẫu. Vui lòng kiểm tra lại cấu hình.\n");
                        }
                        _hasShownTesseractWarning = true;
                    }
                }
                
                var mockOcr = new MockOcrService();
                ocrText = await mockOcr.ExtractTextAsync(imagePath, cancellationToken).ConfigureAwait(false);
            }
            
            // Chỉ log vào file, không hiển thị trên console
            _logger.Log($"OCR xong {imagePath}, dài {ocrText.Length} ký tự.");

            // Post-processing: Sửa một số lỗi OCR phổ biến trước khi gửi cho AI
            ocrText = FixCommonOcrErrors(ocrText);

            var answer = await _aiClient.GetAnswerAsync(ocrText, cancellationToken).ConfigureAwait(false);
            
            // Chỉ in kết quả ra console, không lưu file
            PrintResultToConsole(ocrText, answer);
            
            Console.WriteLine($"[HOÀN TẤT] {fileName} đã được xử lý.\n");
        }
        catch (Exception ex)
        {
            _logger.Log($"Lỗi pipeline: {ex.Message}");
            Console.WriteLine($"[LỖI] {ex.Message}");
            
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

    private void PrintResultToConsole(string ocrText, string answer)
    {
        try
        {
            // Parse JSON từ AI response
            var result = ParseAIResponse(answer);

            Console.WriteLine("\n" + new string('=', 80));
            Console.WriteLine("KẾT QUẢ XỬ LÝ");
            Console.WriteLine(new string('=', 80));
            
            if (!string.IsNullOrWhiteSpace(result.QuestionNumber))
            {
                Console.WriteLine($"\nCâu hỏi: {result.QuestionNumber}");
            }
            
            if (!string.IsNullOrWhiteSpace(result.QuestionId))
            {
                Console.WriteLine($"Mã: {result.QuestionId}");
            }
            
            if (!string.IsNullOrWhiteSpace(result.Question))
            {
                Console.WriteLine($"\nCâu hỏi:\n{result.Question}\n");
            }
            
            // Hiển thị các lựa chọn A, B, C, D
            if (result.Options != null && result.Options.Count > 0)
            {
                Console.WriteLine("Các lựa chọn:");
                foreach (var option in result.Options)
                {
                    Console.WriteLine($"  {option.Key}. {option.Value}");
                }
                Console.WriteLine();
            }
            
            // Hiển thị đáp án với nội dung đầy đủ
            if (!string.IsNullOrWhiteSpace(result.AnswerText))
            {
                Console.WriteLine($"Đáp án: {result.AnswerText}");
            }
            else if (!string.IsNullOrWhiteSpace(result.Answer))
            {
                // Nếu không có answerText, chỉ hiển thị A/B/C/D
                Console.WriteLine($"Đáp án: {result.Answer}");
            }
            else
            {
                // Fallback: hiển thị toàn bộ answer nếu không parse được JSON
                Console.WriteLine($"\nĐáp án:\n{answer}");
            }
            
            Console.WriteLine(new string('=', 80) + "\n");
        }
        catch
        {
            // Nếu parse JSON thất bại, hiển thị raw answer
            Console.WriteLine("\n" + new string('=', 80));
            Console.WriteLine("KẾT QUẢ XỬ LÝ");
            Console.WriteLine(new string('=', 80));
            Console.WriteLine($"\n[Lỗi parse JSON, hiển thị raw answer]\n");
            Console.WriteLine($"\nĐáp án:\n{answer}\n");
            Console.WriteLine(new string('=', 80) + "\n");
        }
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
            // Nếu parse JSON thất bại, thử extract đáp án đơn giản
            var answerMatch = Regex.Match(aiResponse, @"\b([A-D])\b", RegexOptions.IgnoreCase);
            var answer = answerMatch.Success ? answerMatch.Groups[1].Value.ToUpper() : "";
            return (null, null, "", null, answer, "");
        }

        return (null, null, "", null, "", "");
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
}

