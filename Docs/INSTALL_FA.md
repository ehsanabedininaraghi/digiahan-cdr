# نصب DigiAhan CDR Receiver v3.0.0

## 1) دیتابیس
اگر اسکریپت‌های نسخه 2.2 را قبلاً کامل اجرا کرده‌اید، فقط `05_VERIFY_V3.sql` را اجرا کنید.
در غیر این صورت فایل‌های Database را به ترتیب شماره اجرا کنید.

## 2) تنظیمات
در `Source/appsettings.json`، Connection String و ApiToken فعلی خودتان را کنترل کنید.

## 3) Build و اجرا
```powershell
cd Source
dotnet clean
dotnet build
dotnet run
```

## 4) آدرس‌ها
- داشبورد: `http://localhost:5088/dashboard`
- سلامت: `http://localhost:5088/health`
- نسخه: `http://localhost:5088/api/version`

## 5) لاگ
لاگ روزانه در پوشه `Source/Logs` ساخته می‌شود. برای گزارش خطا کل پوشه Logs را ZIP کنید.

## نکته مهم
نسخه 3 دو Query مستقل برای Count و Page دارد. مشکل CTE نسخه 2 که باعث `Invalid object name 'Paged'` می‌شد حذف شده است.
