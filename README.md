# ToolCapQuestion

Ứng dụng tự động chụp ảnh câu hỏi trắc nghiệm, đọc nội dung bằng OCR (Tesseract), và sử dụng AI (Gemini/Ollama) để trả lời câu hỏi.

## Tính Năng

- 📸 Chụp ảnh vùng tùy chọn hoặc vùng cố định
- 🔍 OCR tự động đọc nội dung từ ảnh (Tesseract)
- 🤖 Trả lời câu hỏi bằng AI (Gemini hoặc Ollama)
- 🔧 Tự động sửa lỗi OCR phổ biến
- 📊 Hiển thị kết quả chi tiết trên Console

## Yêu Cầu

- .NET 8.0 SDK
- Tesseract OCR
- AI Service: Gemini API Key hoặc Ollama (local)

## Cài Đặt Nhanh

### 1. Cài đặt Tesseract OCR

**Windows (winget):**
```powershell
winget install --id UB-Mannheim.TesseractOCR
```

**Windows (Chocolatey):**
```powershell
choco install tesseract
```

### 2. Cấu hình API Key

1. Copy file mẫu:
   ```bash
   copy CaptureRegionApp\Config\processing-settings.json.example CaptureRegionApp\Config\processing-settings.json
   ```

2. Mở `CaptureRegionApp\Config\processing-settings.json` và thêm Gemini API Key:
   ```json
   {
     "GeminiApiKey": "YOUR_API_KEY_HERE"
   }
   ```

   Hoặc lấy API Key từ: https://aistudio.google.com/apikey

### 3. Build và Chạy

```bash
cd CaptureRegionApp
dotnet build
dotnet run
```

Hoặc double-click `build-and-run.bat`

## Sử Dụng

1. **Chụp vùng tùy chọn:** Nhấn `Ctrl+Q`, kéo chuột chọn vùng, nhấn "Lưu"
2. **Chụp vùng cố định:** Nhấn `Ctrl+W` (cần cấu hình trước bằng `Ctrl+E`)
3. **Xem kết quả:** Kết quả hiển thị trên Console window

## Cấu Hình

- `CaptureRegionApp/Config/capture-settings.json` - Cấu hình chụp ảnh
- `CaptureRegionApp/Config/processing-settings.json` - Cấu hình AI và OCR

## Tài Liệu

Xem file [HUONG_DAN_SU_DUNG.md](HUONG_DAN_SU_DUNG.md) để biết hướng dẫn chi tiết.

## Lưu Ý Bảo Mật

⚠️ **QUAN TRỌNG:** File `processing-settings.json` có thể chứa API Key. Không commit file này lên public repository nếu chứa API Key thật.

File `.gitignore` đã được cấu hình để bỏ qua các file nhạy cảm. Sử dụng file `.example` làm mẫu.

## License

MIT

## Tác Giả

DUUY69






