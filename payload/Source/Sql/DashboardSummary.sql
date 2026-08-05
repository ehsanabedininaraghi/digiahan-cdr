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
        r.K,r.Calldate,r.ReceivedAtUtc,r.Disposition,r.Billsec,
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
        MIN(Calldate) AS Calldate,
        MAX(ReceivedAtUtc) AS ReceivedAtUtc,
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
        HasContact=CASE WHEN M.IdentityId IS NULL THEN 0 ELSE 1 END
    FROM O
    OUTER APPLY
    (
        SELECT TOP(1) IdentityId,IsVerified
        FROM dbo.CustomerPhoneDirectory
        WHERE NormalizedPhone=dbo.NormalizeIranPhone(O.ExternalPhone)
        ORDER BY IsVerified DESC,IdentityId
    ) M
)
SELECT
    COUNT(*) AS T,
    ISNULL(SUM(A),0) AS A,
    ISNULL(SUM(CASE WHEN A=0 THEN 1 ELSE 0 END),0) AS M,
    ISNULL(SUM(I),0) AS I,
    ISNULL(SUM(O),0) AS O,
    ISNULL(SUM(B),0) AS B,
    CASE WHEN ISNULL(SUM(A),0)>0 THEN ISNULL(SUM(B),0)/SUM(A) ELSE 0 END AS Av,
    COUNT(DISTINCT CASE WHEN NormalizedPhone IS NOT NULL AND HasContact=1 THEN NormalizedPhone END) AS KnownCustomers,
    COUNT(DISTINCT CASE WHEN NormalizedPhone IS NOT NULL AND HasContact=0 THEN NormalizedPhone END) AS NewCustomers,
    MAX(Calldate) AS L,
    MAX(ReceivedAtUtc) AS R
FROM R;
