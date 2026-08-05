# v4.0.1 Log Pack Fix

این Hotfix فقط مشکل ZIP شدن فایل‌های لاگ باز را رفع می‌کند.

فایل‌های `application-stdout.log` و `application-stderr.log` هنگام اجرای داشبورد باز هستند.
اسکریپت جدید آن‌ها را با FileShare.ReadWrite کپی می‌کند و بعد از روی نسخه کپی‌شده ZIP می‌سازد.

اجرا:

```powershell
cd D:\DigiAhan\CDR3.1.0git
.\CREATE-LOG-ZIP-v4.0.1.cmd
```

خروجی داخل:

```text
D:\DigiAhan\CDR3.1.0git\Logs\Runs\v4.0.1-logpack-*.zip
```

داشبورد متوقف نمی‌شود.
