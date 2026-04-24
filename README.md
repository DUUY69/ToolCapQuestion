# ToolCapQuestions – CaptureRegionApp

Ứng dụng Windows chụp vùng màn hình, OCR tự động và trả lời câu hỏi trắc nghiệm bằng AI (Gemini / Ollama).

---

## Yêu cầu hệ thống

- Windows 10/11 (64-bit)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Python 3.8+ (nếu dùng PaddleOCR)

---

## Cài đặt

### 1. Clone repo

```bash
git clone https://github.com/DUUY69/ToolCapQuestions.git
cd ToolCapQuestions
```

### 2. Cấu hình file config

Copy các file example trong thư mục `CaptureRegionApp/Config/`:

```bash
copy CaptureRegionApp\Config\capture-settings.json.example     CaptureRegionApp\Config\capture-settings.json
copy CaptureRegionApp\Config\processing-settings.json.example  CaptureRegionApp\Config\processing-settings.json
```

Sau đó chỉnh sửa theo hướng dẫn bên dưới.

---

## Cấu hình

### `capture-settings.json`

| Trường | Mô tả | Mặc định |
|---|---|---|
| `OverlayOpacity` | Độ mờ overlay khi chụp (0.0 – 1.0) | `0.2` |
| `OutputDirectory` | Thư mục lưu ảnh chụp | `Captures` |
| `Hotkey` | Phím tắt chụp tự do | `Ctrl+Q` |
| `FixedRegionHotkey` | Phím tắt chụp vùng cố định | `Ctrl+W` |
| `FixedRegionConfirmHotkey` | Phím tắt chọn lại vùng cố định | `Ctrl+E` |
| `FixedRegionX/Y/Width/Height` | Tọa độ và kích thước vùng cố định | `0,0,400,200` |

### `processing-settings.json`

| Trường | Mô tả |
|---|---|
| `OcrProvider` | OCR engine: `googlevision` / `tesseract` / `paddle` |
| `GoogleVisionApiKey` | API key Google Cloud Vision |
| `GeminiApiKeys` | Danh sách Gemini API key (dùng xoay vòng) |
| `GeminiModels` | Danh sách model Gemini ưu tiên |
| `OllamaEndpoint` | Endpoint Ollama nếu dùng local AI |
| `OllamaModel` | Model Ollama (ví dụ: `qwen2.5:7b-instruct`) |
| `OutputDirectory` | Thư mục lưu kết quả OCR/AI | 
| `EnableAutoAnswer` | Bật/tắt tự động gửi AI | `true` |

---

## Lấy API Key

### Google Cloud Vision (OCR)
1. Vào [Google Cloud Console](https://console.cloud.google.com/)
2. Tạo project → Enable **Cloud Vision API**
3. Tạo **API Key** tại `APIs & Services > Credentials`
4. Điền vào `GoogleVisionApiKey` trong `processing-settings.json`

### Gemini AI
1. Vào [Google AI Studio](https://aistudio.google.com/app/apikey)
2. Tạo API key
3. Điền vào mảng `GeminiApiKeys` trong `processing-settings.json`

> Có thể thêm nhiều key để tránh rate limit — app sẽ tự xoay vòng.

---

## Cài đặt OCR (chọn 1 trong 3)

### Option A – Google Vision (khuyến nghị, không cần cài thêm)
Chỉ cần điền `GoogleVisionApiKey`, set `"OcrProvider": "googlevision"`.

### Option B – Tesseract
```bash
# Tải installer tại: https://github.com/UB-Mannheim/tesseract/wiki
# Sau khi cài, thêm vào PATH, rồi set:
"OcrProvider": "tesseract"
"OcrCommand": "tesseract \"{input}\" stdout -l eng --psm 4"
```

### Option C – PaddleOCR
```bash
pip install paddlepaddle paddleocr
# set trong processing-settings.json:
"OcrProvider": "paddle"
"PaddlePythonPath": "python"
"PaddleScriptPath": "Processing/Ocr/paddle_ocr_cli.py"
```

---

## Build & Chạy

```bash
cd CaptureRegionApp
dotnet build
dotnet run
```

Hoặc build release:

```bash
dotnet publish -c Release -r win-x64 --self-contained
```

App chạy dưới dạng **system tray** — icon xuất hiện ở góc phải taskbar.

---

## Sử dụng

| Thao tác | Mô tả |
|---|---|
| `Ctrl+Q` | Chụp vùng tự chọn (kéo chuột) |
| `Ctrl+W` | Chụp vùng cố định đã cấu hình |
| `Ctrl+E` | Chọn lại vùng cố định mới |
| Click icon tray | Mở bảng điều khiển |
| Double-click icon tray | Chụp ngay |

Sau khi chụp, app tự động:
1. OCR ảnh → trích xuất text câu hỏi
2. Gửi lên Gemini/Ollama → nhận đáp án
3. Hiển thị kết quả trong cửa sổ Results

---

## Lưu ý Windows Defender

File `apphost.exe` trong thư mục `obj/` và `bin/` là file do .NET SDK tự sinh ra — **không phải virus**. Nếu bị chặn (lỗi `0x800700E1`), thêm exclusion:

`Windows Security > Virus & threat protection > Manage settings > Exclusions > Add folder`

Thêm thư mục gốc của project vào danh sách loại trừ.

---

## Cấu trúc thư mục

```
CaptureRegionApp/
├── Config/
│   ├── capture-settings.json          ← cấu hình chụp màn hình
│   └── processing-settings.json       ← cấu hình OCR + AI
├── Processing/
│   ├── Ocr/                           ← các OCR provider
│   ├── Ai/                            ← Gemini / Ollama client
│   └── Models/                        ← data models
├── Captures/                          ← ảnh chụp (tự tạo khi chạy)
└── Outputs/                           ← kết quả OCR + AI (tự tạo khi chạy)
```
