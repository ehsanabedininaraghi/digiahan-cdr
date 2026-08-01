USE [DigiAhan_CDR];
GO

PRINT N'=== V3.1 prerequisites ===';

SELECT
    DB_NAME() AS CurrentDatabase,
    OBJECT_ID(N'dbo.RawCDR',N'U') AS RawCDR,
    OBJECT_ID(N'dbo.DidarContacts',N'U') AS DidarContacts,
    OBJECT_ID(N'dbo.DidarContactPhones',N'U') AS DidarContactPhones,
    OBJECT_ID(N'dbo.NormalizeIranPhone',N'FN') AS NormalizeIranPhone;
GO

PRINT N'=== Duplicate normalized phones (informational) ===';
SELECT TOP(20)
    NormalizedPhone,
    COUNT(*) AS ContactLinks
FROM dbo.DidarContactPhones
WHERE NULLIF(NormalizedPhone,N'') IS NOT NULL
GROUP BY NormalizedPhone
HAVING COUNT(*)>1
ORDER BY ContactLinks DESC,NormalizedPhone;
GO

PRINT N'=== Sample linked calls: all legs must resolve to same external phone ===';
WITH R AS
(
    SELECT TOP(1000)
        r.RawCDRId,r.Calldate,r.Src,r.Dst,r.LinkedId,r.UniqueId,r.Did,r.Dcontext,
        K=COALESCE(NULLIF(r.LinkedId,N''),NULLIF(r.UniqueId,N''),CONVERT(nvarchar(30),r.RawCDRId))
    FROM dbo.RawCDR r
    ORDER BY r.Calldate DESC
),
G AS
(
    SELECT
        K,
        ExternalPhone=MAX(CASE
            WHEN (NULLIF(Did,N'') IS NOT NULL OR Dcontext LIKE N'%from-trunk%') AND LEN(ISNULL(Src,N''))>4 THEN Src
            WHEN (Dcontext LIKE N'%from-internal%' OR Dcontext LIKE N'%outbound%') AND LEN(ISNULL(Dst,N''))>4 THEN Dst
            WHEN LEN(ISNULL(Src,N''))>4 THEN Src
            WHEN LEN(ISNULL(Dst,N''))>4 THEN Dst
        END)
    FROM R
    GROUP BY K
)
SELECT TOP(30)
    r.Calldate,r.Src,r.Dst,r.LinkedId,g.ExternalPhone,
    dc.FullName,dc.CompanyName
FROM R r
INNER JOIN G g ON g.K=r.K
OUTER APPLY
(
    SELECT TOP(1) d.FullName,d.CompanyName
    FROM dbo.DidarContactPhones p
    INNER JOIN dbo.DidarContacts d
        ON d.DidarContactCode=p.DidarContactCode
       AND d.IsDeleted=0
    WHERE p.NormalizedPhone=dbo.NormalizeIranPhone(g.ExternalPhone)
    ORDER BY p.IsPrimary DESC,p.Id
) dc
ORDER BY r.Calldate DESC;
GO
