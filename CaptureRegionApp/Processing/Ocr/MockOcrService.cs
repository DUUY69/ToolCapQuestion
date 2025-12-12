using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CaptureRegionApp.Processing.Ocr;

/// <summary>
/// OCR service giả lập để test khi chưa có Tesseract.
/// Trả về text mẫu từ ảnh chụp câu hỏi.
/// LƯU Ý: Đây chỉ là dữ liệu mẫu, không đọc từ ảnh thật.
/// Để dùng OCR thật, cần cài đặt Tesseract.
/// </summary>
public sealed class MockOcrService : IOcrService
{
    public Task<string> ExtractTextAsync(string imagePath, CancellationToken cancellationToken)
    {
        // Lấy tên file để tạo text khác nhau cho mỗi file
        var fileName = Path.GetFileNameWithoutExtension(imagePath);
        
        // Tạo text mẫu dựa trên tên file để mỗi file có nội dung khác nhau
        // (Trong thực tế, cần Tesseract để đọc nội dung thật từ ảnh)
        var fileHash = fileName.GetHashCode();
        var questionNum = Math.Abs(fileHash % 50) + 1;
        var questionId = Math.Abs(fileHash % 100000);
        
        var mockText = $@"Multiple choices {questionNum}/50
[{questionId}]
(Choose 1 answer)
The PageModel in Razor Pages is best described as:
A. A combination of Controller and ViewModel
B. A database entity
C. An Entity Framework class
D. A replacement for HTML

⚠ LƯU Ý: Đây là DỮ LIỆU MẪU, không đọc từ ảnh thật. 
⚠ Tất cả ảnh sẽ cho kết quả giống nhau nếu chưa cài Tesseract OCR.
⚠ File: {Path.GetFileName(imagePath)}";

        return Task.FromResult(mockText);
    }
}

