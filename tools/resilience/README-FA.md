# اجرای پایدار داشبورد بدون Codex

این پوشه یک Watchdog مستقل PowerShell برای ویندوز است. Codex برای اجرا، پایش یا بازیابی داشبورد لازم نیست.

## نصب روی سرور اصلی

PowerShell را با همان کاربر ویندوزی اجرا کنید که به SQL Server دسترسی دارد:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
cd D:\DigiAhan\CDR4.0\tools\resilience
.\Install-DigiAhanStartup.ps1
```

نصب‌کننده یک Release Build می‌سازد، Watchdog را همان لحظه اجرا می‌کند و یک لانچر در Startup کاربر فعلی قرار می‌دهد. از ورود بعدی به ویندوز، نگهبان مخفیانه اجرا می‌شود.

## رفتار بازیابی

- هر ۱۵ ثانیه `/health` بررسی می‌شود.
- سه خطای متوالی به‌عنوان خرابی واقعی در نظر گرفته می‌شود.
- اگر برنامه متوقف شده باشد، فایل Release موجود اجرا می‌شود؛ هیچ Build خودکاری وسط ساعت کاری انجام نمی‌شود.
- اگر پردازش ناسالم پورت 5088 را اشغال کرده باشد، همان پردازش متوقف و نسخه سالم اجرا می‌شود.
- لاگ‌ها در `D:\DigiAhan\CDR4.0\Logs\Resilience` ذخیره و بعد از ۳۰ روز پاک می‌شوند.
- Mutex مانع اجرای هم‌زمان چند Watchdog می‌شود.

## فرمان‌های روزمره

بررسی وضعیت:

```powershell
.\Status-DigiAhan.ps1
```

اجرای یک بررسی و بازیابی دستی:

```powershell
.\DigiAhan-Watchdog.ps1 -Once
```

پس از دریافت نسخه جدید، فقط یک بار Build کنید:

```powershell
.\Build-ResilientDashboard.ps1
```

حذف از Startup (خود داشبورد متوقف نمی‌شود):

```powershell
.\Uninstall-DigiAhanStartup.ps1
```

تنظیم مسیر، زمان‌ها و آدرس‌ها در `resilience.config.json` است. اگر محل واقعی پروژه متفاوت است، هنگام نصب از `-RepositoryRoot` استفاده کنید.

> نکته: Startup بعد از ورود کاربر ویندوز اجرا می‌شود. این انتخاب برای حفظ هویت کاربر و اتصال `Integrated Security=True` امن‌تر است. اگر لازم است پیش از Login نیز اجرا شود، باید یک حساب سرویس مشخص با دسترسی SQL تعریف و سپس Windows Service یا Scheduled Task با همان حساب نصب شود.
