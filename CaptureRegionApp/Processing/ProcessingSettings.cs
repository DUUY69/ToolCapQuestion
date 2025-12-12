using System;
using System.IO;
using System.Text.Json;

namespace CaptureRegionApp.Processing;

public sealed class ProcessingSettings
{
    public bool EnableAutoAnswer { get; init; } = true;
    public string PromptPrefix { get; init; } =
        "Bạn là trợ lý trả lời đề kiểm tra. Đọc kỹ nội dung OCR, trích xuất câu hỏi và trả lời ngắn gọn, rõ ràng. ";

    public string GeminiModel { get; init; } = "gemini-2.5-flash";
    public string GeminiApiKey { get; init; } = string.Empty;
    public string GeminiApiKeyEnv { get; init; } = "GEMINI_API_KEY";

    public string OllamaEndpoint { get; init; } = "http://localhost:11434/api/generate";
    public string OllamaModel { get; init; } = "qwen2.5:7b-instruct";

    public string OcrCommand { get; init; } = "tesseract \"{input}\" stdout";
    public string OutputDirectory { get; init; } = "Outputs";

    public static ProcessingSettings Load()
    {
        var baseDir = AppContext.BaseDirectory;
        var configPath = Path.Combine(baseDir, "Config", "processing-settings.json");

        if (!File.Exists(configPath))
        {
            return new ProcessingSettings();
        }

        try
        {
            var json = File.ReadAllText(configPath);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var loaded = JsonSerializer.Deserialize<ProcessingSettings>(json, options);
            return loaded ?? new ProcessingSettings();
        }
        catch
        {
            return new ProcessingSettings();
        }
    }

    public string GetGeminiApiKey()
    {
        if (!string.IsNullOrWhiteSpace(GeminiApiKey))
        {
            return GeminiApiKey;
        }

        if (!string.IsNullOrWhiteSpace(GeminiApiKeyEnv))
        {
            var env = Environment.GetEnvironmentVariable(GeminiApiKeyEnv);
            if (!string.IsNullOrWhiteSpace(env))
            {
                return env;
            }
        }

        return string.Empty;
    }

    public string GetOutputDirectory()
    {
        var baseDir = AppContext.BaseDirectory;
        var dir = string.IsNullOrWhiteSpace(OutputDirectory) ? "Outputs" : OutputDirectory;
        return Path.GetFullPath(Path.Combine(baseDir, dir));
    }
}

