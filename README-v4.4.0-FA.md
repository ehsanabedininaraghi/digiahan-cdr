# نصب امن DigiAhan CDR v4.4.0

## پیش‌نیاز مهم

قبل از نصب، این آدرس باید پاسخ `healthy` بدهد:

```text
http://localhost:5088/health
```

اگر Windows Service با حسابی اجرا می‌شود که به دیتابیس `DigiAhan_CDR` دسترسی ندارد، ابتدا هویت/مجوز SQL سرویس را اصلاح کنید. نصب‌کننده در این وضعیت قبل از توقف برنامه یا کپی فایل متوقف می‌شود.

## کنترل بسته بدون نصب

PowerShell را در پوشه Extractشده باز کنید و اجرا کنید:

```powershell
.\RUN-v4.4.0.ps1 -ValidatePackageOnly
```

این فرمان فقط کامل‌بودن payload، نسخه و syntax اسکریپت‌ها را کنترل می‌کند و سرویس یا دیتابیس را تغییر نمی‌دهد.

## نصب

1. ZIP را در یک پوشه جدا Extract کنید.
2. روی `RUN-v4.4.0.cmd` راست‌کلیک و **Run as administrator** را اجرا کنید.
3. نصب‌کننده ابتدا preflight سرویس را انجام می‌دهد، سپس backup می‌گیرد، فایل‌ها را نصب و build می‌کند.
4. پس از بالا‌آمدن سرویس، Health Check، نسخه، APIها و صفحات اصلی بررسی می‌شوند.
5. در هر خطا، rollback خودکار اجرا می‌شود.

نصب موفق فقط کد نسخه 4.4.0 را آماده می‌کند. Journey و Auto Capture همچنان خاموش‌اند و Seller v2 مسیر کاری فعال است.

## فعال‌کردن پایلوت

پس از مشخص‌کردن `SellerKey` کاربر آزمایشی:

```powershell
.\CONFIGURE-JOURNEY-PILOT-v4.4.0.ps1 `
  -RepositoryRoot "D:\DigiAhan\CDR4.0" `
  -Enable `
  -PilotSellerKeys "SELLER_KEY"
```

در پایلوت اول Auto Capture را روشن نکنید. پس از تأیید پایلوت دستی:

```powershell
.\CONFIGURE-JOURNEY-PILOT-v4.4.0.ps1 `
  -RepositoryRoot "D:\DigiAhan\CDR4.0" `
  -Enable `
  -EnableAutoCapture `
  -PilotSellerKeys "SELLER_KEY"
```

آدرس‌ها:

- میزکار پایلوت فروش: `http://192.168.8.143:5088/seller-v3`
- مرکز کنترل مدیر: `http://192.168.8.143:5088/journey-control`

## rollback دستی

`ROLLBACK-v4.4.0.cmd` را با Administrator اجرا کنید. آخرین backup نسخه 4.4.0 انتخاب می‌شود، فایل‌های نسخه قبل بازمی‌گردند و فایل‌های فقط-v4.4 به قرنطینه قابل‌بازیابی منتقل می‌شوند. جداول افزایشی Journey حذف نمی‌شوند.

فایل‌های تشخیصی:

- نصب: `D:\DigiAhan\CDR4.0\Logs\Runs\v4.4.0-*`
- rollback: `D:\DigiAhan\CDR4.0\Logs\Runs\rollback-v4.4.0-*`

فایل‌های رمز، تنظیمات local، لاگ، `mappingfile.xlsx` و کلید خصوصی داخل بسته انتشار قرار نمی‌گیرند.
