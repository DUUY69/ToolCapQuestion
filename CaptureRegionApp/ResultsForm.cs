using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;
using CaptureRegionApp.Processing.Models;

namespace CaptureRegionApp;

public sealed class ResultsForm : Form
{
    private readonly string _outputDir;
    private readonly ListView _listView;
    private readonly Button _refreshButton;
    private readonly Button _logButton;

    public ResultsForm(string outputDir)
    {
        _outputDir = outputDir;

        Text = "Kết quả";
        Width = 1000;
        Height = 650;
        StartPosition = FormStartPosition.CenterScreen;

        _listView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true
        };
        _listView.Columns.Add("Thời gian (UTC)", 150);
        _listView.Columns.Add("Ảnh", 200);
        _listView.Columns.Add("Mã/Câu số", 120);
        _listView.Columns.Add("Câu hỏi", 300);
        _listView.Columns.Add("Đáp án", 180);

        _refreshButton = new Button
        {
            Text = "Làm mới",
            Dock = DockStyle.Top,
            Height = 32
        };
        _refreshButton.Click += (_, _) => LoadFromDisk();

        _logButton = new Button
        {
            Text = "Xem log",
            Dock = DockStyle.Top,
            Height = 32
        };
        _logButton.Click += (_, _) => OpenLatestLog();

        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 40,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true
        };
        panel.Controls.Add(_refreshButton);
        panel.Controls.Add(_logButton);

        Controls.Add(_listView);
        Controls.Add(panel);

        Load += (_, _) => LoadFromDisk();
        FormClosed += (_, _) => ResultBus.ResultAdded -= OnResultAdded;

        ResultBus.ResultAdded += OnResultAdded;
    }

    private void OnResultAdded(object? sender, AnswerResult e)
    {
        if (IsDisposed) return;
        try
        {
            BeginInvoke((Action)(() => AddOrUpdateItem(e)));
        }
        catch
        {
            // ignore
        }
    }

    private void LoadFromDisk()
    {
        try
        {
            Directory.CreateDirectory(_outputDir);
            var files = Directory.GetFiles(_outputDir, "*_result.json", SearchOption.TopDirectoryOnly);

            var results = new List<AnswerResult>();
            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var obj = JsonSerializer.Deserialize<AnswerResult>(json);
                    if (obj != null)
                    {
                        results.Add(obj);
                    }
                }
                catch
                {
                    // skip invalid file
                }
            }

            results = results
                .OrderByDescending(r => r.CreatedAt)
                .ThenByDescending(r => r.FileName)
                .ToList();

            _listView.BeginUpdate();
            _listView.Items.Clear();
            foreach (var r in results)
            {
                _listView.Items.Add(CreateItem(r));
            }
            _listView.EndUpdate();
        }
        catch
        {
            // ignore
        }
    }

    private void AddOrUpdateItem(AnswerResult r)
    {
        var key = r.FileName;
        foreach (ListViewItem item in _listView.Items)
        {
            if (item.Name == key)
            {
                UpdateItem(item, r);
                return;
            }
        }

        _listView.Items.Add(CreateItem(r));
    }

    private ListViewItem CreateItem(AnswerResult r)
    {
        var item = new ListViewItem(new[]
        {
            r.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            r.FileName,
            string.IsNullOrWhiteSpace(r.QuestionNumber) && string.IsNullOrWhiteSpace(r.QuestionId)
                ? ""
                : $"{r.QuestionNumber} {r.QuestionId}".Trim(),
            TrimForView(r.Question, 120),
            string.IsNullOrWhiteSpace(r.AnswerText) ? r.Answer : r.AnswerText
        })
        {
            Name = r.FileName,
            Tag = r
        };
        return item;
    }

    private void UpdateItem(ListViewItem item, AnswerResult r)
    {
        item.SubItems[0].Text = r.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");
        item.SubItems[1].Text = r.FileName;
        item.SubItems[2].Text = string.IsNullOrWhiteSpace(r.QuestionNumber) && string.IsNullOrWhiteSpace(r.QuestionId)
            ? ""
            : $"{r.QuestionNumber} {r.QuestionId}".Trim();
        item.SubItems[3].Text = TrimForView(r.Question, 120);
        item.SubItems[4].Text = string.IsNullOrWhiteSpace(r.AnswerText) ? r.Answer : r.AnswerText;
        item.Tag = r;
    }

    private static string TrimForView(string text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        if (text.Length <= max) return text;
        return text.Substring(0, max) + "...";
    }

    private void OpenLatestLog()
    {
        try
        {
            if (!Directory.Exists(_outputDir))
            {
                MessageBox.Show("Chưa có thư mục Outputs.", "Log", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var files = Directory.GetFiles(_outputDir, "processing_*.log", SearchOption.TopDirectoryOnly);
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
}

