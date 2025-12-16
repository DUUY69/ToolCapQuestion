#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
CLI đơn giản gọi PaddleOCR và in ra JSON: { "text": "..." }
Yêu cầu: pip install paddleocr paddlepaddle (phiên bản CPU/GPU phù hợp)

Tham số:
  python paddle_ocr_cli.py "<image_path>" --lang en --use-angle-cls 1
"""

import argparse
import json
import os
import sys
import os

# Giảm kiểm tra host tải model để tránh treo
os.environ.setdefault("DISABLE_MODEL_SOURCE_CHECK", "True")

from paddleocr import PaddleOCR


def parse_args():
    parser = argparse.ArgumentParser()
    parser.add_argument("image", help="Path tới ảnh cần OCR")
    parser.add_argument("--lang", default="en", help="Ngôn ngữ (ví dụ: en, en,vi nếu dùng multi-lang pack)")
    parser.add_argument("--use-angle-cls", type=int, default=1, help="(deprecated) giữ tham số cũ, không còn truyền vào predict")
    return parser.parse_args()


def main():
    args = parse_args()
    if not os.path.exists(args.image):
        print(json.dumps({"error": f"Image not found: {args.image}"}), ensure_ascii=False)
        sys.exit(1)

    # PaddleOCR mới không hỗ trợ show_log; use_angle_cls deprecated -> không truyền vào predict
    ocr = PaddleOCR(use_angle_cls=bool(args.use_angle_cls), lang=args.lang)
    # Gọi predict/ocr KHÔNG truyền cls để tránh TypeError
    result = ocr.ocr(args.image)

    lines = []
    for res in result:
        for line in res:
            text, conf = line[1]
            lines.append(text)

    print(json.dumps({"text": "\n".join(lines)}, ensure_ascii=False))


if __name__ == "__main__":
    main()

