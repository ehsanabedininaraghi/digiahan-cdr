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
    SELECT DISTINCT K FROM Raw
    WHERE @ext=N'all' OR Src=@ext OR Dst=@ext
),
O AS
(
    SELECT
        r.K,
        H=DATEPART(HOUR,MIN(r.Calldate)),
        A=MAX(CASE WHEN r.Disposition=N'ANSWERED' OR r.Billsec>0 THEN 1 ELSE 0 END)
    FROM Raw r
    INNER JOIN EligibleKeys e ON e.K=r.K
    GROUP BY r.K
)
SELECT H,COUNT(*) T,SUM(A) A,SUM(CASE WHEN A=0 THEN 1 ELSE 0 END) M
FROM O
GROUP BY H
ORDER BY H;