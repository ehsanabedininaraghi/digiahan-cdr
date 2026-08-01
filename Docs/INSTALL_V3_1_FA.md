# نصب نسخه 3.1.0

1. از پوشه فعلی پروژه و دیتابیس نسخه پشتیبان بگیرید.
2. فایل `Database/06_VERIFY_V3_1.sql` را در SSMS اجرا کنید. این نسخه Migration جدیدی لازم ندارد.
3. فایل `Source/appsettings.json` را با نسخه فعال خودتان تطبیق دهید و Connection String و ApiToken سالم را نگه دارید.
4. در پوشه `Source` اجرا کنید:

```powershell
dotnet clean
dotnet build
dotnet run
```

5. تست‌ها:

- `http://localhost:5088/api/version`
- `http://localhost:5088/api/dashboard/summary?startDate=2026-08-01&endDate=2026-08-01&extension=all`
- `http://localhost:5088/api/dashboard/daily?startDate=2026-07-26&endDate=2026-08-01&extension=all`
- `http://localhost:5088/api/dashboard/calls?startDate=2026-08-01&endDate=2026-08-01&extension=400&pageSize=20`
- `http://localhost:5088/dashboard`

## رفتار صحیح مورد انتظار

- تمام ردیف‌های یک تماس با LinkedId مشترک باید یک نام مشتری یکسان داشته باشند.
- تماس داخلی بدون شماره خارجی نباید مشتری جدید باشد.
- کارت مشتری جدید، تعداد شماره‌های خارجی یکتای ثبت‌نشده در دیدار است.
- انتخاب داخلی 400 باید فقط تماس‌های درگیر با داخلی 400 را نمایش دهد.
