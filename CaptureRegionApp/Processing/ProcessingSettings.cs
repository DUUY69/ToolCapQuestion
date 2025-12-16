using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CaptureRegionApp.Processing;

public sealed class ProcessingSettings
{
    public bool EnableAutoAnswer { get; init; } = true;
    public string PromptPrefix { get; init; } =
        "Bạn là trợ lý trả lời đề kiểm tra. Đọc kỹ nội dung OCR, trích xuất câu hỏi và trả lời ngắn gọn, rõ ràng. ";

    public string GeminiModel { get; init; } = "gemini-2.5-flash";
    public List<string> GeminiModels { get; init; } = new();

    public string GeminiApiKey { get; init; } = string.Empty;
    public List<string> GeminiApiKeys { get; init; } = new();
    public string GeminiApiKeyEnv { get; init; } = "GEMINI_API_KEY";

    public string OllamaEndpoint { get; init; } = "http://localhost:11434/api/generate";
    public string OllamaModel { get; init; } = "qwen2.5:7b-instruct";

    public string OcrCommand { get; init; } = "tesseract \"{input}\" stdout";
    public string OcrProvider { get; init; } = "tesseract"; // tesseract | paddle | googlevision
    public string PaddlePythonPath { get; init; } = "python";
    public string PaddleScriptPath { get; init; } = "paddle_ocr_cli.py";
    public string PaddleLang { get; init; } = "en";
    public bool PaddleUseAngleCls { get; init; } = true;
    public string GoogleVisionApiKey { get; init; } = string.Empty;
    public string GoogleVisionApiKeyEnv { get; init; } = "GOOGLE_VISION_API_KEY";
    public string OutputDirectory { get; init; } = "Outputs";

    public static ProcessingSettings Load()
    {
        var baseDir = Program.GetProjectRoot();
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

    public IReadOnlyList<string> GetGeminiApiKeysOrdered()
    {
        var keys = new List<string>();

        if (GeminiApiKeys is { Count: > 0 })
        {
            keys.AddRange(GeminiApiKeys
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(GeminiApiKey))
        {
            keys.Add(GeminiApiKey.Trim());
        }

        if (!string.IsNullOrWhiteSpace(GeminiApiKeyEnv))
        {
            var env = Environment.GetEnvironmentVariable(GeminiApiKeyEnv);
            if (!string.IsNullOrWhiteSpace(env))
            {
                keys.Add(env.Trim());
            }
        }

        // Remove duplicates while preserving order
        var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return keys.Where(distinct.Add).ToList();
    }

    public IReadOnlyList<string> GetGeminiModelsOrdered()
    {
        var models = new List<string>();

        if (GeminiModels is { Count: > 0 })
        {
            models.AddRange(GeminiModels
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Select(m => m.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(GeminiModel))
        {
            models.Add(GeminiModel.Trim());
        }

        if (models.Count == 0)
        {
            models.Add("gemini-2.5-flash");
            models.Add("gemini-2.5-flash-lite");
        }

        var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return models.Where(distinct.Add).ToList();
    }

    public string GetGoogleVisionApiKey()
    {
        if (!string.IsNullOrWhiteSpace(GoogleVisionApiKey))
        {
            return GoogleVisionApiKey.Trim();
        }

        if (!string.IsNullOrWhiteSpace(GoogleVisionApiKeyEnv))
        {
            var env = Environment.GetEnvironmentVariable(GoogleVisionApiKeyEnv);
            if (!string.IsNullOrWhiteSpace(env))
            {
                return env.Trim();
            }
        }

        return string.Empty;
    }

    public string GetOutputDirectory()
    {
        var baseDir = Program.GetProjectRoot();
        var dir = string.IsNullOrWhiteSpace(OutputDirectory) ? "Outputs" : OutputDirectory;
        return Path.GetFullPath(Path.Combine(baseDir, dir));
    }

    public static void Save(ProcessingSettings settings)
    {
        var baseDir = Program.GetProjectRoot();
        var configDir = Path.Combine(baseDir, "Config");
        Directory.CreateDirectory(configDir);
        var configPath = Path.Combine(configDir, "processing-settings.json");

        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(settings, options);
        File.WriteAllText(configPath, json);
    }
}

