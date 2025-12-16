using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CaptureRegionApp.Processing.Logging;

namespace CaptureRegionApp.Processing.Ocr;

/// <summary>
/// OCR qua Google Cloud Vision API (TEXT_DETECTION).
/// </summary>
public sealed class GoogleVisionOcrService : IOcrService
{
    private readonly string _apiKey;
    private readonly HttpClient _httpClient;
    private readonly AppLogger _logger;

    public GoogleVisionOcrService(string apiKey, AppLogger? logger = null, HttpClient? httpClient = null)
    {
        _apiKey = apiKey?.Trim() ?? string.Empty;
        _logger = logger ?? AppLogger.Null;
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<string> ExtractTextAsync(string imagePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new InvalidOperationException("Google Vision API key chưa được cấu hình.");
        }

        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException("Không tìm thấy ảnh để OCR", imagePath);
        }

        var imageBytes = await File.ReadAllBytesAsync(imagePath, cancellationToken).ConfigureAwait(false);
        var base64 = Convert.ToBase64String(imageBytes);

        var payload = new
        {
            requests = new[]
            {
                new
                {
                    image = new { content = base64 },
                    features = new[]
                    {
                        new { type = "TEXT_DETECTION", maxResults = 1 }
                    }
                }
            }
        };

        var url = $"https://vision.googleapis.com/v1/images:annotate?key={_apiKey}";
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.Log($"Google Vision lỗi: {(int)response.StatusCode} {response.ReasonPhrase} - {responseBody}");
            throw new InvalidOperationException($"Google Vision OCR lỗi: {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (!doc.RootElement.TryGetProperty("responses", out var responses) ||
                responses.ValueKind != JsonValueKind.Array ||
                responses.GetArrayLength() == 0)
            {
                return string.Empty;
            }

            var first = responses[0];
            if (first.TryGetProperty("fullTextAnnotation", out var full) &&
                full.TryGetProperty("text", out var fullText))
            {
                return fullText.GetString() ?? string.Empty;
            }

            if (first.TryGetProperty("textAnnotations", out var textAnnotations) &&
                textAnnotations.ValueKind == JsonValueKind.Array &&
                textAnnotations.GetArrayLength() > 0 &&
                textAnnotations[0].TryGetProperty("description", out var description))
            {
                return description.GetString() ?? string.Empty;
            }

            return string.Empty;
        }
        catch (JsonException ex)
        {
            _logger.Log($"Google Vision parse lỗi: {ex.Message}");
            return string.Empty;
        }
    }
}

