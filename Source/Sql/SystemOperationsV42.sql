SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.IntegrationSchedules',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.IntegrationSchedules
    (
        JobKey nvarchar(50) NOT NULL CONSTRAINT PK_IntegrationSchedules PRIMARY KEY,
        DisplayName nvarchar(200) NOT NULL,
        IntervalMinutes int NOT NULL,
        IsEnabled bit NOT NULL CONSTRAINT DF_IntegrationSchedules_Enabled DEFAULT(1),
        LastStartedAtUtc datetime2(0) NULL,
        LastFinishedAtUtc datetime2(0) NULL,
        LastStatus nvarchar(20) NULL,
        LastDurationMs bigint NULL,
        LastError nvarchar(2000) NULL,
        NextRunAtUtc datetime2(0) NOT NULL,
        ConsecutiveFailures int NOT NULL CONSTRAINT DF_IntegrationSchedules_Failures DEFAULT(0),
        UpdatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_IntegrationSchedules_Updated DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT CK_IntegrationSchedules_Interval CHECK(IntervalMinutes BETWEEN 1 AND 10080)
    );
    CREATE INDEX IX_IntegrationSchedules_Due
        ON dbo.IntegrationSchedules(IsEnabled,NextRunAtUtc)
        INCLUDE(JobKey,IntervalMinutes,LastStatus);
END;

MERGE dbo.IntegrationSchedules AS target
USING (VALUES
    (N'ACCOUNTING',N'حسابداری و فاکتورها',10,1),
    (N'DIDAR_IDENTITY',N'دیدار و هویت مشتری',10,1),
    (N'ISSABEL_MONITOR',N'کنترل دریافت اطلاعات ایزابل',1,1),
    (N'MAPPING_FILE',N'فایل اتصال کد حسابداری',1440,1),
    (N'DATABASE_MAINTENANCE',N'نگهداری پایگاه داده و لاگ',1440,1)
) AS source(JobKey,DisplayName,IntervalMinutes,IsEnabled)
ON target.JobKey=source.JobKey
WHEN NOT MATCHED THEN
    INSERT(JobKey,DisplayName,IntervalMinutes,IsEnabled,NextRunAtUtc)
    VALUES(source.JobKey,source.DisplayName,source.IntervalMinutes,source.IsEnabled,SYSUTCDATETIME());

IF OBJECT_ID(N'dbo.IntegrationJobRuns',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.IntegrationJobRuns
    (
        RunId uniqueidentifier NOT NULL CONSTRAINT PK_IntegrationJobRuns PRIMARY KEY,
        JobKey nvarchar(50) NOT NULL,
        StartedAtUtc datetime2(0) NOT NULL,
        FinishedAtUtc datetime2(0) NULL,
        Status nvarchar(20) NOT NULL,
        DurationMs bigint NULL,
        Detail nvarchar(2000) NULL,
        CONSTRAINT FK_IntegrationJobRuns_Schedule FOREIGN KEY(JobKey)
            REFERENCES dbo.IntegrationSchedules(JobKey)
    );
    CREATE INDEX IX_IntegrationJobRuns_JobStarted
        ON dbo.IntegrationJobRuns(JobKey,StartedAtUtc DESC);
END;

IF OBJECT_ID(N'dbo.RawCDR',N'U') IS NOT NULL
BEGIN
    IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.RawCDR') AND name=N'IX_RawCDR_Calldate_LinkedId')
        CREATE INDEX IX_RawCDR_Calldate_LinkedId ON dbo.RawCDR(Calldate,LinkedId) INCLUDE(UniqueId,Src,Dst,Disposition,Billsec,Duration,Did,Dcontext,ReceivedAtUtc);
    IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.RawCDR') AND name=N'IX_RawCDR_ReceivedAtUtc')
        CREATE INDEX IX_RawCDR_ReceivedAtUtc ON dbo.RawCDR(ReceivedAtUtc DESC) INCLUDE(Calldate);
    IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.RawCDR') AND name=N'IX_RawCDR_ReceivedAtUtc_Dashboard')
        CREATE INDEX IX_RawCDR_ReceivedAtUtc_Dashboard ON dbo.RawCDR(ReceivedAtUtc,LinkedId)
        INCLUDE(UniqueId,Src,Dst,Disposition,Billsec,Duration,Did,Dcontext,RecordingFile,RawCDRId);
END;

IF OBJECT_ID(N'dbo.AgentCallOutcomes',N'U') IS NOT NULL
AND NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.AgentCallOutcomes') AND name=N'IX_AgentCallOutcomes_CreatedExtensionOutcome')
    CREATE INDEX IX_AgentCallOutcomes_CreatedExtensionOutcome
        ON dbo.AgentCallOutcomes(CreatedAtUtc,Extension,Outcome) INCLUDE(Note,FollowUpAt);

IF OBJECT_ID(N'dbo.AccountingInvoices',N'U') IS NOT NULL
AND NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.AccountingInvoices') AND name=N'IX_AccountingInvoices_RangeVisitor')
    CREATE INDEX IX_AccountingInvoices_RangeVisitor
        ON dbo.AccountingInvoices(FactorDate,VisitorId) INCLUDE(FactorCode,FactorNumber,CustomerDetailCode,CustomerName,Amount,VisitorName);
