# DigiAhan CDR Receiver

سامانه دریافت CDR ایزابل، ذخیره در SQL Server، اتصال اطلاعات مخاطبان دیدار و داشبورد مدیریتی دیجی‌آهن.

## ساختار
- `Source/` کد ASP.NET Core
- `Database/` اسکریپت‌های Migration و کنترل
- `Docs/` مستندات، Changelog و Bug Tracker
- `scripts/` راه‌اندازی GitHub، Build، Deploy و Rollback

## اجرای محلی
1. فایل `Source/appsettings.example.json` را به `Source/appsettings.json` کپی کنید.
2. Connection String و ApiToken را فقط در فایل محلی وارد کنید. این فایل در Git ثبت نمی‌شود.
3. اجرا:

```powershell
cd Source
dotnet restore
dotnet build
dotnet run
```

داشبورد: `http://localhost:5088/dashboard`

## نسخه فعلی
`v4.3.1`

راهنمای نصب و تغییرات این نسخه:

- [README-v4.3.1-FA.md](README-v4.3.1-FA.md)
- [CHANGELOG-v4.3.1.md](CHANGELOG-v4.3.1.md)
