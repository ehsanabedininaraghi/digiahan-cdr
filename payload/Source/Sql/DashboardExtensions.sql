WITH X AS
(
    SELECT
        E=CASE
            WHEN Src LIKE N'[0-9][0-9][0-9]' THEN Src
            WHEN Dst LIKE N'[0-9][0-9][0-9]' THEN Dst
        END,
        K=COALESCE(NULLIF(LinkedId,N''),NULLIF(UniqueId,N''),CONVERT(nvarchar(30),RawCDRId)),
        Disposition,
        Billsec
    FROM dbo.RawCDR
    WHERE Calldate>=@s AND Calldate<@e
),
O AS
(
    SELECT
        E,K,
        A=MAX(CASE WHEN Disposition=N'ANSWERED' OR Billsec>0 THEN 1 ELSE 0 END),
        B=MAX(ISNULL(Billsec,0))
    FROM X
    WHERE E IS NOT NULL
    GROUP BY E,K
)
SELECT TOP(100)
    E,
    COUNT(*) T,
    SUM(A) A,
    SUM(CASE WHEN A=0 THEN 1 ELSE 0 END) M,
    SUM(B) B,
    CASE WHEN SUM(A)>0 THEN SUM(B)/SUM(A) ELSE 0 END Av
FROM O
GROUP BY E
ORDER BY T DESC,E;