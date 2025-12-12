using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CaptureRegionApp.Processing;

/// <summary>
/// Định kỳ quét thư mục ảnh gốc, tìm PNG chưa có file kết quả JSON, đẩy vào pipeline.
/// Xử lý tuần tự - một file tại một thời điểm.
/// </summary>
public sealed class PendingWatcher : IDisposable
{
    private readonly CapturePipeline _pipeline;
    private readonly string _captureDir;
    private readonly string _resultDir;
    private readonly TimeSpan _interval;
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();
    private readonly SemaphoreSlim _processingSemaphore = new(1, 1); // Chỉ cho phép 1 file xử lý tại một thời điểm
    private Timer? _timer;
    private bool _disposed;

    public PendingWatcher(CapturePipeline pipeline, string captureDir, string resultDir, TimeSpan? interval = null)
    {
        _pipeline = pipeline;
        _captureDir = captureDir;
        _resultDir = resultDir;
        _interval = interval ?? TimeSpan.FromSeconds(5);
    }

    public void Start()
    {
        Directory.CreateDirectory(_captureDir);
        Directory.CreateDirectory(_resultDir);
        _timer = new Timer(async _ => await OnTickAsync().ConfigureAwait(false), null, TimeSpan.Zero, _interval);
    }

    private async Task OnTickAsync()
    {
        if (_disposed)
        {
            return;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(_captureDir, "*.png", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return;
        }

        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var resultJson = Path.Combine(_resultDir, $"{name}_result.json");
            var processedMarker = Path.Combine(_resultDir, $"{name}.processed");

            // Bỏ qua nếu đã có file JSON hoặc marker đã xử lý
            if (File.Exists(resultJson) || File.Exists(processedMarker))
            {
                continue;
            }

            if (!_inFlight.TryAdd(file, 0))
            {
                continue; // đã/đang xử lý
            }

            // Xử lý tuần tự - đợi semaphore để đảm bảo chỉ xử lý 1 file tại một thời điểm
            _ = Task.Run(async () =>
            {
                await _processingSemaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    await _pipeline.ProcessAsync(file).ConfigureAwait(false);
                }
                finally
                {
                    _inFlight.TryRemove(file, out _);
                    _processingSemaphore.Release();
                }
            });
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Dispose();
        _processingSemaphore?.Dispose();
    }
}

