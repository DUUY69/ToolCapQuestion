using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using CaptureRegionApp.Processing;
using CaptureRegionApp.Processing.Models;
using CaptureRegionApp.Processing.Video;

namespace CaptureRegionApp;

public sealed class ControlPanelForm : Form
{
    private readonly CaptureSettings _captureSettings;
    private readonly ProcessingSettings _processingSettings;

    // Tabs
    private readonly TabControl _tabs;

    // Kết quả
    private readonly ListView _resultsList;
    private readonly TextBox _questionBox;
    private readonly TextBox _answerBox;
    private readonly PictureBox _resultPreview;
    private readonly ContextMenuStrip _resultsMenu;
    private AnswerResult? _currentEditingResult; // Kết quả đang được chỉnh sửa
    private float _imageZoomLevel = 1.0f; // Mức zoom hiện tại (1.0 = 100%)
    private Image? _originalImage; // Lưu ảnh gốc để zoom


    // Config controls
    private readonly TextBox _capOutputDir;
    private readonly TextBox _capFileFormat;
    private readonly TextBox _capHotkey;
    private readonly TextBox _capFixedHotkey;
    private readonly TextBox _capConfirmHotkey;
    private readonly NumericUpDown _capX;
    private readonly NumericUpDown _capY;
    private readonly NumericUpDown _capW;
    private readonly NumericUpDown _capH;
    private readonly CheckBox _procEnableAuto;
    private readonly TextBox _procOutputDir;
    private readonly TextBox _procGemModels;
    private readonly TextBox _procGemKeys;
    private readonly TextBox _procGemKeyInput;
    private readonly Button _addGemKeyBtn;
    private readonly ComboBox _procOcrProvider;
    private readonly TextBox _procVisionKey;
    private readonly TextBox _procVisionKeyEnv;
    private readonly TextBox _procOcrCmd;
    private readonly TextBox _procOllamaEndpoint;
    private readonly TextBox _procOllamaModel;
    private readonly TextBox _procPrompt;
    private readonly Button _saveConfigBtn;

    private readonly string _capturesDir;
    private readonly string _outputsDir;
    private readonly string _videoCapturesDir;
    // Video
    private TextBox? _videoPathBox;
    private NumericUpDown? _videoInterval;
    private Button? _videoBrowseBtn;
    private Button? _videoRunBtn;
    private Label? _videoStatus;
    private TextBox? _videoFfmpegPath;

    public ControlPanelForm()
    {
        Text = "Bảng điều khiển";
        Width = 1200;
        Height = 800;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1000, 700);

        _captureSettings = CaptureSettings.Load();
        _processingSettings = ProcessingSettings.Load();
        _capturesDir = _captureSettings.GetOutputDirectory();
        _outputsDir = _processingSettings.GetOutputDirectory();
        _videoCapturesDir = Path.Combine(Program.GetProjectRoot(), "VideoCaptures");

        _tabs = new TabControl { Dock = DockStyle.Fill };

        // Tab kết quả
        _resultsList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true
        };
        _resultsList.Columns.Add("#", 50, HorizontalAlignment.Center);
        _resultsList.Columns.Add("Thời gian", 140);
        _resultsList.Columns.Add("Ảnh", 160);
        _resultsList.Columns.Add("Mã/Câu số", 140);
        _resultsList.Columns.Add("Câu hỏi (tóm tắt)", 220);
        _resultsList.Columns.Add("Đáp án", 200);
        _resultsList.Columns.Add("Action", 70, HorizontalAlignment.Center);
        _resultsList.SelectedIndexChanged += (_, _) => ShowResultDetails();
        _resultsList.MouseDown += ResultsListOnMouseDown;
        EnableListViewDoubleBuffer(_resultsList);

        _questionBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            ReadOnly = false, // Cho phép chỉnh sửa
            Font = new Font(Font.FontFamily, 10, FontStyle.Regular)
        };
        _answerBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            ReadOnly = false, // Cho phép chỉnh sửa
            Font = new Font(Font.FontFamily, 10, FontStyle.Bold)
        };

        _resultPreview = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.Black
        };
        
        // Cho phép PictureBox nhận focus để nhận MouseWheel
        _resultPreview.MouseEnter += (sender, e) => _resultPreview.Focus();
        
        // Hỗ trợ zoom bằng scroll wheel
        _resultPreview.MouseWheel += (sender, e) =>
        {
            if (e.Delta > 0)
            {
                ZoomImage(1.1f);
            }
            else
            {
                ZoomImage(0.9f);
            }
        };
        
        // Panel chứa ảnh và nút zoom
        var imagePanel = new Panel { Dock = DockStyle.Fill };
        imagePanel.Controls.Add(_resultPreview);
        
        // Nút zoom
        var zoomPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 35,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(5),
            BackColor = Color.LightGray
        };
        
        var zoomInBtn = new Button
        {
            Text = "🔍+",
            Width = 50,
            Height = 28,
            Margin = new Padding(2)
        };
        zoomInBtn.Click += (_, _) => ZoomImage(1.2f);
        
        var zoomOutBtn = new Button
        {
            Text = "🔍-",
            Width = 50,
            Height = 28,
            Margin = new Padding(2)
        };
        zoomOutBtn.Click += (_, _) => ZoomImage(0.8f);
        
        var zoomResetBtn = new Button
        {
            Text = "⟲ 100%",
            Width = 70,
            Height = 28,
            Margin = new Padding(2)
        };
        zoomResetBtn.Click += (_, _) => ResetZoom();
        
        zoomPanel.Controls.Add(zoomInBtn);
        zoomPanel.Controls.Add(zoomOutBtn);
        zoomPanel.Controls.Add(zoomResetBtn);
        
        var imageContainer = new Panel { Dock = DockStyle.Fill };
        imageContainer.Controls.Add(_resultPreview);
        imageContainer.Controls.Add(zoomPanel);

        // Layout chi tiết: Ảnh bên trái, câu hỏi/đáp án bên phải
        var detailSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            Panel1MinSize = 0,
            Panel2MinSize = 0,
            SplitterWidth = 6,
            IsSplitterFixed = false
        };
        detailSplit.Panel1.Controls.Add(imageContainer);

        var detailLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 5,
            Padding = new Padding(6),
            AutoSize = false
        };
        detailLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        detailLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        detailLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // label câu hỏi + lựa chọn
        detailLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 60));  // box câu hỏi + lựa chọn
        detailLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // label đáp án
        detailLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 40));  // box đáp án
        detailLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // nút Lưu

        detailLayout.Controls.Add(new Label { Text = "Câu hỏi + Lựa chọn (kèm Mã/Câu số nếu có)", AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, 0, 0);
        detailLayout.SetColumnSpan(detailLayout.Controls[detailLayout.Controls.Count - 1], 2);
        detailLayout.Controls.Add(_questionBox, 0, 1);
        detailLayout.SetColumnSpan(_questionBox, 2);
        detailLayout.Controls.Add(new Label { Text = "Đáp án", AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, 0, 2);
        detailLayout.SetColumnSpan(detailLayout.Controls[detailLayout.Controls.Count - 1], 2);
        detailLayout.Controls.Add(_answerBox, 0, 3);
        detailLayout.SetColumnSpan(_answerBox, 2);

        // Nút Lưu và Xem log ở hàng cuối
        var saveBtn = new Button
        {
            Text = "💾 Lưu",
            Width = 100,
            Height = 32
        };
        saveBtn.Click += (_, _) => SaveCurrentResult();
        
        var viewLogBtn = new Button
        {
            Text = "📋 Xem log",
            Width = 100,
            Height = 32,
            Margin = new Padding(0, 0, 8, 0)
        };
        viewLogBtn.Click += (_, _) => OpenLatestLog();
        
        // Panel chứa 2 nút
        var buttonPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Dock = DockStyle.Fill
        };
        buttonPanel.Controls.Add(saveBtn);
        buttonPanel.Controls.Add(viewLogBtn);
        
        detailLayout.Controls.Add(buttonPanel, 0, 4);
        detailLayout.SetColumnSpan(buttonPanel, 2);

        detailSplit.Panel2.Controls.Add(detailLayout);

        _resultsMenu = BuildResultsMenu();

        var resultPanel = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            Panel1MinSize = 0,
            Panel2MinSize = 0,
            SplitterWidth = 6,
            IsSplitterFixed = false
        };
        resultPanel.Panel1.Controls.Add(_resultsList);
        resultPanel.Panel2.Controls.Add(detailSplit);

        var resultContainer = new Panel { Dock = DockStyle.Fill };
        resultContainer.Controls.Add(resultPanel);

        // Thanh action riêng: chỉ có nút Xuất TXT (tất cả)
        var actionBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 40,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 8, 0, 0)
        };
        var exportAllBtn = new Button
        {
            Text = "Xuất TXT (tất cả)",
            Width = 140,
            Height = 28
        };
        exportAllBtn.Click += (_, _) => ExportResultsToTxt();
        actionBar.Controls.Add(exportAllBtn);

        var tabResults = new TabPage("Kết quả") { Padding = new Padding(8) };
        tabResults.Controls.Add(resultContainer);
        tabResults.Controls.Add(actionBar);

        // Tab cấu hình
        (_capOutputDir, _capFileFormat, _capHotkey, _capFixedHotkey, _capConfirmHotkey, _capX, _capY, _capW, _capH,
            _procEnableAuto, _procOutputDir, _procGemModels, _procGemKeys, _procGemKeyInput, _addGemKeyBtn,
            _procOcrProvider, _procVisionKey, _procVisionKeyEnv, _procOcrCmd, _procOllamaEndpoint, _procOllamaModel, _procPrompt, _saveConfigBtn)
            = BuildConfigControls();

        var tabVideo = new TabPage("Video") { Padding = new Padding(8) };
        tabVideo.Controls.Add(BuildVideoLayout());

        var tabConfig = new TabPage("Cấu hình") { Padding = new Padding(8) };
        tabConfig.Controls.Add(BuildConfigLayout());

        _tabs.TabPages.Add(tabResults);
        _tabs.TabPages.Add(tabVideo);
        _tabs.TabPages.Add(tabConfig);

        Controls.Add(_tabs);

        Load += (_, _) =>
        {
            LoadResults();
            LoadConfigToUI();
            // Đặt SplitterDistance sau khi form đã render kích thước
            BeginInvoke((Action)(() =>
            {
                // Tỷ lệ: Bảng 3, Ảnh 4, Câu hỏi/đáp án 3 (tổng 10)
                SafeSplit(resultPanel, 0.3); // Bảng = 3/10 = 30%
                // Set tỷ lệ cho detailSplit: Ảnh 4/7, Câu hỏi/đáp án 3/7
                if (resultPanel.Panel2.Controls.Count > 0 && resultPanel.Panel2.Controls[0] is SplitContainer detailSplit)
                {
                    SafeSplit(detailSplit, 4.0 / 7.0); // Ảnh = 4/7 ≈ 57.1%
                }
                AutoSizeResultColumns();
                if (_videoPathBox != null) _videoPathBox.Text = string.Empty;
                if (_videoInterval != null) _videoInterval.Value = 2;
                SetVideoStatus("Chọn video và nhấn Trích frame");
            }));
        };

        FormClosed += (_, _) =>
        {
            ResultBus.ResultAdded -= OnResultAdded;
            _originalImage?.Dispose();
            _originalImage = null;
        };

        ResultBus.ResultAdded += OnResultAdded;
    }

    private static void SafeSplit(SplitContainer sc, double ratio = 0.35)
    {
        try
        {
            // Lấy kích thước panel thực tế đã layout
            var width = sc.ClientSize.Width;
            if (width <= 0) return;

            var min1 = Math.Max(sc.Panel1MinSize, 0);
            var min2 = Math.Max(sc.Panel2MinSize, 0);
            var max = width - min2;
            if (max <= 0) return;

            // Đặt mặc định theo ratio (0-1), nhưng người dùng có thể kéo full do min=0
            ratio = Math.Clamp(ratio, 0.05, 0.95);
            var target = Math.Max(0, Math.Min(width - 1, (int)(width * ratio)));
            sc.SplitterDistance = target;
        }
        catch
        {
            // ignore sizing errors
        }
    }

    private void OnResultAdded(object? sender, AnswerResult e)
    {
        if (IsDisposed) return;
        BeginInvoke((Action)(() =>
        {
            AddResultItem(e);
        }));
    }

    private void LoadResults()
    {
        Directory.CreateDirectory(_outputsDir);
        var files = Directory.GetFiles(_outputsDir, "*_result.json", SearchOption.TopDirectoryOnly)
            .OrderByDescending(f => f)
            .Take(200)
            .ToList();

        _resultsList.BeginUpdate();
        _resultsList.Items.Clear();

        // Dùng để deduplication: mỗi ảnh chỉ có một JSON, mỗi nội dung chỉ có một kết quả
        var seenImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // Đảm bảo mỗi ảnh chỉ có một JSON
        var seenContent = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // Đảm bảo không trùng nội dung

        int idx = 1;
        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                var r = JsonSerializer.Deserialize<AnswerResult>(json);
                if (r != null)
                {
                    // Tìm ảnh dựa trên base name của JSON file (đảm bảo khớp chính xác)
                    // JSON: {name}_result.json → Ảnh: {name}.png
                    var jsonBaseName = Path.GetFileNameWithoutExtension(file).Replace("_result", "");
                    var imageName = $"{jsonBaseName}.png";
                    var path1 = Path.Combine(_capturesDir, imageName);
                    var path2 = Path.Combine(_videoCapturesDir, imageName);
                    
                    string? actualImagePath = null;
                    if (File.Exists(path1))
                    {
                        actualImagePath = path1;
                    }
                    else if (File.Exists(path2))
                    {
                        actualImagePath = path2;
                    }
                    else if (!string.IsNullOrWhiteSpace(r.ImagePath) && File.Exists(r.ImagePath))
                    {
                        // Fallback: dùng ImagePath từ JSON nếu còn tồn tại
                        actualImagePath = r.ImagePath;
                    }
                    
                    // Chỉ xử lý nếu ảnh tồn tại
                    if (actualImagePath != null)
                    {
                        // Check 1: Mỗi ảnh chỉ có một JSON (nếu đã có JSON cho ảnh này, bỏ qua JSON cũ hơn)
                        var imageKey = Path.GetFileName(actualImagePath).ToLowerInvariant();
                        if (seenImages.Contains(imageKey))
                        {
                            // Đã có JSON cho ảnh này → xóa JSON trùng (giữ cái mới hơn)
                            try
                            {
                                File.Delete(file);
                                var markerFile = Path.Combine(_outputsDir, $"{jsonBaseName}.processed");
                                if (File.Exists(markerFile))
                                {
                                    File.Delete(markerFile);
                                }
                            }
                            catch
                            {
                                // ignore
                            }
                            continue;
                        }

                        // Check 2: Deduplication theo nội dung (question + options + answer)
                        var contentKey = BuildContentKey(r);
                        if (seenContent.Contains(contentKey))
                        {
                            // Nội dung trùng → xóa JSON và ảnh
                            try
                            {
                                File.Delete(file);
                                var markerFile = Path.Combine(_outputsDir, $"{jsonBaseName}.processed");
                                if (File.Exists(markerFile))
                                {
                                    File.Delete(markerFile);
                                }
                                // Xóa ảnh trùng
                                TryDelete(actualImagePath);
                            }
                            catch
                            {
                                // ignore
                            }
                            continue;
                        }

                        // Cập nhật lại ImagePath và FileName để đảm bảo đồng bộ
                        r.ImagePath = actualImagePath;
                        r.FileName = Path.GetFileName(actualImagePath);
                        
                        // Đánh dấu đã thấy
                        seenImages.Add(imageKey);
                        seenContent.Add(contentKey);
                        
                        AddResultItem(r, idx++);
                    }
                    else
                    {
                        // Ảnh không tồn tại → có thể bị xóa do dedup, xóa JSON để đồng bộ
                        try
                        {
                            File.Delete(file);
                            var markerFile = Path.Combine(_outputsDir, $"{jsonBaseName}.processed");
                            if (File.Exists(markerFile))
                            {
                                File.Delete(markerFile);
                            }
                        }
                        catch
                        {
                            // ignore
                        }
                    }
                }
            }
            catch
            {
                // ignore
            }
        }

        _resultsList.EndUpdate();

        // Cập nhật lại STT cho tất cả items để đảm bảo đồng bộ
        for (int i = 0; i < _resultsList.Items.Count; i++)
        {
            _resultsList.Items[i].SubItems[0].Text = (i + 1).ToString();
        }

        // Auto-size cột dựa trên nội dung
        AutoSizeResultColumns();
    }

    // Tạo key để check trùng nội dung (giống logic trong CapturePipeline)
    private string BuildContentKey(AnswerResult r)
    {
        var parts = new List<string>
        {
            (r.Question ?? string.Empty).Trim()
        };

        if (r.Options != null && r.Options.Count > 0)
        {
            foreach (var kv in r.Options.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                parts.Add($"{kv.Key}:{kv.Value}".Trim());
            }
        }

        var ansText = !string.IsNullOrWhiteSpace(r.AnswerText)
            ? r.AnswerText
            : r.Answer;
        parts.Add((ansText ?? string.Empty).Trim());

        var key = string.Join("|", parts)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        key = System.Text.RegularExpressions.Regex.Replace(key, @"\s+", " ");
        return key;
    }

    private void ExportResultsToTxt()
    {
        try
        {
            if (_resultsList.Items.Count == 0)
            {
                LoadResults();
            }

            if (_resultsList.Items.Count == 0)
            {
                MessageBox.Show("Chưa có kết quả nào để xuất.", "Xuất TXT", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var sfd = new SaveFileDialog
            {
                Filter = "Text Files (*.txt)|*.txt|All Files (*.*)|*.*",
                FileName = "results.txt",
                InitialDirectory = Directory.Exists(_outputsDir) ? _outputsDir : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };

            if (sfd.ShowDialog() != DialogResult.OK)
            {
                return;
            }

            var lines = new List<string>();
            int stt = 1;
            foreach (ListViewItem item in _resultsList.Items)
            {
                if (item.Tag is not AnswerResult r) continue;

                // Thêm số thứ tự
                var meta = BuildMeta(r);
                lines.Add($"Câu {stt}: {meta}");
                
                if (!string.IsNullOrWhiteSpace(r.Question))
                    lines.Add($"Câu hỏi: {r.Question}");

                var opts = FormatOptions(r.Options);
                if (!string.IsNullOrWhiteSpace(opts))
                {
                    lines.Add("Lựa chọn:");
                    lines.Add(opts);
                }

                var ansText = !string.IsNullOrWhiteSpace(r.AnswerText) ? r.AnswerText : r.Answer;
                if (!string.IsNullOrWhiteSpace(ansText))
                    lines.Add($"Đáp án: {ansText}");

                lines.Add(string.Empty);
                stt++;
            }

            File.WriteAllLines(sfd.FileName, lines);
            MessageBox.Show($"Đã xuất ra: {sfd.FileName}", "Xuất TXT", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Xuất TXT lỗi: {ex.Message}", "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
    private ContextMenuStrip BuildResultsMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Xuất TXT (tất cả)", null, (_, _) => ExportResultsToTxt());
        menu.Items.Add("Xóa kết quả đã chọn", null, (_, _) => DeleteSelectedResults());
        menu.Items.Add("Mở thư mục Outputs", null, (_, _) => OpenFolderSafe(_outputsDir));
        return menu;
    }


    private void DeleteSelectedResults()
    {
        try
        {
            if (_resultsList.SelectedItems.Count == 0)
            {
                MessageBox.Show("Chọn ít nhất một kết quả để xóa.", "Xóa kết quả", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (MessageBox.Show("Bạn có chắc muốn xóa các kết quả đã chọn?", "Xóa kết quả", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return;
            }

            foreach (ListViewItem item in _resultsList.SelectedItems)
            {
                if (item.Tag is not AnswerResult r) continue;

                var baseName = Path.GetFileNameWithoutExtension(r.FileName);
                var jsonPath = Path.Combine(_outputsDir, $"{baseName}_result.json");
                var markerPath = Path.Combine(_outputsDir, $"{baseName}.processed");

                TryDelete(jsonPath);
                TryDelete(markerPath);
                item.Remove();
            }

            // Cập nhật lại STT cho tất cả items còn lại
            for (int i = 0; i < _resultsList.Items.Count; i++)
            {
                _resultsList.Items[i].SubItems[0].Text = (i + 1).ToString();
            }

            _questionBox.Clear();
            _answerBox.Clear();
            _resultPreview.Image = null;
            _originalImage?.Dispose();
            _originalImage = null;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Xóa kết quả lỗi: {ex.Message}", "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }


    private void AddResultItem(AnswerResult r, int? index = null)
    {
        var key = r.FileName;
        foreach (ListViewItem item in _resultsList.Items)
        {
            if (item.Name == key)
            {
                UpdateResultItem(item, r, index);
                return;
            }
        }

        _resultsList.Items.Add(CreateResultItem(r, index));
    }

    private ListViewItem CreateResultItem(AnswerResult r, int? index)
    {
        // Đảm bảo STT luôn có giá trị
        var stt = index.HasValue ? index.Value.ToString() : (_resultsList.Items.Count + 1).ToString();
        var item = new ListViewItem(new[]
        {
            stt,
            r.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            r.FileName,
            BuildMeta(r),
            TrimForView(r.Question, 80),
            TrimForView(string.IsNullOrWhiteSpace(r.AnswerText) ? r.Answer : r.AnswerText, 60),
            "⋯"
        })
        {
            Name = r.FileName,
            Tag = r
        };
        return item;
    }

    private void UpdateResultItem(ListViewItem item, AnswerResult r, int? index = null)
    {
        // Đảm bảo STT luôn có giá trị
        if (index.HasValue)
        {
            item.SubItems[0].Text = index.Value.ToString();
        }
        else if (string.IsNullOrWhiteSpace(item.SubItems[0].Text))
        {
            // Nếu STT rỗng, dùng vị trí trong list + 1
            item.SubItems[0].Text = (_resultsList.Items.IndexOf(item) + 1).ToString();
        }
        item.SubItems[1].Text = r.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");
        item.SubItems[2].Text = r.FileName;
        item.SubItems[3].Text = BuildMeta(r);
        item.SubItems[4].Text = TrimForView(r.Question, 80);
        item.SubItems[5].Text = TrimForView(string.IsNullOrWhiteSpace(r.AnswerText) ? r.Answer : r.AnswerText, 60);
        item.SubItems[6].Text = "⋯";
        item.Tag = r;
    }

    private void ShowResultDetails()
    {
        if (_resultsList.SelectedItems.Count == 0)
        {
            _questionBox.Clear();
            _answerBox.Clear();
            _resultPreview.Image = null;
            _originalImage?.Dispose();
            _originalImage = null;
            _currentEditingResult = null;
            return;
        }

        var r = _resultsList.SelectedItems[0].Tag as AnswerResult;
        if (r == null)
        {
            _currentEditingResult = null;
            return;
        }

        _currentEditingResult = r; // Lưu để sau này save

        // Load ảnh - tìm dựa trên FileName hoặc ImagePath (đảm bảo khớp chính xác)
        string? imagePath = null;
        
        // Ưu tiên dùng ImagePath nếu có và tồn tại
        if (!string.IsNullOrWhiteSpace(r.ImagePath) && File.Exists(r.ImagePath))
        {
            imagePath = r.ImagePath;
        }
        else if (!string.IsNullOrWhiteSpace(r.FileName))
        {
            // Tìm ảnh theo tên file trong cả 2 thư mục
            var fileName = r.FileName;
            var path1 = Path.Combine(_capturesDir, fileName);
            var path2 = Path.Combine(_videoCapturesDir, fileName);
            
            if (File.Exists(path1))
            {
                imagePath = path1;
            }
            else if (File.Exists(path2))
            {
                imagePath = path2;
            }
        }
        
        // Nếu vẫn không tìm thấy, thử tìm dựa trên base name từ JSON (fallback)
        if (imagePath == null && _currentEditingResult != null)
        {
            // Tìm JSON file tương ứng để lấy base name
            var jsonBaseName = Path.GetFileNameWithoutExtension(_currentEditingResult.FileName ?? "");
            if (!string.IsNullOrWhiteSpace(jsonBaseName))
            {
                var imageName = $"{jsonBaseName}.png";
                var path1 = Path.Combine(_capturesDir, imageName);
                var path2 = Path.Combine(_videoCapturesDir, imageName);
                
                if (File.Exists(path1))
                {
                    imagePath = path1;
                }
                else if (File.Exists(path2))
                {
                    imagePath = path2;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
        {
            try
            {
                using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                // Dispose ảnh cũ nếu có
                _originalImage?.Dispose();
                _originalImage = Image.FromStream(stream);
                ResetZoom(); // Reset zoom khi load ảnh mới
            }
            catch (Exception ex)
            {
                _resultPreview.Image = null;
                _originalImage?.Dispose();
                _originalImage = null;
                System.Diagnostics.Debug.WriteLine($"Lỗi load ảnh: {ex.Message}");
            }
        }
        else
        {
            _resultPreview.Image = null;
            _originalImage?.Dispose();
            _originalImage = null;
        }

        // Load nội dung
        var opts = FormatOptions(r.Options);
        _questionBox.Text = string.IsNullOrWhiteSpace(opts)
            ? r.Question
            : $"{r.Question}{Environment.NewLine}{Environment.NewLine}{opts}";
        _answerBox.Text = string.IsNullOrWhiteSpace(r.AnswerText) ? r.Answer : r.AnswerText;
    }

    private void SaveCurrentResult()
    {
        if (_currentEditingResult == null)
        {
            MessageBox.Show("Chưa chọn kết quả nào để lưu.", "Lưu", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            // Parse lại từ text box
            var questionText = _questionBox.Text.Trim();
            var answerText = _answerBox.Text.Trim();

            // Tách câu hỏi và lựa chọn (nếu có)
            string question;
            Dictionary<string, string>? options = null;

            // Tìm phần lựa chọn (A., B., C., D., ...)
            var lines = questionText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var questionLines = new List<string>();
            var optionLines = new List<string>();

            bool foundOptions = false;
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                // Nếu bắt đầu bằng chữ cái + dấu chấm (A., B., C., ...)
                if (trimmed.Length >= 2 && char.IsLetter(trimmed[0]) && trimmed[1] == '.' && char.IsUpper(trimmed[0]))
                {
                    foundOptions = true;
                    optionLines.Add(trimmed);
                }
                else if (!foundOptions)
                {
                    questionLines.Add(trimmed);
                }
                else
                {
                    // Nếu đã bắt đầu options, tiếp tục thêm vào options
                    optionLines.Add(trimmed);
                }
            }

            question = string.Join(Environment.NewLine, questionLines).Trim();
            if (optionLines.Count > 0)
            {
                options = new Dictionary<string, string>();
                foreach (var optLine in optionLines)
                {
                    var parts = optLine.Split(new[] { '.' }, 2, StringSplitOptions.None);
                    if (parts.Length == 2 && parts[0].Length == 1 && char.IsLetter(parts[0][0]))
                    {
                        var key = parts[0].Trim().ToUpper();
                        var value = parts[1].Trim();
                        options[key] = value;
                    }
                }
            }

            // Cập nhật AnswerResult
            _currentEditingResult.Question = question;
            _currentEditingResult.Options = options;
            _currentEditingResult.AnswerText = answerText;
            
            // Tách đáp án nếu có format "A. ..." hoặc chỉ "A"
            if (!string.IsNullOrWhiteSpace(answerText))
            {
                var answerMatch = Regex.Match(answerText, @"^([A-Z])[\.\s]");
                if (answerMatch.Success)
                {
                    _currentEditingResult.Answer = answerMatch.Groups[1].Value;
                }
                else if (answerText.Length == 1 && char.IsLetter(answerText[0]))
                {
                    _currentEditingResult.Answer = answerText.ToUpper();
                }
            }

            // Lưu vào JSON
            var jsonPath = Path.Combine(_outputsDir, $"{Path.GetFileNameWithoutExtension(_currentEditingResult.FileName)}_result.json");
            var json = JsonSerializer.Serialize(_currentEditingResult, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(jsonPath, json);

            // Cập nhật lại item trong list
            var item = _resultsList.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Name == _currentEditingResult.FileName);
            if (item != null)
            {
                UpdateResultItem(item, _currentEditingResult);
            }

            MessageBox.Show("Đã lưu thành công!", "Lưu", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Lưu lỗi: {ex.Message}", "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ResultsListOnMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var hit = _resultsList.HitTest(e.Location);
        if (hit.Item == null) return;
        hit.Item.Selected = true;
        if (hit.Item.SubItems.Count > 0 && hit.Item.SubItems[^1].Bounds.Contains(e.Location))
        {
            _resultsMenu.Show(_resultsList, e.Location);
        }
    }


    private (TextBox capOut, TextBox capFmt, TextBox hotkey, TextBox fixedHotkey, TextBox confirmHotkey,
        NumericUpDown x, NumericUpDown y, NumericUpDown w, NumericUpDown h,
        CheckBox autoAns, TextBox procOut, TextBox gemModels, TextBox gemKeys, TextBox gemKeyInput, Button addGemKeyBtn,
        ComboBox ocrProvider, TextBox visionKey, TextBox visionKeyEnv, TextBox ocrCmd, TextBox ollamaEp, TextBox ollamaModel, TextBox prompt, Button saveBtn)
        BuildConfigControls()
    {
        var capOut = new TextBox { Width = 300 };
        var capFmt = new TextBox { Width = 300 };
        var hotkey = new TextBox { Width = 120 };
        var fixedHotkey = new TextBox { Width = 120 };
        var confirmHotkey = new TextBox { Width = 120 };

        var x = new NumericUpDown { Maximum = 10000, Minimum = -10000 };
        var y = new NumericUpDown { Maximum = 10000, Minimum = -10000 };
        var w = new NumericUpDown { Maximum = 10000, Minimum = 0 };
        var h = new NumericUpDown { Maximum = 10000, Minimum = 0 };

        var autoAns = new CheckBox { Text = "EnableAutoAnswer" };
        var procOut = new TextBox { Width = 300 };
        var gemModels = new TextBox { Width = 300, Multiline = true, Height = 60, ScrollBars = ScrollBars.Vertical };
        var gemKeys = new TextBox { Width = 300, Multiline = true, Height = 80, ScrollBars = ScrollBars.Vertical };
        var gemKeyInput = new TextBox { Width = 240, Height = 28, PlaceholderText = "Nhập API key rồi bấm Thêm" };
        var addGemKeyBtn = new Button { Text = "Thêm key", Width = 100, Height = 28, Margin = new Padding(6, 0, 0, 0) };
        addGemKeyBtn.Click += (_, _) => AddGemKey(gemKeys, gemKeyInput);
        var ocrProvider = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 200
        };
        ocrProvider.Items.AddRange(new object[] { "tesseract", "paddle", "googlevision" });
        var visionKey = new TextBox { Width = 400, PlaceholderText = "Google Vision API key" };
        var visionKeyEnv = new TextBox { Width = 220, PlaceholderText = "GOOGLE_VISION_API_KEY" };
        var ocrCmd = new TextBox { Width = 400 };
        var ollamaEp = new TextBox { Width = 300 };
        var ollamaModel = new TextBox { Width = 200 };
        var prompt = new TextBox { Width = 500, Multiline = true, Height = 120, ScrollBars = ScrollBars.Vertical };
        var saveBtn = new Button { Text = "Lưu cấu hình", Width = 140, Height = 32 };

        saveBtn.Click += (_, _) => SaveConfig();

        return (capOut, capFmt, hotkey, fixedHotkey, confirmHotkey, x, y, w, h,
            autoAns, procOut, gemModels, gemKeys, gemKeyInput, addGemKeyBtn, ocrProvider, visionKey, visionKeyEnv, ocrCmd, ollamaEp, ollamaModel, prompt, saveBtn);
    }

    private Control BuildConfigLayout()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoScroll = true
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void AddRow(string label, Control control)
        {
            var row = panel.RowCount++;
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, row);
            panel.Controls.Add(control, 1, row);
        }

        AddRow("Captures OutputDirectory", _capOutputDir);
        AddRow("FileNameFormat", _capFileFormat);
        AddRow("Hotkey (chụp tự do)", _capHotkey);
        AddRow("FixedRegionHotkey (chụp cố định)", _capFixedHotkey);
        AddRow("FixedRegionConfirmHotkey (chọn lại vùng)", _capConfirmHotkey);
        AddRow("FixedRegion X", _capX);
        AddRow("FixedRegion Y", _capY);
        AddRow("FixedRegion Width", _capW);
        AddRow("FixedRegion Height", _capH);

        AddRow("EnableAutoAnswer", _procEnableAuto);
        AddRow("Outputs Directory", _procOutputDir);
        AddRow("Gemini Models (mỗi dòng 1 model)", _procGemModels);

        // Panel cho Gemini keys: hộp nhiều dòng + dòng nhập nhanh bên dưới, tránh đè lên control khác
        var keyPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true
        };
        keyPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        keyPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var keyInputPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Dock = DockStyle.Fill,
            WrapContents = false,
            Margin = new Padding(0, 6, 0, 0)
        };
        keyInputPanel.Controls.Add(_procGemKeyInput);
        keyInputPanel.Controls.Add(_addGemKeyBtn);

        keyPanel.Controls.Add(_procGemKeys, 0, 0);
        keyPanel.Controls.Add(keyInputPanel, 0, 1);

        AddRow("Gemini API Keys (mỗi dòng 1 key)", keyPanel);
        AddRow("OCR Provider (tesseract/paddle/googlevision)", _procOcrProvider);
        AddRow("Google Vision API Key", _procVisionKey);
        AddRow("Google Vision API Key Env", _procVisionKeyEnv);
        AddRow("OCR Command", _procOcrCmd);
        AddRow("Ollama Endpoint", _procOllamaEndpoint);
        AddRow("Ollama Model", _procOllamaModel);
        AddRow("Prompt Prefix", _procPrompt);

        AddRow("", _saveConfigBtn);

        var note = new Label
        {
            Text = "Lưu ý: đổi cấu hình cần khởi động lại ứng dụng để áp dụng đầy đủ.",
            AutoSize = true,
            ForeColor = Color.DarkRed,
            Padding = new Padding(0, 6, 0, 0)
        };
        AddRow("", note);

        return panel;
    }

    private void LoadConfigToUI()
    {
        _capOutputDir.Text = _captureSettings.OutputDirectory ?? "Captures";
        _capFileFormat.Text = _captureSettings.FileNameFormat;
        _capHotkey.Text = _captureSettings.Hotkey;
        _capFixedHotkey.Text = _captureSettings.FixedRegionHotkey;
        _capConfirmHotkey.Text = _captureSettings.FixedRegionConfirmHotkey;
        _capX.Value = _captureSettings.FixedRegionX;
        _capY.Value = _captureSettings.FixedRegionY;
        _capW.Value = _captureSettings.FixedRegionWidth;
        _capH.Value = _captureSettings.FixedRegionHeight;

        _procEnableAuto.Checked = _processingSettings.EnableAutoAnswer;
        _procOutputDir.Text = _processingSettings.OutputDirectory;
        _procGemModels.Text = string.Join(Environment.NewLine, _processingSettings.GetGeminiModelsOrdered());
        _procGemKeys.Text = string.Join(Environment.NewLine, _processingSettings.GetGeminiApiKeysOrdered());
        _procGemKeyInput.Text = string.Empty;
        _procOcrProvider.SelectedItem = (_processingSettings.OcrProvider ?? "tesseract").ToLowerInvariant() switch
        {
            "paddle" => "paddle",
            "googlevision" => "googlevision",
            _ => "tesseract"
        };
        _procVisionKey.Text = _processingSettings.GoogleVisionApiKey;
        _procVisionKeyEnv.Text = _processingSettings.GoogleVisionApiKeyEnv;
        _procOcrCmd.Text = _processingSettings.OcrCommand;
        _procOllamaEndpoint.Text = _processingSettings.OllamaEndpoint;
        _procOllamaModel.Text = _processingSettings.OllamaModel;
        _procPrompt.Text = _processingSettings.PromptPrefix;
    }

    private void SaveConfig()
    {
        try
        {
            var newCapture = new CaptureSettings
            {
                OverlayOpacity = _captureSettings.OverlayOpacity,
                OverlayColorHex = _captureSettings.OverlayColorHex,
                SelectionLineColorHex = _captureSettings.SelectionLineColorHex,
                SelectionLineWidth = _captureSettings.SelectionLineWidth,
                OutputDirectory = _capOutputDir.Text,
                FileNameFormat = _capFileFormat.Text,
                PixelFormatName = _captureSettings.PixelFormatName,
                ShowSavedDialog = _captureSettings.ShowSavedDialog,
                Hotkey = _capHotkey.Text,
                FixedRegionHotkey = _capFixedHotkey.Text,
                FixedRegionConfirmHotkey = _capConfirmHotkey.Text,
                FixedRegionX = (int)_capX.Value,
                FixedRegionY = (int)_capY.Value,
                FixedRegionWidth = (int)_capW.Value,
                FixedRegionHeight = (int)_capH.Value
            };
            CaptureSettings.Save(newCapture);

            var gemModels = _procGemModels.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            var gemKeys = _procGemKeys.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

            var newProcessing = new ProcessingSettings
            {
                EnableAutoAnswer = _procEnableAuto.Checked,
                PromptPrefix = _procPrompt.Text,
                GeminiModels = gemModels,
                GeminiModel = string.Empty,
                GeminiApiKeys = gemKeys,
                GeminiApiKey = string.Empty,
                GeminiApiKeyEnv = _processingSettings.GeminiApiKeyEnv,
                OllamaEndpoint = _procOllamaEndpoint.Text,
                OllamaModel = _procOllamaModel.Text,
                OcrProvider = (_procOcrProvider.SelectedItem?.ToString() ?? "tesseract").Trim(),
                OcrCommand = _procOcrCmd.Text,
                GoogleVisionApiKey = _procVisionKey.Text,
                GoogleVisionApiKeyEnv = string.IsNullOrWhiteSpace(_procVisionKeyEnv.Text)
                    ? _processingSettings.GoogleVisionApiKeyEnv
                    : _procVisionKeyEnv.Text,
                // Giữ nguyên cấu hình Paddle hiện tại (không chỉnh trong UI)
                PaddlePythonPath = _processingSettings.PaddlePythonPath,
                PaddleScriptPath = _processingSettings.PaddleScriptPath,
                PaddleLang = _processingSettings.PaddleLang,
                PaddleUseAngleCls = _processingSettings.PaddleUseAngleCls,
                OutputDirectory = _procOutputDir.Text
            };
            ProcessingSettings.Save(newProcessing);

            MessageBox.Show("Đã lưu cấu hình. Khởi động lại ứng dụng để áp dụng.", "Cấu hình", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Lưu cấu hình thất bại: {ex.Message}", "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }


    private void OpenLatestLog()
    {
        try
        {
            var logDir = _outputsDir;
            if (!Directory.Exists(logDir))
            {
                MessageBox.Show("Chưa có thư mục Outputs.", "Log", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var files = Directory.GetFiles(logDir, "processing_*.log", SearchOption.TopDirectoryOnly);
            if (files.Length == 0)
            {
                MessageBox.Show("Chưa có log xử lý.", "Log", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var latest = files.OrderBy(f => f).Last();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = latest,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Không mở được log: {ex.Message}", "Log", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore individual delete errors
        }
    }

    private static void AddGemKey(TextBox gemKeys, TextBox keyInput)
    {
        var key = keyInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(key)) return;

        var existing = gemKeys.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        if (!existing.Contains(key, StringComparer.OrdinalIgnoreCase))
        {
            existing.Add(key);
            gemKeys.Text = string.Join(Environment.NewLine, existing);
        }

        keyInput.Clear();
        keyInput.Focus();
    }

    private static void EnableListViewDoubleBuffer(ListView lv)
    {
        try
        {
            var prop = typeof(ListView).GetProperty("DoubleBuffered", BindingFlags.NonPublic | BindingFlags.Instance);
            prop?.SetValue(lv, true);
        }
        catch
        {
            // ignore
        }
    }

    private static string TrimForView(string text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        if (text.Length <= max) return text;
        return text.Substring(0, max) + "...";
    }

    private static string BuildMeta(AnswerResult r)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(r.QuestionNumber)) parts.Add(r.QuestionNumber.Trim());
        if (!string.IsNullOrWhiteSpace(r.QuestionId)) parts.Add(r.QuestionId.Trim());
        return string.Join(" ", parts);
    }

    private static void OpenFolderSafe(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch
        {
            // ignore
        }
    }

    private static string FormatOptions(Dictionary<string, string>? options)
    {
        if (options == null || options.Count == 0) return string.Empty;
        var ordered = options.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => $"{kv.Key}. {kv.Value}");
        return string.Join(Environment.NewLine, ordered);
    }

    private void ZoomImage(float factor)
    {
        if (_originalImage == null) return;
        
        _imageZoomLevel *= factor;
        _imageZoomLevel = Math.Max(0.1f, Math.Min(5.0f, _imageZoomLevel)); // Giới hạn từ 10% đến 500%
        
        UpdateImageZoom();
    }

    private void ResetZoom()
    {
        _imageZoomLevel = 1.0f;
        UpdateImageZoom();
    }

    private void UpdateImageZoom()
    {
        if (_originalImage == null) return;
        
        try
        {
            var newWidth = (int)(_originalImage.Width * _imageZoomLevel);
            var newHeight = (int)(_originalImage.Height * _imageZoomLevel);
            
            // Dispose ảnh zoomed cũ
            if (_resultPreview.Image != null && _resultPreview.Image != _originalImage)
            {
                _resultPreview.Image.Dispose();
            }
            
            var zoomedImage = new Bitmap(_originalImage, newWidth, newHeight);
            _resultPreview.Image = zoomedImage;
            _resultPreview.SizeMode = PictureBoxSizeMode.Zoom;
        }
        catch
        {
            // ignore errors
        }
    }

    private Control BuildVideoLayout()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 3,
            AutoSize = true,
            Padding = new Padding(4)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        int row = 0;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(new Label { Text = "Video file", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, row);
        _videoPathBox = new TextBox { Width = 400, ReadOnly = false };
        _videoBrowseBtn = new Button { Text = "Chọn...", Width = 80 };
        _videoBrowseBtn.Click += (_, _) => BrowseVideoFile();
        var filePanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = false,
            Height = 28
        };
        filePanel.Controls.Add(_videoPathBox);
        filePanel.Controls.Add(_videoBrowseBtn);
        panel.Controls.Add(filePanel, 1, row);
        panel.SetColumnSpan(filePanel, 2);

        row++;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(new Label { Text = "Khoảng cách frame (s)", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, row);
        _videoInterval = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 20,
            Value = 2,
            Width = 60
        };
        panel.Controls.Add(_videoInterval, 1, row);

        row++;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(new Label { Text = "FFmpeg path", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, row);
        _videoFfmpegPath = new TextBox { Width = 300 };
        
        // Tự động tìm ffmpeg và điền vào
        var foundFfmpeg = FindFfmpegPath();
        _videoFfmpegPath.Text = foundFfmpeg ?? "ffmpeg";
        
        var findFfmpegBtn = new Button { Text = "🔍 Tìm", Width = 70, Height = 28, Margin = new Padding(4, 0, 0, 0) };
        findFfmpegBtn.Click += (_, _) =>
        {
            var found = FindFfmpegPath();
            if (!string.IsNullOrWhiteSpace(found))
            {
                _videoFfmpegPath.Text = found;
                MessageBox.Show($"Đã tìm thấy FFmpeg:\n{found}", "Tìm FFmpeg", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show("Không tìm thấy FFmpeg. Vui lòng cài đặt FFmpeg hoặc nhập đường dẫn thủ công.", "Tìm FFmpeg", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        
        _videoRunBtn = new Button { Text = "Trích frame & xử lý", Width = 150, Height = 28, Margin = new Padding(8, 0, 0, 0) };
        _videoRunBtn.Click += async (_, _) => await RunVideoAsync().ConfigureAwait(false);
        var logBtn = new Button { Text = "Xem log", Width = 100, Height = 28, Margin = new Padding(8, 0, 0, 0) };
        logBtn.Click += (_, _) => OpenLatestLog();

        var ffmpegPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = false,
            Height = 28
        };
        ffmpegPanel.Controls.Add(_videoFfmpegPath);
        ffmpegPanel.Controls.Add(findFfmpegBtn);
        ffmpegPanel.Controls.Add(_videoRunBtn);
        ffmpegPanel.Controls.Add(logBtn);
        panel.Controls.Add(ffmpegPanel, 1, row);
        panel.SetColumnSpan(ffmpegPanel, 2);

        row++;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _videoStatus = new Label { Text = "Chọn video và nhấn Trích frame", AutoSize = true, ForeColor = Color.DarkBlue, Padding = new Padding(0, 6, 0, 0) };
        panel.Controls.Add(_videoStatus, 1, row);

        // info line
        row++;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var note = new Label
        {
            Text = "Tính năng thử nghiệm: Hệ thống sẽ tự động tìm FFmpeg. Nếu không tìm thấy, nhấn nút '🔍 Tìm' hoặc nhập đường dẫn thủ công. Frame video sẽ được trích vào Captures để AI xử lý, sau đó tự động chuyển sang thư mục VideoCaptures.",
            AutoSize = true,
            ForeColor = Color.DarkRed,
            Padding = new Padding(0, 10, 0, 0)
        };
        panel.Controls.Add(note, 0, row);
        panel.SetColumnSpan(note, 3);

        var topContainer = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        topContainer.Controls.Add(panel);

        return topContainer;
    }

    private void BrowseVideoFile()
    {
        using var ofd = new OpenFileDialog
        {
            Filter = "Video files|*.mp4;*.mkv;*.avi;*.mov;*.wmv|All files|*.*"
        };
        if (ofd.ShowDialog() == DialogResult.OK && _videoPathBox != null)
        {
            _videoPathBox.Text = ofd.FileName;
        }
    }

    private async Task RunVideoAsync()
    {
        if (_videoPathBox == null || _videoInterval == null || _videoRunBtn == null)
            return;

        var videoPath = _videoPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
        {
            MessageBox.Show("Chọn file video hợp lệ.", "Video", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var interval = (int)_videoInterval.Value;
        _videoRunBtn.Enabled = false;
        SetVideoStatus("Đang trích frame... vui lòng đợi");

        try
        {
            var tempDir = Path.Combine(Program.GetProjectRoot(), "TempFrames");
            var ffmpegPath = _videoFfmpegPath != null && !string.IsNullOrWhiteSpace(_videoFfmpegPath.Text)
                ? _videoFfmpegPath.Text.Trim()
                : "ffmpeg";

            var extractor = new VideoFrameExtractor(ffmpegPath, interval, tempDir, _capturesDir);
            var kept = await extractor.ExtractAsync(videoPath).ConfigureAwait(false);
            SetVideoStatus($"Đã trích {kept} frame (đã lọc trùng). Ảnh nằm trong Captures, sẽ được AI xử lý.");
            // reload list to see new results when xong processing
            BeginInvoke((Action)(() =>
            {
                LoadResults();
            }));
        }
        catch (Exception ex)
        {
            SetVideoStatus($"Lỗi: {ex.Message}", true);
            MessageBox.Show($"Lỗi trích frame: {ex.Message}", "Video", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _videoRunBtn.Enabled = true;
        }
    }

    private void SetVideoStatus(string text, bool isError = false)
    {
        if (_videoStatus == null) return;
        if (InvokeRequired)
        {
            BeginInvoke((Action)(() => SetVideoStatus(text, isError)));
            return;
        }
        _videoStatus.Text = text;
        _videoStatus.ForeColor = isError ? Color.DarkRed : Color.DarkBlue;
    }

    private void AutoSizeResultColumns()
    {
        if (_resultsList.Columns.Count == 0) return;
        _resultsList.BeginUpdate();
        _resultsList.AutoResizeColumns(ColumnHeaderAutoResizeStyle.HeaderSize);
        _resultsList.EndUpdate();
    }

    private static string? FindFfmpegPath()
    {
        // 1. Kiểm tra trong PATH
        try
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrWhiteSpace(pathEnv))
            {
                var paths = pathEnv.Split(Path.PathSeparator);
                foreach (var path in paths)
                {
                    var ffmpegPath = Path.Combine(path, "ffmpeg.exe");
                    if (File.Exists(ffmpegPath))
                    {
                        return ffmpegPath;
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        // 2. Kiểm tra trong WinGet Packages (thư mục phổ biến nhất)
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var wingetPackages = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
            
            if (Directory.Exists(wingetPackages))
            {
                // Tìm tất cả thư mục chứa "ffmpeg" (không phân biệt hoa thường)
                var allDirs = Directory.GetDirectories(wingetPackages, "*", SearchOption.TopDirectoryOnly);
                foreach (var dir in allDirs)
                {
                    var dirName = Path.GetFileName(dir);
                    if (!dirName.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase) && 
                        !dirName.Contains("Gyan", StringComparison.OrdinalIgnoreCase))
                        continue;
                    
                    // Tìm đệ quy trong thư mục này (tối đa 3 cấp)
                    var found = FindFfmpegRecursive(dir, maxDepth: 3);
                    if (!string.IsNullOrWhiteSpace(found))
                    {
                        return found;
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        // 3. Kiểm tra trong Program Files
        try
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            
            foreach (var basePath in new[] { programFiles, programFilesX86 })
            {
                if (string.IsNullOrWhiteSpace(basePath) || !Directory.Exists(basePath)) continue;
                
                // Tìm trong các thư mục phổ biến
                var searchPaths = new[]
                {
                    Path.Combine(basePath, "ffmpeg", "bin", "ffmpeg.exe"),
                    Path.Combine(basePath, "FFmpeg", "bin", "ffmpeg.exe")
                };
                
                foreach (var searchPath in searchPaths)
                {
                    if (File.Exists(searchPath))
                    {
                        return searchPath;
                    }
                }
                
                // Tìm đệ quy trong các thư mục con (giới hạn độ sâu)
                try
                {
                    var ffmpegDirs = Directory.GetDirectories(basePath, "*ffmpeg*", SearchOption.TopDirectoryOnly);
                    foreach (var dir in ffmpegDirs)
                    {
                        var binPath = Path.Combine(dir, "bin", "ffmpeg.exe");
                        if (File.Exists(binPath))
                        {
                            return binPath;
                        }
                    }
                }
                catch
                {
                    // ignore
                }
            }
        }
        catch
        {
            // ignore
        }

        // 4. Kiểm tra trong thư mục người dùng
        try
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var userPaths = new[]
            {
                Path.Combine(userProfile, "ffmpeg", "bin", "ffmpeg.exe"),
                Path.Combine(userProfile, "AppData", "Local", "ffmpeg", "bin", "ffmpeg.exe")
            };
            
            foreach (var path in userPaths)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }
        catch
        {
            // ignore
        }

        return null; // Không tìm thấy
    }

    private static string? FindFfmpegRecursive(string directory, int maxDepth, int currentDepth = 0)
    {
        if (currentDepth >= maxDepth || !Directory.Exists(directory))
            return null;

        try
        {
            // Kiểm tra trực tiếp trong thư mục này
            var binPath = Path.Combine(directory, "bin", "ffmpeg.exe");
            if (File.Exists(binPath))
            {
                return binPath;
            }

            // Kiểm tra trực tiếp trong thư mục này (không có bin)
            var directPath = Path.Combine(directory, "ffmpeg.exe");
            if (File.Exists(directPath))
            {
                return directPath;
            }

            // Tìm đệ quy trong các thư mục con
            var subDirs = Directory.GetDirectories(directory);
            foreach (var subDir in subDirs)
            {
                var found = FindFfmpegRecursive(subDir, maxDepth, currentDepth + 1);
                if (!string.IsNullOrWhiteSpace(found))
                {
                    return found;
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

}

