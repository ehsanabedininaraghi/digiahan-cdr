# DigiAhan CDR v4.0.0 Core

این نسخه روی هسته سالم ۳.۷.۷ ساخته شده، اما Endpoint رویداد VoIP را از نو پیاده‌سازی می‌کند.

## تفاوت کلیدی

- حذف Model Binding از `/api/voip/events`
- خواندن و Parse دستی JSON
- پشتیبانی از نام‌های جایگزین فیلدها مثل `caller`, `phone`, `src`
- سه حالت کارت مشتری: `FULL`, `FALLBACK`, `EMERGENCY`
- یک رویداد معتبر VoIP دیگر به دلیل خطای SQL یا ساخت کارت، پاسخ 500 نمی‌گیرد
- لاگ مستقل هر تماس در:

```text
Source\Logs\Voip\v4\
```

- فایل تشخیصی کامل هر نصب در:

```text
Logs\Runs\v4.0.0-*.zip
```

## نصب

محتویات ZIP را داخل این مسیر Extract کنید:

```text
D:\DigiAhan\CDR3.1.0git
```

سپس:

```powershell
cd D:\DigiAhan\CDR3.1.0git
.\RUN-v4.0.0.cmd
```

## خروجی موفق

```text
[PASS] VoIP → Identity → Accounting
PASS=12 FAIL=0
```

## تست واقعی Issabel

```bash
digiahan-test-ring 201 09121395663
```
