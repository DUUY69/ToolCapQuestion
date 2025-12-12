using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using CaptureRegionApp.Processing;

namespace CaptureRegionApp;

internal static class Program
{
    // 2 = Per Monitor DPI Aware
    [DllImport("Shcore.dll", SetLastError = true)]
    private static extern int SetProcessDpiAwareness(int value);

    [STAThread]
    private static void Main()
    {
        var settings = CaptureSettings.Load();
        var processingSettings = ProcessingSettings.Load();
        var pipeline = new CapturePipeline(processingSettings);
        var watcher = new PendingWatcher(pipeline, settings.GetOutputDirectory(), processingSettings.GetOutputDirectory());
        watcher.Start();

        try
        {
            SetProcessDpiAwareness(2);
        }
        catch
        {
            // Ignore if the platform doesn't support setting DPI awareness here.
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new CaptureAppContext(settings, pipeline, watcher));
    }
}

public class CaptureForm : Form
{
    private readonly CaptureSettings _settings;
    private Point _startPoint;
    private Rectangle _selection;
    private bool _selecting;
    public Bitmap? CapturedImage { get; private set; }
    public Rectangle SelectedRegion => _selection;

    public CaptureForm(CaptureSettings settings)
    {
        _settings = settings;
        Text = "Screen Region Capture";
        DoubleBuffered = true;
        WindowState = FormWindowState.Maximized;
        FormBorderStyle = FormBorderStyle.None;
        Opacity = _settings.OverlayOpacity;
        BackColor = _settings.OverlayColor;
        TopMost = true;
        Cursor = Cursors.Cross;
        ShowInTaskbar = false;

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                Close();
            }
        };

        MouseDown += (_, e) =>
        {
            _selecting = true;
            _startPoint = e.Location;
            _selection = new Rectangle(e.Location, Size.Empty);
            Invalidate();
        };

        MouseMove += (_, e) =>
        {
            if (_selecting)
            {
                _selection = GetRect(_startPoint, e.Location);
                Invalidate();
            }
        };

        MouseUp += (_, e) =>
        {
            _selecting = false;
            _selection = GetRect(_startPoint, e.Location);
            if (_selection.Width > 2 && _selection.Height > 2)
            {
                CapturedImage = CaptureRegion(_selection);
            }

            Close();
        };

        Paint += (_, e) =>
        {
            if (_selection.Width > 0 && _selection.Height > 0)
            {
                using var pen = new Pen(_settings.SelectionLineColor, _settings.SelectionLineWidth);
                e.Graphics.DrawRectangle(pen, _selection);
            }
        };
    }

    private static Rectangle GetRect(Point p1, Point p2)
    {
        return new Rectangle(
            Math.Min(p1.X, p2.X),
            Math.Min(p1.Y, p2.Y),
            Math.Abs(p1.X - p2.X),
            Math.Abs(p1.Y - p2.Y));
    }

    private Bitmap CaptureRegion(Rectangle rect)
    {
        using var bmp = new Bitmap(rect.Width, rect.Height, _settings.PixelFormat);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(rect.Location, Point.Empty, rect.Size, CopyPixelOperation.SourceCopy);
        }

        return (Bitmap)bmp.Clone();
    }
}

public class CaptureAppContext : ApplicationContext
{
    private readonly CaptureSettings _settings;
    private readonly CapturePipeline? _pipeline;
    private readonly PendingWatcher? _watcher;
    private readonly NotifyIcon _trayIcon;
    private readonly HotkeyWindow _hotkeyWindow;
    private bool _isCapturing;

    public CaptureAppContext(CaptureSettings settings, CapturePipeline? pipeline, PendingWatcher? watcher)
    {
        _settings = settings;
        _pipeline = pipeline;
        _watcher = watcher;

        _trayIcon = new NotifyIcon
        {
            Text = "Capture Region App",
            Icon = SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };

        _trayIcon.DoubleClick += (_, _) => TriggerCapture();

        _hotkeyWindow = new HotkeyWindow(_settings);
        _hotkeyWindow.Register(new[]
        {
            (_settings.Hotkey, (Action)TriggerCapture),
            (_settings.FixedRegionHotkey, (Action)(() => TriggerFixedCapture(showPreview: false))),
            (_settings.FixedRegionConfirmHotkey, (Action)ConfigureFixedRegionAndRestart)
        });
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Chụp (Ctrl+Q)", null, (_, _) => TriggerCapture());
        menu.Items.Add("Chụp vùng cố định (Ctrl+W)", null, (_, _) => TriggerFixedCapture(showPreview: false));
        menu.Items.Add("Chọn vùng cố định mới & khởi động lại (Ctrl+E)", null, (_, _) => ConfigureFixedRegionAndRestart());
        menu.Items.Add("Thoát", null, (_, _) => ExitThread());
        return menu;
    }

    private void TriggerCapture()
    {
        if (_isCapturing)
        {
            return;
        }

        _isCapturing = true;
        try
        {
            while (true)
            {
                using var captureForm = new CaptureForm(_settings);
                captureForm.ShowDialog();

                if (captureForm.CapturedImage == null)
                {
                    break; // user cancelled the selection
                }

                using var preview = new PreviewForm(captureForm.CapturedImage);
                var result = preview.ShowDialog();

                if (result == DialogResult.Retry)
                {
                    continue; // capture again
                }

                if (result == DialogResult.OK)
                {
                    SaveCapturedImage(preview.ImageToSave ?? captureForm.CapturedImage);
                }

                break; // OK or Cancel closes loop
            }
        }
        finally
        {
            _isCapturing = false;
        }
    }

    private void TriggerFixedCapture(bool showPreview)
    {
        if (_isCapturing)
        {
            return;
        }

        _isCapturing = true;
        try
        {
            var rect = _settings.GetFixedRegionRect();
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                MessageBox.Show("Cấu hình vùng cố định chưa hợp lệ (Width/Height phải > 0).", "Capture", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using var bmp = CaptureFixedRegion(rect);

            if (showPreview)
            {
                using var preview = new PreviewForm(bmp);
                var result = preview.ShowDialog();
                if (result == DialogResult.OK)
                {
                    SaveCapturedImage(preview.ImageToSave ?? bmp);
                }
            }
            else
            {
                SaveCapturedImage(bmp);
            }
        }
        finally
        {
            _isCapturing = false;
        }
    }

    private Bitmap CaptureFixedRegion(Rectangle rect)
    {
        using var bmp = new Bitmap(rect.Width, rect.Height, _settings.PixelFormat);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(rect.Location, Point.Empty, rect.Size, CopyPixelOperation.SourceCopy);
        }

        return (Bitmap)bmp.Clone();
    }

    private void ConfigureFixedRegionAndRestart()
    {
        if (_isCapturing)
        {
            return;
        }

        _isCapturing = true;
        try
        {
            // Cho phép người dùng chọn lại vùng (overlay như Ctrl+Q)
            using var captureForm = new CaptureForm(_settings);
            captureForm.ShowDialog();

            if (captureForm.CapturedImage == null)
            {
                return; // user cancel
            }

            var rect = captureForm.SelectedRegion;
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                MessageBox.Show("Vùng cố định chưa hợp lệ (Width/Height phải > 0).", "Capture", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var updated = CloneSettingsWithRegion(rect);
            CaptureSettings.Save(updated);

            MessageBox.Show("Đã cập nhật vùng cố định. Ứng dụng sẽ khởi động lại để áp dụng cấu hình.", "Capture", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Application.Restart();
            ExitThread();
        }
        finally
        {
            _isCapturing = false;
        }
    }

    private CaptureSettings CloneSettingsWithRegion(Rectangle rect)
    {
        return new CaptureSettings
        {
            OverlayOpacity = _settings.OverlayOpacity,
            OverlayColorHex = _settings.OverlayColorHex,
            SelectionLineColorHex = _settings.SelectionLineColorHex,
            SelectionLineWidth = _settings.SelectionLineWidth,
            OutputDirectory = _settings.OutputDirectory,
            FileNameFormat = _settings.FileNameFormat,
            PixelFormatName = _settings.PixelFormatName,
            ShowSavedDialog = _settings.ShowSavedDialog,
            Hotkey = _settings.Hotkey,
            FixedRegionHotkey = _settings.FixedRegionHotkey,
            FixedRegionConfirmHotkey = _settings.FixedRegionConfirmHotkey,
            FixedRegionX = rect.X,
            FixedRegionY = rect.Y,
            FixedRegionWidth = rect.Width,
            FixedRegionHeight = rect.Height
        };
    }

    protected override void ExitThreadCore()
    {
        _hotkeyWindow.Unregister();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _watcher?.Dispose();
        _hotkeyWindow.DestroyHandle();
        base.ExitThreadCore();
    }

    private void SaveCapturedImage(Image image)
    {
        var outputDir = _settings.GetOutputDirectory();
        Directory.CreateDirectory(outputDir);
        var fileName = string.Format(_settings.FileNameFormat, DateTime.Now);
        var file = Path.Combine(outputDir, fileName);

        // Always save as PNG for lossless quality
        image.Save(file, ImageFormat.Png);

        // Không chặn UI: xử lý OCR + AI ở nền
        _ = _pipeline?.ProcessAsync(file);
    }
}

internal sealed class HotkeyWindow : NativeWindow
{
    private const int WM_HOTKEY = 0x0312;

    private readonly CaptureSettings _settings;
    private readonly List<HotkeyRegistration> _registrations = new();

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly record struct HotkeyRegistration(int Id, Action Action);

    public HotkeyWindow(CaptureSettings settings)
    {
        _settings = settings;
        CreateHandle(new CreateParams());
    }

    public void Register(IEnumerable<(string hotkey, Action action)> hotkeys)
    {
        foreach (var (hotkey, action) in hotkeys)
        {
            var id = HashCode.Combine(GetHashCode(), hotkey, _registrations.Count);
            var (mods, key) = HotkeyParser.Parse(hotkey);
            RegisterHotKey(Handle, id, mods, key);
            _registrations.Add(new HotkeyRegistration(id, action));
        }
    }

    public void Unregister()
    {
        foreach (var reg in _registrations)
        {
            UnregisterHotKey(Handle, reg.Id);
        }

        _registrations.Clear();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            var id = m.WParam.ToInt32();
            var reg = _registrations.Find(r => r.Id == id);
            reg.Action?.Invoke();
        }

        base.WndProc(ref m);
    }
}

internal sealed class PreviewForm : Form
{
    private readonly PictureBox _picture;
    public Image? ImageToSave { get; }

    public PreviewForm(Image image)
    {
        ImageToSave = image;
        Text = "Xem trước ảnh chụp";
        Width = Math.Min(1200, image.Width + 40);
        Height = Math.Min(800, image.Height + 100);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;

        _picture = new PictureBox
        {
            Dock = DockStyle.Fill,
            Image = image,
            SizeMode = PictureBoxSizeMode.Zoom,
            BorderStyle = BorderStyle.FixedSingle
        };

        var saveBtn = new Button { Text = "Lưu", DialogResult = DialogResult.OK, Width = 90 };
        var retryBtn = new Button { Text = "Chụp lại", DialogResult = DialogResult.Retry, Width = 90 };
        var cancelBtn = new Button { Text = "Hủy", DialogResult = DialogResult.Cancel, Width = 90 };

        var btnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 50,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10),
            AutoSize = true
        };

        btnPanel.Controls.AddRange(new Control[] { saveBtn, retryBtn, cancelBtn });

        Controls.Add(_picture);
        Controls.Add(btnPanel);

        AcceptButton = saveBtn;
        CancelButton = cancelBtn;
    }
}

internal static class HotkeyParser
{
    private static readonly Dictionary<string, uint> ModLookup = new(StringComparer.OrdinalIgnoreCase)
    {
        { "CTRL", 0x0002 },
        { "ALT", 0x0001 },
        { "SHIFT", 0x0004 },
        { "WIN", 0x0008 }
    };

    public static (uint modifiers, uint key) Parse(string? hotkey)
    {
        // Default to Ctrl+Q
        if (string.IsNullOrWhiteSpace(hotkey))
        {
            return (ModLookup["CTRL"], (uint)Keys.Q);
        }

        var parts = hotkey.Split(new[] { '+', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        uint mods = 0;
        Keys key = Keys.Q;

        foreach (var part in parts)
        {
            if (ModLookup.TryGetValue(part, out var mod))
            {
                mods |= mod;
            }
            else
            {
                if (Enum.TryParse(part, true, out Keys parsed))
                {
                    key = parsed;
                }
            }
        }

        return (mods == 0 ? ModLookup["CTRL"] : mods, (uint)key);
    }
}

public sealed class CaptureSettings
{
    public double OverlayOpacity { get; init; } = 0.2;
    public string OverlayColorHex { get; init; } = "#000000";
    public string SelectionLineColorHex { get; init; } = "#ff0000";
    public float SelectionLineWidth { get; init; } = 2f;
    public string? OutputDirectory { get; init; }
    public string FileNameFormat { get; init; } = "capture_{0:yyyyMMdd_HHmmss}.png";
    public string PixelFormatName { get; init; } = nameof(PixelFormat.Format32bppArgb);
    public bool ShowSavedDialog { get; init; } = true;
    public string Hotkey { get; init; } = "Ctrl+Q";
    public string FixedRegionHotkey { get; init; } = "Ctrl+W";
    public string FixedRegionConfirmHotkey { get; init; } = "Ctrl+E";
    public int FixedRegionX { get; init; } = 0;
    public int FixedRegionY { get; init; } = 0;
    public int FixedRegionWidth { get; init; } = 400;
    public int FixedRegionHeight { get; init; } = 200;

    public Color OverlayColor => ToColor(OverlayColorHex, Color.Black);
    public Color SelectionLineColor => ToColor(SelectionLineColorHex, Color.Red);
    public PixelFormat PixelFormat => ToPixelFormat(PixelFormatName, PixelFormat.Format32bppArgb);

    public static CaptureSettings Load()
    {
        var baseDir = AppContext.BaseDirectory;
        var configPath = Path.Combine(baseDir, "Config", "capture-settings.json");

        if (!File.Exists(configPath))
        {
            return new CaptureSettings();
        }

        try
        {
            var json = File.ReadAllText(configPath);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var loaded = JsonSerializer.Deserialize<CaptureSettings>(json, options);
            return loaded ?? new CaptureSettings();
        }
        catch
        {
            // Fallback to defaults if config is invalid.
            return new CaptureSettings();
        }
    }

    public string GetOutputDirectory()
    {
        if (!string.IsNullOrWhiteSpace(OutputDirectory))
        {
            // Nếu là đường dẫn tuyệt đối, dùng trực tiếp
            if (Path.IsPathRooted(OutputDirectory))
            {
                return Path.GetFullPath(OutputDirectory);
            }
            // Nếu là đường dẫn tương đối, kết hợp với BaseDirectory
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, OutputDirectory));
        }

        // Mặc định lưu vào thư mục "Captures" cạnh file thực thi
        return Path.Combine(AppContext.BaseDirectory, "Captures");
    }

    public Rectangle GetFixedRegionRect()
    {
        return new Rectangle(FixedRegionX, FixedRegionY, FixedRegionWidth, FixedRegionHeight);
    }

    public static void Save(CaptureSettings settings)
    {
        var baseDir = AppContext.BaseDirectory;
        var configDir = Path.Combine(baseDir, "Config");
        Directory.CreateDirectory(configDir);
        var configPath = Path.Combine(configDir, "capture-settings.json");

        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(settings, options);
        File.WriteAllText(configPath, json);
    }

    private static Color ToColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return fallback;
        }

        try
        {
            return ColorTranslator.FromHtml(hex);
        }
        catch
        {
            return fallback;
        }
    }

    private static PixelFormat ToPixelFormat(string? name, PixelFormat fallback)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        try
        {
            return Enum.Parse<PixelFormat>(name, true);
        }
        catch
        {
            return fallback;
        }
    }
}

