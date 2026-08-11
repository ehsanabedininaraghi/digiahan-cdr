# ورودی Batch ضبط مکالمه

تا زمان اتصال مستقیم به Issabel، مسیر زیر ورودی رسمی فایل‌های صوتی است:

```text
D:\ChatGPT\DIGIAHAN\recording-sample
```

فایل‌ها می‌توانند روزانه یا هفتگی، مستقیم یا در پوشه‌های تاریخ‌دار اضافه شوند. پردازش بر پایه SHA-256 است؛ بنابراین جابه‌جایی یا تکرار یک فایل باعث تحلیل دوباره نمی‌شود.

## مراحل پردازش

```text
WAV جدید
  → اعتبارسنجی WAV و محاسبه SHA-256
  → تشخیص گفتار و جداسازی سکوت/تماس قطع‌شده
  → تبدیل متن اولیه برای فایل‌های دارای گفتار
  → تفکیک مکالمه کاری، صف/IVR و موارد نیازمند بازبینی
  → ساخت خروجی بی‌نام برای داشبورد
```

متن خام، نام اصلی فایل و شماره تلفن در `batch-data.json` منتشر نمی‌شوند. داشبورد فقط شناسه بی‌نام، طبقه‌بندی، خلاصه و موارد بازبینی را دریافت می‌کند.

## اجرای افزایشی

متغیر `PYTHONPATH` باید به بسته‌های runtime محلی اشاره کند. سپس سه ابزار به ترتیب اجرا می‌شوند:

```powershell
python tools\ai\triage_recording_batch.py `
  D:\ChatGPT\DIGIAHAN\recording-sample `
  --output D:\ChatGPT\DIGIAHAN\recording-sample\output\batch-triage-v1.json

python tools\ai\transcribe_recording_batch.py `
  D:\ChatGPT\DIGIAHAN\recording-sample `
  --triage D:\ChatGPT\DIGIAHAN\recording-sample\output\batch-triage-v1.json `
  --output D:\ChatGPT\DIGIAHAN\recording-sample\output\batch-transcripts-small-v1.json `
  --model-cache D:\ChatGPT\DIGIAHAN\.sprint05-runtime\models `
  --exclude-sha256-file tools\ai\recording-sample-baseline-sha256.json

python tools\ai\generate_safe_dashboard_batch.py `
  --baseline Source\wwwroot\ai\sample-data.json `
  --triage D:\ChatGPT\DIGIAHAN\recording-sample\output\batch-triage-v1.json `
  --transcripts D:\ChatGPT\DIGIAHAN\recording-sample\output\batch-transcripts-small-v1.json `
  --output D:\ChatGPT\DIGIAHAN\recording-sample\output\batch-dashboard-data.json `
  --exclude-sha256-file tools\ai\recording-sample-baseline-sha256.json
```

پنج فایل اولیه با فایل hash ثابت از batchهای بعدی کنار گذاشته می‌شوند، چون همان‌ها قبلاً شش تحلیل پایه داشبورد را ساخته‌اند. خروجی‌های triage و transcription بعد از هر فایل به‌صورت اتمیک ذخیره می‌شوند و در اجرای بعدی ادامه پیدا می‌کنند.

## وضعیت Batch نخست

- کل فایل‌ها: ۱۲۲ فایل، حدود ۱۳۴ دقیقه و ۱۲۸٬۶۶۷٬۷۶۸ بایت
- فایل‌های جدید: ۱۱۷ فایل، حدود ۱۲۷ دقیقه
- بدون گفتار/قطع‌شده قطعی: ۱۱
- گفتار کم و نیازمند بازبینی: ۷
- دارای گفتار محتمل: ۹۹

این طبقه‌بندی صرفاً دروازه ورودی است. وجود گفتار به معنای مکالمه کاری نیست؛ صف انتظار، IVR و صدای غیرکاری در مرحله متن اولیه جدا می‌شوند.
