# v3.7.4 Changelog

## Recovery
- Dashboard starts before accounting import.
- Dashboard remains available when accounting sync or diagnostics fail.
- Added standalone dashboard start command.

## Accounting
- Replaced invalid `CutoffDate` usage with actual `CutoffPersianDate`.
- Direct `SQLOLEDB` connection with `sa`.
- Accounting schema executes before snapshot import.
- Compatibility migration copies old cutoff data when necessary.

## Didar and Identity
- Uses actual `DidarContactPhones.OriginalPhone`.
- Reads all phone-like fields from `DidarContacts`.
- Resolves full and short accounting codes.
- Preserves verified manual mappings.

## Operations
- Full source backup.
- Rollback command.
- 15-minute scheduled accounting sync.
- End-to-end diagnostics and log files.
