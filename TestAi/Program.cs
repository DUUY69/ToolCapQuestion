using System;
using System.Threading.Tasks;
using CaptureRegionApp.Processing;
using CaptureRegionApp.Processing.Ai;
using CaptureRegionApp.Processing.Logging;

namespace TestAi;

class Program
{
    static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("=== Kiểm tra chức năng AI ===\n");

        var settings = ProcessingSettings.Load();
        var logger = AppLogger.Create(settings.GetOutputDirectory());
        var aiClient = new AiClient(settings, logger: logger);

        // Kiểm tra cấu hình
        Console.WriteLine("Thông tin cấu hình:");
        var geminiKey = settings.GetGeminiApiKey();
        Console.WriteLine($"  Gemini API Key: {(string.IsNullOrWhiteSpace(geminiKey) ? "Chưa thiết lập" : "Đã thiết lập")}");
        Console.WriteLine($"  Gemini Model: {settings.GeminiModel}");
        Console.WriteLine($"  Ollama Endpoint: {settings.OllamaEndpoint}");
        Console.WriteLine($"  Ollama Model: {settings.OllamaModel}");
        Console.WriteLine();

        // Văn bản kiểm tra
        var testText = "Câu hỏi: 2 + 2 bằng bao nhiêu?\nTrả lời:";

        Console.WriteLine($"Văn bản kiểm tra: {testText}\n");

        // Kiểm tra Gemini
        if (!string.IsNullOrWhiteSpace(geminiKey))
        {
            Console.WriteLine("=== Kiểm tra Gemini ===");
            try
            {
                var geminiClient = new AiClient(settings, logger: logger);
                var answer = await geminiClient.GetAnswerAsync(testText, default);
                Console.WriteLine($"✓ Gemini phản hồi thành công!");
                Console.WriteLine($"\nNội dung trả lời:\n{answer}\n");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Kiểm tra Gemini thất bại: {ex.Message}");
                Console.WriteLine($"Chi tiết lỗi: {ex.GetType().Name}\n");
            }
        }
        else
        {
            Console.WriteLine("⚠ Gemini API Key chưa thiết lập, bỏ qua kiểm tra Gemini\n");
        }

        // Kiểm tra Ollama
        Console.WriteLine("=== Kiểm tra Ollama ===");
        try
        {
            // Tạm thời vô hiệu hóa Gemini để bắt buộc sử dụng Ollama
            var ollamaSettings = new ProcessingSettings
            {
                EnableAutoAnswer = settings.EnableAutoAnswer,
                PromptPrefix = settings.PromptPrefix,
                GeminiModel = settings.GeminiModel,
                GeminiApiKey = "", // Xóa Gemini key để bắt buộc dùng Ollama
                GeminiApiKeyEnv = settings.GeminiApiKeyEnv,
                OllamaEndpoint = settings.OllamaEndpoint,
                OllamaModel = settings.OllamaModel,
                OcrCommand = settings.OcrCommand,
                OutputDirectory = settings.OutputDirectory
            };
            var ollamaClient = new AiClient(ollamaSettings, logger: logger);
            var answer = await ollamaClient.GetAnswerAsync(testText, default);
            Console.WriteLine($"✓ Ollama phản hồi thành công!");
            Console.WriteLine($"\nNội dung trả lời:\n{answer}\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Kiểm tra Ollama thất bại: {ex.Message}");
            Console.WriteLine($"Chi tiết lỗi: {ex.GetType().Name}");
            if (ex.InnerException != null)
            {
                Console.WriteLine($"Lỗi bên trong: {ex.InnerException.Message}");
            }
            Console.WriteLine();
        }

        Console.WriteLine("\nHoàn tất kiểm tra!");
        
        // Thử đọc phím, nếu không được thì bỏ qua
        try
        {
            if (Console.IsInputRedirected == false)
            {
                Console.WriteLine("\nNhấn phím bất kỳ để thoát...");
                Console.ReadKey();
            }
        }
        catch
        {
            // Bỏ qua nếu không thể đọc phím
        }
    }
}

