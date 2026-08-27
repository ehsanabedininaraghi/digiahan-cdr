WITH RawLegs AS
(
    SELECT
        CallKey=COALESCE(NULLIF(LinkedId,N''),NULLIF(UniqueId,N''),CONVERT(nvarchar(30),RawCDRId)),
        IsInbound=CASE WHEN NULLIF(Did,N'') IS NOT NULL OR Dcontext LIKE N'%from-trunk%' THEN 1 ELSE 0 END,
        IsOutbound=CASE WHEN Dcontext LIKE N'%from-internal%' OR Dcontext LIKE N'%outbound%' THEN 1 ELSE 0 END,
        IsAnswered=CASE WHEN Disposition=N'ANSWERED' OR ISNULL(Billsec,0)>0 THEN 1 ELSE 0 END,
        AnsweredExtension=CASE
            WHEN (Disposition=N'ANSWERED' OR ISNULL(Billsec,0)>0) AND Dst LIKE N'[0-9][0-9][0-9]' THEN Dst
            WHEN (Disposition=N'ANSWERED' OR ISNULL(Billsec,0)>0) AND Src LIKE N'[0-9][0-9][0-9]' THEN Src
        END,
        OutboundExtension=CASE
            WHEN (Dcontext LIKE N'%from-internal%' OR Dcontext LIKE N'%outbound%')
             AND Src LIKE N'[0-9][0-9][0-9]' THEN Src
        END,
        Billsec=ISNULL(Billsec,0)
    FROM dbo.RawCDR
    WHERE ReceivedAtUtc>=@s AND ReceivedAtUtc<@e
),
Calls AS
(
    SELECT
        CallKey,
        IsInbound=MAX(IsInbound),
        IsOutbound=MAX(IsOutbound),
        IsAnswered=MAX(IsAnswered),
        AnsweredExtension=MAX(AnsweredExtension),
        OutboundExtension=MAX(OutboundExtension),
        Billsec=MAX(Billsec)
    FROM RawLegs
    GROUP BY CallKey
),
Attributed AS
(
    SELECT
        Extension=CASE
            WHEN IsInbound=1 THEN AnsweredExtension
            WHEN IsOutbound=1 THEN OutboundExtension
            ELSE COALESCE(AnsweredExtension,OutboundExtension)
        END,
        IsInbound,
        IsOutbound,
        IsAnswered,
        Billsec
    FROM Calls
)
SELECT TOP(100)
    E=Extension,
    T=COUNT(*),
    I=SUM(CASE WHEN IsInbound=1 THEN 1 ELSE 0 END),
    O=SUM(CASE WHEN IsOutbound=1 THEN 1 ELSE 0 END),
    A=SUM(IsAnswered),
    M=SUM(CASE WHEN IsOutbound=1 AND IsAnswered=0 THEN 1 ELSE 0 END),
    B=SUM(Billsec),
    Av=CASE WHEN SUM(IsAnswered)>0 THEN SUM(Billsec)/SUM(IsAnswered) ELSE 0 END
FROM Attributed
WHERE Extension IS NOT NULL
GROUP BY Extension
ORDER BY T DESC,Extension
OPTION(RECOMPILE);
