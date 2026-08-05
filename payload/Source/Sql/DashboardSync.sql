SELECT TOP(1) StartedAtUtc,FinishedAtUtc,Status,InsertedCount,DuplicateCount,ErrorCount
FROM dbo.SyncBatch
ORDER BY StartedAtUtc DESC;

SELECT
    MAX(ReceivedAtUtc) AS LastReceivedAtUtc,
    MAX(Calldate) AS LastCdrAt,
    SUM(CASE WHEN ReceivedAtUtc>=DATEADD(HOUR,-1,SYSUTCDATETIME()) THEN 1 ELSE 0 END) AS RowsLastHour
FROM dbo.RawCDR;
