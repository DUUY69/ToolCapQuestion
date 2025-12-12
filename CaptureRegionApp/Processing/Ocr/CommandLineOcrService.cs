using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CaptureRegionApp.Processing.Ocr;

public interface IOcrService
{
    Task<string> ExtractTextAsync(string imagePath, CancellationToken cancellationToken);
}

/// <summary>
/// Chạy OCR qua lệnh ngoài (mặc định: tesseract "{input}" stdout).
/// Để thay đổi lệnh, sửa OcrCommand trong processing-settings.json.
/// </summary>
public sealed class CommandLineOcrService : IOcrService
{
    private readonly string _commandTemplate;

    public CommandLineOcrService(string commandTemplate)
    {
        _commandTemplate = string.IsNullOrWhiteSpace(commandTemplate)
            ? "tesseract \"{input}\" stdout"
            : commandTemplate;
        
        // Kiểm tra và thay thế tesseract bằng đường dẫn đầy đủ nếu cần
        if (_commandTemplate.Contains("tesseract") && !_commandTemplate.Contains("\\"))
        {
            var tesseractPath = FindTesseractPath();
            if (!string.IsNullOrEmpty(tesseractPath))
            {
                // Escape đường dẫn có khoảng trắng bằng cách dùng dấu ngoặc kép và escape đúng cách
                // Thay "tesseract " bằng đường dẫn đầy đủ có escape
                _commandTemplate = _commandTemplate.Replace("tesseract ", $"\"{tesseractPath}\" ");
            }
        }
    }

    public async Task<string> ExtractTextAsync(string imagePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException("Không tìm thấy ảnh để OCR", imagePath);
        }

        var cmd = _commandTemplate.Replace("{input}", imagePath);
        
        // Nếu command chứa đường dẫn đầy đủ đến tesseract, chạy trực tiếp thay vì qua cmd.exe
        ProcessStartInfo startInfo;
        var tesseractPath = FindTesseractPath();
        if (!string.IsNullOrEmpty(tesseractPath) && cmd.Contains(tesseractPath))
        {
            // Extract tesseract path và arguments từ command
            // Format: "C:\Program Files\Tesseract-OCR\tesseract.exe" "image.png" stdout -l eng --psm 6
            // Hoặc: "C:\Program Files\Tesseract-OCR\tesseract.exe" "image.png" stdout
            var match = Regex.Match(cmd, @"^""([^""]+)""\s+""([^""]+)""\s+(.+)$");
            if (match.Success)
            {
                var exePath = match.Groups[1].Value;
                var imgPath = match.Groups[2].Value;
                var args = match.Groups[3].Value.Trim();
                
                startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = $"\"{imgPath}\" {args}",
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
            }
            else
            {
                // Thử parse format khác: tesseract "image.png" stdout -l eng --psm 6
                var match2 = Regex.Match(cmd, @"^""([^""]+)""\s+""([^""]+)""\s+(\w+)(.*)$");
                if (match2.Success)
                {
                    var exePath = match2.Groups[1].Value;
                    var imgPath = match2.Groups[2].Value;
                    var outputType = match2.Groups[3].Value;
                    var extraArgs = match2.Groups[4].Value.Trim();
                    
                    startInfo = new ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = $"\"{imgPath}\" {outputType} {extraArgs}".Trim(),
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    };
                }
                else
                {
                    startInfo = BuildProcessStart(cmd);
                }
            }
        }
        else
        {
            startInfo = BuildProcessStart(cmd);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var output = new StringBuilder();
        var error = new StringBuilder();

        var tcs = new TaskCompletionSource<int>();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                output.AppendLine(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                error.AppendLine(e.Data);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Không thể khởi động tiến trình OCR.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await using (cancellationToken.Register(() =>
               {
                   try { if (!process.HasExited) process.Kill(); }
                   catch { /* ignore */ }
               }))
        {
            process.Exited += (_, _) => tcs.TrySetResult(process.ExitCode);
            var exitCode = await tcs.Task.ConfigureAwait(false);

            if (exitCode != 0)
            {
                var message = error.Length > 0 ? error.ToString() : "OCR trả về mã lỗi.";
                throw new InvalidOperationException(message);
            }
        }

        return output.ToString().Trim();
    }

    private static ProcessStartInfo BuildProcessStart(string commandLine)
    {
        string fileName;
        string arguments;

        if (OperatingSystem.IsWindows())
        {
            // Escape đúng cách cho cmd.exe - dùng dấu ngoặc kép bên ngoài toàn bộ command
            fileName = "cmd.exe";
            // Đảm bảo các đường dẫn có khoảng trắng được escape đúng
            arguments = "/C \"" + commandLine.Replace("\"", "\"\"") + "\"";
        }
        else
        {
            fileName = "/bin/sh";
            arguments = "-c \"" + commandLine.Replace("\"", "\\\"") + "\"";
        }

        return new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
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
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
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

