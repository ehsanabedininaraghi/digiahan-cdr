# DigiAhan CDR v4.3.1 Hotfix

Release date: 2026-08-08

## Hotfixes

- Separates compilation of the new `PrimaryMobilePhoneId` column, foreign key and filtered index so SQL Server can complete the migration.
- Makes the migration idempotent and able to recover from a partially completed v4.3.0 attempt.
- Stops orphan `DigiAhan.CDR.Receiver` processes before build, preventing locked executable failures.

## Invoice customer notification MVP

- Imports `factor.description` into `AccountingInvoices.FactorDescription`.
- Extracts delivery voucher numbers such as `حواله: 40416/20`.
- Connects invoices to the existing master `IdentityId`; no duplicate customer table was introduced.
- Adds `InvoiceNotifications` and append-only `InvoiceNotificationAttempts` history.
- Adds one explicit `PrimaryMobilePhoneId` per customer identity while keeping all existing phones.
- Blocks ambiguous mobile numbers that belong to more than one identity.
- Generates 256-bit random public links and stores only SHA-256 token hashes.
- Adds the management page `ارسال اطلاعات خرید مشتری`.
- Generates an SMS-ready message for manual copy/send and records manual completion.
- Adds a public order page containing product, voucher and purchase date only; no amount, accounting code or database ID is exposed.
- Adds per-extension inbound and outbound call counts to the management dashboard.

## Deliberately deferred

- Automatic SMS provider integration.
- DigiAhan.com login/registration integration.
- Supplier and warehouse fields; the current accounting flow has no warehouse source.

## Compatibility and safety

- Existing v4.2 identity, CDR, Didar, mapping and accounting tables remain compatible.
- No destructive migration is used.
- The installer backs up every replaced file before installation.
- Existing `appsettings.DataGathering.local.json` values are preserved and extended.
