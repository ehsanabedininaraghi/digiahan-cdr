SELECT TOP(1) StartedAtUtc,FinishedAtUtc,Status,InsertedCount,DuplicateCount,ErrorCount
FROM dbo.SyncBatch
ORDER BY StartedAtUtc DESC;

SELECT MAX(ReceivedAtUtc),SUM(CASE WHEN ReceivedAtUtc>=DATEADD(HOUR,-1,SYSUTCDATETIME()) THEN 1 ELSE 0 END)
FROM dbo.RawCDR;
