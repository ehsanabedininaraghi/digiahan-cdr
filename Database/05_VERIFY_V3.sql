USE [DigiAhan_CDR];
GO
SELECT @@VERSION AS SqlServerVersion;
SELECT compatibility_level FROM sys.databases WHERE name=DB_NAME();
SELECT OBJECT_ID(N'dbo.RawCDR',N'U') RawCDR,
       OBJECT_ID(N'dbo.DidarContacts',N'U') DidarContacts,
       OBJECT_ID(N'dbo.DidarContactPhones',N'U') DidarContactPhones,
       OBJECT_ID(N'dbo.NormalizeIranPhone',N'FN') NormalizeIranPhone;
SELECT COUNT(*) Contacts FROM dbo.DidarContacts;
SELECT COUNT(*) Phones FROM dbo.DidarContactPhones;
SELECT TOP 20 p.NormalizedPhone,c.FullName,c.CompanyName,c.OwnerName
FROM dbo.DidarContactPhones p
JOIN dbo.DidarContacts c ON c.DidarContactCode=p.DidarContactCode
ORDER BY p.Id DESC;
GO
