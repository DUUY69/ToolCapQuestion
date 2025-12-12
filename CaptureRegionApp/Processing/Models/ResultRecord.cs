using System;

namespace CaptureRegionApp.Processing.Models;

public sealed class ResultRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string ImagePath { get; init; } = string.Empty;
    public string OcrText { get; init; } = string.Empty;
    public string Answer { get; init; } = string.Empty;
    public string Status { get; init; } = "pending"; // pending | answered
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? AnsweredAt { get; init; }
}

