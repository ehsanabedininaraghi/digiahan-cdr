# نصب DigiAhan CDR v4.3.10

1. ZIP نسخه را Extract کنید.
2. روی `RUN-v4.3.10.cmd` راست‌کلیک و **Run as administrator** را اجرا کنید.
3. یک رمز جدید حداقل ۸ کاراکتری برای داشبورد مدیریت وارد کنید.
4. تا پیام `v4.3.10 installed successfully` پنجره را نبندید.

آدرس‌ها:

- داشبورد مدیریت: `http://192.168.8.143:5088/dashboard`
- میزکار فروش: `http://192.168.8.143:5088/seller-v2/`
- داشبورد مستقل پیامک: `http://192.168.8.143:5088/sms-dashboard/`

ورود فروشنده از `appsettings.SellerWorkspace.local.json` ساخته می‌شود. اگر `Username` تعریف نشده باشد، مقدار `Key` مانند `seller-215` نام کاربری است. رمز اولیه به‌ترتیب از `InitialPassword` و سپس `AccessToken` قبلی خوانده می‌شود و در SQL به‌صورت PBKDF2 ذخیره می‌گردد.

نصب‌کننده از فایل‌های تغییرکرده پشتیبان می‌گیرد و نتیجه و ZIP عیب‌یابی را در `D:\DigiAhan\CDR4.0\Logs\Runs` می‌گذارد.
