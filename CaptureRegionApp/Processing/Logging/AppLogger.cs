using System;
using System.IO;

namespace CaptureRegionApp.Processing.Logging;

public sealed class AppLogger
{
    private readonly string _logFile;
    private readonly object _lock = new();

    private AppLogger(string logFile)
    {
        _logFile = logFile;
    }

    public static AppLogger Create(string directory)
    {
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, $"processing_{DateTime.Now:yyyyMMdd}.log");
        return new AppLogger(file);
    }

    public static AppLogger Null { get; } = new AppLogger(string.Empty);

    public void Log(string message)
    {
        if (string.IsNullOrWhiteSpace(_logFile))
        {
            return;
        }

        lock (_lock)
        {
            File.AppendAllText(_logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
    }
}

