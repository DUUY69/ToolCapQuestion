using System;
using System.Collections.Generic;

namespace CaptureRegionApp.Processing.Models;

public sealed class AnswerResult
{
    public string FileName { get; set; } = string.Empty; // Cho phép cập nhật để đồng bộ với file thực tế
    public string ImagePath { get; set; } = string.Empty; // Cho phép cập nhật để đồng bộ với file thực tế
    public string QuestionNumber { get; set; } = string.Empty; // Cho phép chỉnh sửa meta (STT)
    public string QuestionId { get; set; } = string.Empty;     // Cho phép chỉnh sửa meta (Mã câu hỏi)
    public string Question { get; set; } = string.Empty; // Cho phép chỉnh sửa
    public Dictionary<string, string>? Options { get; set; } // Cho phép chỉnh sửa
    public string Answer { get; set; } = string.Empty; // Cho phép chỉnh sửa
    public string AnswerText { get; set; } = string.Empty; // Cho phép chỉnh sửa
    public string RawAnswer { get; init; } = string.Empty;
    public string OcrText { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

