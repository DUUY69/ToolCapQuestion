using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CaptureRegionApp.Processing.Logging;

namespace CaptureRegionApp.Processing.Video;

public sealed class VideoFrameExtractor
{
    private readonly string _ffmpegPath;
    private readonly int _intervalSeconds;
    private readonly string _tempDir;
    private readonly string _outputDir;
    private readonly AppLogger _logger;
    private readonly HashSet<string> _hashes = new(StringComparer.OrdinalIgnoreCase);

    public VideoFrameExtractor(string ffmpegPath, int intervalSeconds, string tempDir, string outputDir, AppLogger? logger = null)
    {
        if (intervalSeconds < 1 || intervalSeconds > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(intervalSeconds), "Interval must be 1-20 seconds.");
        }

        _ffmpegPath = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath;
        _intervalSeconds = intervalSeconds;
        _tempDir = tempDir;
        _outputDir = outputDir;
        _logger = logger ?? AppLogger.Null;
    }

    public async Task<int> ExtractAsync(string videoPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(videoPath))
        {
            throw new FileNotFoundException("Video file not found.", videoPath);
        }

        // Clean temp directory
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(_outputDir);

        var fpsFilter = $"fps=1/{_intervalSeconds}";
        var tempPattern = Path.Combine(_tempDir, "frame_%06d.png");

        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments = $"-i \"{videoPath}\" -vf {fpsFilter} -qscale:v 2 -y \"{tempPattern}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _logger.Log($"[Video] Running ffmpeg: {psi.Arguments}");

        using (var proc = Process.Start(psi) ?? throw new InvalidOperationException("Cannot start ffmpeg"))
        {
            var stdErr = await proc.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException($"ffmpeg failed (code {proc.ExitCode}): {stdErr}");
            }
        }

        var kept = 0;
        var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var files = Directory.GetFiles(_tempDir, "frame_*.png").OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsDuplicate(file))
            {
                TryDelete(file);
                continue;
            }

            var name = $"capture_video_{ts}_{kept + 1:000000}.png";
            var dest = Path.Combine(_outputDir, name);
            File.Move(file, dest, overwrite: true);
            kept++;
        }

        // Cleanup temp
        TryDeleteDirectory(_tempDir);

        _logger.Log($"[Video] Extracted {kept} frames (unique) to {_outputDir}");
        return kept;
    }

    private bool IsDuplicate(string filePath)
    {
        try
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hashBytes = sha.ComputeHash(stream);
            var hash = Convert.ToHexString(hashBytes);
            if (_hashes.Contains(hash))
            {
                return true;
            }
            _hashes.Add(hash);
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }
}

