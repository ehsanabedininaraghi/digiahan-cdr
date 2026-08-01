USE [digiahan_cdr];
GO

-- پیش‌نیازهای داشبورد دیدار
SELECT
    CASE WHEN OBJECT_ID(N'dbo.DidarContacts', N'U') IS NOT NULL THEN N'OK' ELSE N'MISSING' END AS DidarContacts,
    CASE WHEN OBJECT_ID(N'dbo.DidarContactPhones', N'U') IS NOT NULL THEN N'OK' ELSE N'MISSING' END AS DidarContactPhones,
    CASE WHEN OBJECT_ID(N'dbo.NormalizeIranPhone', N'FN') IS NOT NULL THEN N'OK' ELSE N'MISSING' END AS NormalizeIranPhone;

SELECT COUNT(*) AS ContactCount FROM dbo.DidarContacts WHERE IsDeleted = 0;
SELECT COUNT(*) AS PhoneCount FROM dbo.DidarContactPhones;

-- نمونه تطبیق شماره تماس با مخاطب دیدار
SELECT TOP (50)
    r.Calldate,
    r.Src,
    r.Dst,
    x.CustomerPhone,
    d.FullName,
    d.CompanyName,
    d.OwnerName,
    CASE
        WHEN NULLIF(x.CustomerPhone,N'') IS NOT NULL AND d.DidarContactCode IS NULL THEN N'مشتری جدید'
        WHEN d.DidarContactCode IS NOT NULL THEN N'مخاطب دیدار'
        ELSE N'داخلی / نامشخص'
    END AS CustomerStatus
FROM dbo.RawCDR r
CROSS APPLY
(
    SELECT CustomerPhone = CASE
        WHEN NULLIF(r.Did,N'') IS NOT NULL OR r.Dcontext LIKE N'%from-trunk%' THEN r.Src
        WHEN r.Dcontext LIKE N'%from-internal%' OR r.Dcontext LIKE N'%outbound%' THEN r.Dst
        WHEN LEN(ISNULL(r.Src,N'')) > 4 THEN r.Src
        WHEN LEN(ISNULL(r.Dst,N'')) > 4 THEN r.Dst
    END
) x
OUTER APPLY
(
    SELECT TOP (1) dc.*
    FROM dbo.DidarContactPhones p
    INNER JOIN dbo.DidarContacts dc
        ON dc.DidarContactCode = p.DidarContactCode
       AND dc.IsDeleted = 0
    WHERE p.NormalizedPhone = dbo.NormalizeIranPhone(x.CustomerPhone)
    ORDER BY p.IsPrimary DESC, p.Id ASC
) d
ORDER BY r.Calldate DESC;
GO
