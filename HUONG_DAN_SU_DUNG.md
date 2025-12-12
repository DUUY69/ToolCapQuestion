# Hướng Dẫn Sử Dụng Hệ Thống Chụp Ảnh và Trả Lời Câu Hỏi

## Mô Tả
Ứng dụng tự động chụp ảnh câu hỏi trắc nghiệm, đọc nội dung bằng OCR, và sử dụng AI để trả lời câu hỏi.

## Yêu Cầu Hệ Thống

### 1. .NET 8.0 SDK
- Tải từ: https://dotnet.microsoft.com/download/dotnet/8.0
- Hoặc cài qua winget: `winget install Microsoft.DotNet.SDK.8`

### 2. Tesseract OCR (Bắt buộc để đọc nội dung từ ảnh)
**Cách cài đặt:**

**Cách 1: Dùng winget (Khuyến nghị)**
```powershell
winget install --id UB-Mannheim.TesseractOCR --accept-package-agreements --accept-source-agreements
```

**Cách 2: Dùng Chocolatey**
```powershell
choco install tesseract
```

**Cách 3: Tải thủ công**
1. Tải từ: https://github.com/UB-Mannheim/tesseract/wiki
2. Cài đặt file `.exe`
3. Thêm vào PATH: `C:\Program Files\Tesseract-OCR`

**Kiểm tra cài đặt:**
```bash
tesseract --version
```

### 3. AI Service (Chọn một trong hai)

#### Option A: Gemini AI (Khuyến nghị - miễn phí)
1. Lấy API Key từ: https://aistudio.google.com/apikey
2. Mở file `CaptureRegionApp\Config\processing-settings.json`
3. Thêm API Key vào `GeminiApiKey`:
```json
{
  "GeminiApiKey": "YOUR_API_KEY_HERE"
}
```

#### Option B: Ollama (Local - cần cài đặt)
1. Tải Ollama từ: https://ollama.ai
2. Cài đặt và chạy Ollama
3. Tải model: `ollama pull qwen2.5:7b-instruct`
4. Ứng dụng sẽ tự động dùng Ollama nếu Gemini không khả dụng

## Cài Đặt và Chạy

### 1. Build ứng dụng
```bash
cd CaptureRegionApp
dotnet build
```

### 2. Chạy ứng dụng
```bash
dotnet run
```

Hoặc double-click vào file `build-and-run.bat` trong thư mục gốc.

### 3. Chạy file exe đã build
Sau khi build, file exe sẽ ở:
```
CaptureRegionApp\bin\Debug\net8.0-windows\CaptureRegionApp.exe
```

## Cách Sử Dụng

### 1. Khởi động ứng dụng
- Chạy ứng dụng, icon sẽ xuất hiện ở system tray (góc dưới bên phải)

### 2. Chụp ảnh câu hỏi

**Cách 1: Chụp vùng tùy chọn**
- Nhấn `Ctrl+Q` hoặc double-click vào icon
- Kéo chuột để chọn vùng cần chụp
- Nhấn "Lưu" để lưu và xử lý

**Cách 2: Chụp vùng cố định**
- Nhấn `Ctrl+W` để chụp vùng đã cấu hình sẵn
- Vùng này sẽ được chụp tự động, không cần chọn

**Cách 3: Chọn vùng cố định mới**
- Nhấn `Ctrl+E` để chọn vùng mới
- Ứng dụng sẽ tự động khởi động lại sau khi cấu hình

### 3. Xem kết quả
- Kết quả sẽ hiển thị trong Console window
- Format hiển thị:
  - Câu hỏi: Số thứ tự (nếu có)
  - Mã: Mã câu hỏi (nếu có)
  - Câu hỏi: Nội dung câu hỏi
  - Các lựa chọn: A, B, C, D với nội dung đầy đủ
  - Đáp án: Đáp án kèm nội dung

## Cấu Hình

### File cấu hình chính:
- `CaptureRegionApp\Config\capture-settings.json` - Cấu hình chụp ảnh
- `CaptureRegionApp\Config\processing-settings.json` - Cấu hình xử lý AI

### Các cấu hình quan trọng:

**processing-settings.json:**
```json
{
  "EnableAutoAnswer": true,  // Bật/tắt tự động trả lời
  "GeminiApiKey": "",         // API Key cho Gemini (hoặc để trống dùng Ollama)
  "OllamaEndpoint": "http://localhost:11434/api/generate",
  "OllamaModel": "qwen2.5:7b-instruct",
  "OutputDirectory": "Outputs"  // Thư mục lưu kết quả (tương đối)
}
```

**capture-settings.json:**
```json
{
  "Hotkey": "Ctrl+Q",              // Phím tắt chụp vùng tùy chọn
  "FixedRegionHotkey": "Ctrl+W",    // Phím tắt chụp vùng cố định
  "OutputDirectory": "Captures"     // Thư mục lưu ảnh (tương đối)
}
```

## Thư Mục

- `Captures/` - Lưu ảnh đã chụp
- `Outputs/` - Lưu log và kết quả xử lý
- `Config/` - File cấu hình

## Xử Lý Sự Cố

### Lỗi "Tesseract không được cài đặt"
- Cài đặt Tesseract OCR (xem phần Yêu Cầu Hệ Thống)
- Khởi động lại ứng dụng

### Lỗi "429 Too Many Requests" từ Gemini
- Đây là lỗi rate limit, hệ thống sẽ tự động chuyển sang Ollama
- Đợi một lúc rồi thử lại, hoặc dùng Ollama

### OCR đọc sai nhiều từ
- Hệ thống tự động sửa một số lỗi OCR phổ biến
- AI sẽ tiếp tục sửa các lỗi còn lại
- Nếu vẫn sai, có thể cần cải thiện chất lượng ảnh chụp

### Kết quả hiển thị không đúng
- Kiểm tra xem Tesseract đã được cài đặt chưa
- Kiểm tra API Key Gemini (nếu dùng)
- Kiểm tra Ollama đang chạy (nếu dùng Ollama)

## Lưu Ý

- Ứng dụng xử lý tuần tự: một ảnh tại một thời điểm
- Kết quả chỉ hiển thị trên Console, không lưu file
- Mỗi ảnh chỉ được xử lý một lần duy nhất
- Console window sẽ tự động hiển thị khi có ảnh mới

## Hỗ Trợ

Nếu gặp vấn đề, kiểm tra:
1. File log: `Outputs\processing_YYYYMMDD.log`
2. Console window để xem thông báo lỗi
3. Đảm bảo Tesseract đã được cài đặt và có trong PATH

