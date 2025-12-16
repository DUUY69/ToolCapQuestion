using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CaptureRegionApp.Processing.Logging;

namespace CaptureRegionApp.Processing.Ocr;

/// <summary>
/// Gọi PaddleOCR qua script Python CLI.
/// Yêu cầu: đã cài paddleocr (pip install paddleocr) và có python trong PATH.
/// Script Python phải in ra JSON: { "text": "..." }
/// </summary>
public sealed class PaddleOcrService : IOcrService
{
    private readonly string _pythonPath;
    private readonly string _scriptPath;
    private readonly string _lang;
    private readonly bool _useAngleCls;
    private readonly AppLogger _logger;

    public PaddleOcrService(string pythonPath, string scriptPath, string lang, bool useAngleCls, AppLogger logger)
    {
        _pythonPath = string.IsNullOrWhiteSpace(pythonPath) ? "python" : pythonPath;
        _scriptPath = string.IsNullOrWhiteSpace(scriptPath) ? "paddle_ocr_cli.py" : scriptPath;
        _lang = string.IsNullOrWhiteSpace(lang) ? "en" : lang;
        _useAngleCls = useAngleCls;
        _logger = logger ?? AppLogger.Null;
    }

    public async Task<string> ExtractTextAsync(string imagePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException("Không tìm thấy ảnh để OCR", imagePath);
        }

        var psi = new ProcessStartInfo
        {
            FileName = _pythonPath,
            Arguments = $"\"{_scriptPath}\" \"{imagePath}\" --lang \"{_lang}\" --use-angle-cls {(_useAngleCls ? "1" : "0")}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var proc = new Process { StartInfo = psi };
        var output = new StringBuilder();
        var error = new StringBuilder();

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) output.AppendLine(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) error.AppendLine(e.Data);
        };

        if (!proc.Start())
        {
            throw new InvalidOperationException("Không thể khởi động tiến trình PaddleOCR.");
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (proc.ExitCode != 0)
        {
            var err = error.ToString();
            _logger.Log($"PaddleOCR lỗi: {err}");
            throw new InvalidOperationException($"PaddleOCR exit code {proc.ExitCode}: {err}");
        }

        var stdout = output.ToString();
        try
        {
            var doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.TryGetProperty("text", out var textProp))
            {
                return textProp.GetString() ?? string.Empty;
            }
        }
        catch (Exception ex)
        {
            _logger.Log($"PaddleOCR parse lỗi: {ex.Message}");
        }

        // Nếu không parse được JSON, trả về toàn bộ stdout
        return stdout.Trim();
    }
}

