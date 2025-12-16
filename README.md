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

1. **Chụp vùng tùy chọn:** Nhấn `Ctrl+Q`, kéo chuột chọn vùng, nhấn "Lưu".
2. **Chụp vùng cố định:** Nhấn `Ctrl+W` (cần cấu hình trước bằng `Ctrl+E`).
3. **Xem & chỉnh kết quả:**
   - Mở cửa sổ `Bảng điều khiển (Xem ảnh/Config)` → tab **Kết quả** để xem danh sách câu hỏi, ảnh, đáp án.
   - Chọn một dòng để xem chi tiết bên phải: ảnh, **Câu số (QuestionNumber)**, **Mã (QuestionId)**, nội dung câu hỏi + lựa chọn và đáp án.
   - Có thể chỉnh sửa câu hỏi, lựa chọn, đáp án, **Câu số** và **Mã**, sau đó bấm **Lưu** để ghi lại vào file `*_result.json`.
   - Dùng nút **“Xuất TXT (tất cả)”** để xuất toàn bộ kết quả ra file `results.txt` (bao gồm Câu số/Mã đã chỉnh).

## Cấu Hình

- `CaptureRegionApp/Config/capture-settings.json`  
  - Cấu hình chụp ảnh (hotkey, vùng cố định, thư mục `Captures`).
- `CaptureRegionApp/Config/processing-settings.json`  
  - Cấu hình AI, OCR, thư mục `Outputs`, danh sách `GeminiModels` và `GeminiApiKeys`, prompt xử lý.
  - File thật **không nằm trong repo** – bạn copy từ file mẫu:
    ```bash
    copy CaptureRegionApp\Config\capture-settings.json.example CaptureRegionApp\Config\capture-settings.json
    copy CaptureRegionApp\Config\processing-settings.json.example CaptureRegionApp\Config\processing-settings.json
    ```
  - Sau đó chỉnh bằng tay hoặc qua tab **Cấu hình** trong `Bảng điều khiển`.

## Tài Liệu

Xem file [HUONG_DAN_SU_DUNG.md](HUONG_DAN_SU_DUNG.md) để biết hướng dẫn chi tiết.

## Lưu Ý Bảo Mật

⚠️ **QUAN TRỌNG:** Các file:

- `CaptureRegionApp/Config/processing-settings.json`
- `CaptureRegionApp/Config/capture-settings.json`

có thể chứa API Key và cấu hình cá nhân.  
File `.gitignore` đã được cấu hình để **bỏ qua** chúng; chỉ các file `*.example` được track.  
Luôn:

- Lấy API key mới nếu key cũ từng bị public.
- Đặt key thật qua file local hoặc biến môi trường (ví dụ `GEMINI_API_KEY`, `GOOGLE_VISION_API_KEY`), **không commit lên GitHub**.

## License

MIT

## Tác Giả

DUUY69







