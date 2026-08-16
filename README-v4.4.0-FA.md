# نصب امن DigiAhan CDR v4.4.0

## نصب اولیه

1. ZIP را در یک پوشه جدا Extract کنید.
2. روی `RUN-v4.4.0.cmd` راست‌کلیک و **Run as administrator** را اجرا کنید.
3. نصب‌کننده از فایل‌های فعلی backup می‌گیرد، build و Health Check انجام می‌دهد.
4. در صورت هر خطا، نسخه قبلی به‌صورت خودکار برگردانده و دوباره Health Check می‌شود.
5. پیام موفقیت به معنی نصب کد است؛ قابلیت Journey v3 همچنان خاموش می‌ماند و Seller v2 مسیر کاری بچه‌هاست.

آدرس‌های جدید پس از فعال‌سازی پایلوت:

- میزکار پایلوت فروش: `http://192.168.8.143:5088/seller-v3`
- مرکز کنترل مدیر: `http://192.168.8.143:5088/journey-control`

## فعال‌کردن پایلوت

ابتدا `SellerKey` کاربر آزمایشی را از مدیریت کاربران فروش مشخص کنید. سپس PowerShell را با دسترسی Administrator باز کنید:

```powershell
.\CONFIGURE-JOURNEY-PILOT-v4.4.0.ps1 `
  -RepositoryRoot "D:\DigiAhan\CDR4.0" `
  -Enable `
  -PilotSellerKeys "SELLER_KEY"
```

بعد برنامه را یک‌بار restart کنید. در پایلوت اول `AutoCaptureSellerInteractions` را روشن نکنید.

پس از تأیید پایلوت دستی، برای capture خودکار همان فروشنده:

```powershell
.\CONFIGURE-JOURNEY-PILOT-v4.4.0.ps1 `
  -RepositoryRoot "D:\DigiAhan\CDR4.0" `
  -Enable `
  -EnableAutoCapture `
  -PilotSellerKeys "SELLER_KEY"
```

## rollback دستی

اگر با وجود rollback خودکار نیاز به بازگشت دستی بود، `ROLLBACK-v4.4.0.cmd` را با Administrator اجرا کنید. آخرین backup نسخه 4.4.0 انتخاب می‌شود، نسخه قبلی build و اجرا می‌شود و سلامت آن کنترل خواهد شد.

## فایل‌های تشخیصی

- نصب: `D:\DigiAhan\CDR4.0\Logs\Runs\v4.4.0-*`
- rollback: `D:\DigiAhan\CDR4.0\Logs\Runs\rollback-v4.4.0-*`

ZIP این پوشه‌ها را برای بررسی ارسال کنید؛ فایل رمز یا کلید خصوصی داخل بسته انتشار قرار نمی‌گیرد.
