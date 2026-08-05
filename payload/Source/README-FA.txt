DigiAhan CDR Receiver - مرحله تست

1) این فایل‌ها را داخل پوشه پروژه DigiAhan.CDR.Receiver کپی و جایگزین کنید.

2) فایل appsettings.json را باز کنید.

3) اگر SQL Server روی همین ویندوز و با Windows Authentication است، این مقدار معمولاً درست است:
   Server=localhost;Database=DigiAhan_CDR;Integrated Security=True;TrustServerCertificate=True;

   اگر Instance نام‌دار دارید، نمونه:
   Server=localhost\SQLEXPRESS;Database=DigiAhan_CDR;Integrated Security=True;TrustServerCertificate=True;

4) مقدار ApiToken را به یک رشته طولانی تغییر دهید.
   همان مقدار را در test-request.json هم وارد کنید.

5) در Visual Studio:
   Build > Build Solution

6) با دکمه سبز http پروژه را اجرا کنید.

7) مرورگر باید این آدرس را باز کند:
   http://localhost:5088/health

   خروجی مطلوب:
   {"status":"healthy","database":"connected",...}

8) برای تست ارسال، PowerShell را در پوشه پروژه باز کنید و اجرا کنید:

   Invoke-RestMethod `
     -Uri "http://localhost:5088/api/cdr" `
     -Method Post `
     -ContentType "application/json" `
     -InFile ".\test-request.json"

9) خروجی مطلوب:
   Inserted = 1
   Duplicates = 0
   Errors = 0

10) اجرای دوباره همان دستور:
    Inserted = 0
    Duplicates = 1
    Errors = 0

فعلاً Windows Service، Firewall و اتصال Issabel را انجام ندهید.
اول Health و تست Insert باید موفق شود.
