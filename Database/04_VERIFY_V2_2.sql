USE [digiahan_cdr];
GO
SET NOCOUNT ON;

SELECT N'Didar contacts' AS Metric, COUNT(*) AS Value FROM dbo.DidarContacts WHERE IsDeleted=0
UNION ALL SELECT N'All extracted phones', COUNT(*) FROM dbo.DidarContactPhones
UNION ALL SELECT N'Mobile phones', COUNT(*) FROM dbo.DidarContactPhones WHERE PhoneType=N'Mobile'
UNION ALL SELECT N'Landline phones', COUNT(*) FROM dbo.DidarContactPhones WHERE PhoneType=N'Landline'
UNION ALL SELECT N'Company phones', COUNT(*) FROM dbo.DidarContactPhones WHERE PhoneType=N'Company'
UNION ALL SELECT N'Fax phones', COUNT(*) FROM dbo.DidarContactPhones WHERE PhoneType=N'Fax'
UNION ALL SELECT N'Other phones', COUNT(*) FROM dbo.DidarContactPhones WHERE PhoneType IN (N'Other',N'Other2');

SELECT TOP (100)
    p.DidarContactCode,d.FullName,d.CompanyName,p.OriginalPhone,p.NormalizedPhone,p.PhoneType,p.SourceColumn,p.IsPrimary
FROM dbo.DidarContactPhones p
JOIN dbo.DidarContacts d ON d.DidarContactCode=p.DidarContactCode
ORDER BY p.Id DESC;

SELECT TOP (100)
    r.Calldate,r.Src,r.Dst,x.CustomerPhone,
    p.PhoneType,p.OriginalPhone,d.FullName,d.CompanyName,d.OwnerName
FROM dbo.RawCDR r
CROSS APPLY (SELECT CustomerPhone=CASE
    WHEN NULLIF(r.Did,N'') IS NOT NULL OR r.Dcontext LIKE N'%from-trunk%' THEN r.Src
    WHEN r.Dcontext LIKE N'%from-internal%' OR r.Dcontext LIKE N'%outbound%' THEN r.Dst
    WHEN LEN(ISNULL(r.Src,N''))>4 THEN r.Src
    WHEN LEN(ISNULL(r.Dst,N''))>4 THEN r.Dst END) x
OUTER APPLY
(
    SELECT TOP(1) pp.* FROM dbo.DidarContactPhones pp
    WHERE pp.NormalizedPhone=dbo.NormalizeIranPhone(x.CustomerPhone)
    ORDER BY pp.IsPrimary DESC,pp.Id
) p
LEFT JOIN dbo.DidarContacts d ON d.DidarContactCode=p.DidarContactCode AND d.IsDeleted=0
ORDER BY r.Calldate DESC;
GO
