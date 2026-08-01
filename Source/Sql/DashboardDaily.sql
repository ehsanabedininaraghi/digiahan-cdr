WITH Raw AS
(
    SELECT
        r.*,
        K=COALESCE(NULLIF(r.LinkedId,N''),NULLIF(r.UniqueId,N''),CONVERT(nvarchar(30),r.RawCDRId))
    FROM dbo.RawCDR r
    WHERE r.Calldate>=@s AND r.Calldate<@e
),
EligibleKeys AS
(
    SELECT DISTINCT K
    FROM Raw
    WHERE @ext=N'all' OR Src=@ext OR Dst=@ext
),
C AS
(
    SELECT
        r.K,
        D=CONVERT(date,MIN(r.Calldate) OVER(PARTITION BY r.K)),
        r.Disposition,
        r.Billsec,
        Dir=CASE
            WHEN NULLIF(r.Did,N'') IS NOT NULL OR r.Dcontext LIKE N'%from-trunk%' THEN N'in'
            WHEN r.Dcontext LIKE N'%from-internal%' OR r.Dcontext LIKE N'%outbound%' THEN N'out'
            ELSE N'u'
        END,
        ExternalPhone=CASE
            WHEN (NULLIF(r.Did,N'') IS NOT NULL OR r.Dcontext LIKE N'%from-trunk%')
                 AND LEN(ISNULL(r.Src,N''))>4 THEN r.Src
            WHEN (r.Dcontext LIKE N'%from-internal%' OR r.Dcontext LIKE N'%outbound%')
                 AND LEN(ISNULL(r.Dst,N''))>4 THEN r.Dst
            WHEN LEN(ISNULL(r.Src,N''))>4 THEN r.Src
            WHEN LEN(ISNULL(r.Dst,N''))>4 THEN r.Dst
        END
    FROM Raw r
    INNER JOIN EligibleKeys e ON e.K=r.K
),
O AS
(
    SELECT
        K,
        MIN(D) AS D,
        MAX(CASE WHEN Disposition=N'ANSWERED' OR Billsec>0 THEN 1 ELSE 0 END) AS A,
        MAX(ISNULL(Billsec,0)) AS B,
        MAX(CASE WHEN Dir=N'in' THEN 1 ELSE 0 END) AS I,
        MAX(CASE WHEN Dir=N'out' THEN 1 ELSE 0 END) AS O,
        MAX(NULLIF(ExternalPhone,N'')) AS ExternalPhone
    FROM C
    GROUP BY K
),
R AS
(
    SELECT
        O.*,
        NormalizedPhone=dbo.NormalizeIranPhone(O.ExternalPhone),
        HasContact=CASE WHEN M.DidarContactCode IS NULL THEN 0 ELSE 1 END
    FROM O
    OUTER APPLY
    (
        SELECT TOP(1) p.DidarContactCode
        FROM dbo.DidarContactPhones p
        INNER JOIN dbo.DidarContacts d
            ON d.DidarContactCode=p.DidarContactCode
           AND d.IsDeleted=0
        WHERE p.NormalizedPhone=dbo.NormalizeIranPhone(O.ExternalPhone)
        ORDER BY p.IsPrimary DESC,p.Id ASC
    ) M
)
SELECT
    D,
    COUNT(*) AS T,
    SUM(A) AS A,
    SUM(CASE WHEN A=0 THEN 1 ELSE 0 END) AS M,
    SUM(I) AS I,
    SUM(O) AS O,
    COUNT(DISTINCT CASE WHEN NormalizedPhone IS NOT NULL AND HasContact=0 THEN NormalizedPhone END) AS NewCustomers,
    COUNT(DISTINCT CASE WHEN NormalizedPhone IS NOT NULL AND HasContact=1 THEN NormalizedPhone END) AS KnownCustomers,
    SUM(B) AS B
FROM R
GROUP BY D
ORDER BY D;