SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'dbo.AiRecordingAssets',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AiRecordingAssets
    (
        RecordingAssetId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_AiRecordingAssets PRIMARY KEY,
        DiscoveryKey char(64) NOT NULL,
        SourceServer nvarchar(100) NOT NULL,
        OriginalFileName nvarchar(1000) NOT NULL,
        SourceRelativePath nvarchar(1000) NULL,
        CallDate date NOT NULL,
        StorageKey nvarchar(1000) NULL,
        RemoteFileSizeBytes bigint NULL,
        FileSizeBytes bigint NULL,
        RemoteObservedAtUtc datetime2(3) NULL,
        RemoteIsStable bit NULL,
        Sha256 char(64) NULL,
        DurationSeconds decimal(12,3) NULL,
        ProcessingStatus nvarchar(40) NOT NULL
            CONSTRAINT DF_AiRecordingAssets_Status DEFAULT(N'DISCOVERED'),
        AttemptCount int NOT NULL CONSTRAINT DF_AiRecordingAssets_Attempts DEFAULT(0),
        LastAttemptAtUtc datetime2(3) NULL,
        NextRetryAtUtc datetime2(3) NULL,
        LeaseOwner nvarchar(200) NULL,
        LeaseExpiresAtUtc datetime2(3) NULL,
        DownloadedAtUtc datetime2(3) NULL,
        CompletedAtUtc datetime2(3) NULL,
        PurgedAtUtc datetime2(3) NULL,
        LastError nvarchar(2000) NULL,
        CreatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_AiRecordingAssets_Created DEFAULT(SYSUTCDATETIME()),
        UpdatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_AiRecordingAssets_Updated DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT UX_AiRecordingAssets_DiscoveryKey UNIQUE(DiscoveryKey),
        CONSTRAINT CK_AiRecordingAssets_Status CHECK
        (
            ProcessingStatus IN
            (N'DISCOVERED',N'SOURCE_LOOKUP_PENDING',N'SOURCE_MISSING',N'WAITING_FOR_STABLE_FILE',
             N'FETCHING',N'READY_FOR_AI',N'TRANSCRIBING',N'ANALYZING',N'COMPLETED',N'RETRY',
             N'VALIDATION_FAILED',N'QUARANTINED',N'LOCAL_PURGED')
        ),
        CONSTRAINT CK_AiRecordingAssets_Size CHECK
            ((RemoteFileSizeBytes IS NULL OR RemoteFileSizeBytes>=0) AND
             (FileSizeBytes IS NULL OR FileSizeBytes>=0))
    );
    CREATE INDEX IX_AiRecordingAssets_Work
        ON dbo.AiRecordingAssets(ProcessingStatus,NextRetryAtUtc,LeaseExpiresAtUtc,CallDate)
        INCLUDE(SourceServer,OriginalFileName,SourceRelativePath,StorageKey,AttemptCount);
END;

IF OBJECT_ID(N'dbo.AiLogicalCallRecordings',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AiLogicalCallRecordings
    (
        LogicalCallId bigint NOT NULL,
        RunId bigint NOT NULL,
        RecordingAssetId bigint NOT NULL,
        IsPrimary bit NOT NULL CONSTRAINT DF_AiLogicalCallRecordings_Primary DEFAULT(0),
        CreatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_AiLogicalCallRecordings_Created DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT PK_AiLogicalCallRecordings PRIMARY KEY(LogicalCallId,RecordingAssetId),
        CONSTRAINT FK_AiLogicalCallRecordings_Call FOREIGN KEY(LogicalCallId)
            REFERENCES dbo.AiLogicalCalls(LogicalCallId),
        CONSTRAINT FK_AiLogicalCallRecordings_Run FOREIGN KEY(RunId)
            REFERENCES dbo.AiPipelineRuns(RunId),
        CONSTRAINT FK_AiLogicalCallRecordings_Asset FOREIGN KEY(RecordingAssetId)
            REFERENCES dbo.AiRecordingAssets(RecordingAssetId)
    );
    CREATE UNIQUE INDEX UX_AiLogicalCallRecordings_Primary
        ON dbo.AiLogicalCallRecordings(LogicalCallId) WHERE IsPrimary=1;
    CREATE UNIQUE INDEX UX_AiLogicalCallRecordings_Run
        ON dbo.AiLogicalCallRecordings(RunId);
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_AiDiscoverRecordingAssets
    @TargetDate date,
    @SourceServer nvarchar(100),
    @BatchSize int=100
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET @BatchSize=CASE WHEN @BatchSize<1 THEN 1 WHEN @BatchSize>1000 THEN 1000 ELSE @BatchSize END;
    DECLARE @Now datetime2(3)=SYSUTCDATETIME(),@Assets int=0,@Links int=0;

    CREATE TABLE #Candidates
    (
        LogicalCallId bigint NOT NULL,
        RunId bigint NOT NULL,
        DiscoveryKey char(64) NOT NULL,
        OriginalFileName nvarchar(1000) NOT NULL,
        CallDate date NOT NULL
    );

    INSERT #Candidates
    SELECT TOP(@BatchSize)
        c.LogicalCallId,
        r.RunId,
        CONVERT(char(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),
            CONCAT(@SourceServer,N'|',LTRIM(RTRIM(r.RecordingFile))))),2),
        LTRIM(RTRIM(r.RecordingFile)),
        CONVERT(date,c.StartedAt)
    FROM dbo.AiLogicalCalls c
    JOIN dbo.AiPipelineRuns r ON r.LogicalCallId=c.LogicalCallId
        AND r.SourceMaxRawCdrId=c.SourceMaxRawCdrId
    WHERE c.PipelineState=N'FINALIZED'
      AND CONVERT(date,c.StartedAt)=@TargetDate
      AND NULLIF(LTRIM(RTRIM(r.RecordingFile)),N'') IS NOT NULL
      AND NOT EXISTS(SELECT 1 FROM dbo.AiLogicalCallRecordings x WHERE x.RunId=r.RunId)
    ORDER BY c.LogicalCallId;

    BEGIN TRANSACTION;
    INSERT dbo.AiRecordingAssets
        (DiscoveryKey,SourceServer,OriginalFileName,CallDate,ProcessingStatus,UpdatedAtUtc)
    SELECT DISTINCT x.DiscoveryKey,@SourceServer,x.OriginalFileName,x.CallDate,N'DISCOVERED',@Now
    FROM #Candidates x
    WHERE NOT EXISTS(SELECT 1 FROM dbo.AiRecordingAssets a WITH(UPDLOCK,SERIALIZABLE)
                     WHERE a.DiscoveryKey=x.DiscoveryKey);
    SET @Assets=@@ROWCOUNT;

    INSERT dbo.AiLogicalCallRecordings(LogicalCallId,RunId,RecordingAssetId,IsPrimary)
    SELECT x.LogicalCallId,x.RunId,a.RecordingAssetId,1
    FROM #Candidates x JOIN dbo.AiRecordingAssets a ON a.DiscoveryKey=x.DiscoveryKey
    WHERE NOT EXISTS(SELECT 1 FROM dbo.AiLogicalCallRecordings l WHERE l.RunId=x.RunId);
    SET @Links=@@ROWCOUNT;
    COMMIT TRANSACTION;

    SELECT @Assets AS AssetsDiscovered,@Links AS CallsLinked,@Now AS ExecutedAtUtc;
END;
GO
