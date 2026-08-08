SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'dbo.AiLogicalCalls', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AiLogicalCalls
    (
        LogicalCallId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_AiLogicalCalls PRIMARY KEY,
        CallKey nvarchar(310) NOT NULL,
        LinkedId nvarchar(150) NULL,
        StartedAt datetime2(3) NULL,
        LastLegAt datetime2(3) NULL,
        LastReceivedAtUtc datetime2(3) NOT NULL,
        StableAfterUtc datetime2(3) NOT NULL,
        LegCount int NOT NULL,
        SourceMaxRawCdrId bigint NOT NULL,
        MaxDurationSeconds int NULL,
        MaxBillsecSeconds int NULL,
        RecordingFile nvarchar(500) NULL,
        RecordingReferenceCount int NOT NULL,
        PipelineState nvarchar(20) NOT NULL,
        FinalizedAtUtc datetime2(3) NULL,
        CreatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_AiLogicalCalls_Created DEFAULT(SYSUTCDATETIME()),
        UpdatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_AiLogicalCalls_Updated DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT UX_AiLogicalCalls_CallKey UNIQUE(CallKey),
        CONSTRAINT CK_AiLogicalCalls_State CHECK
            (PipelineState IN (N'DISCOVERING', N'STABILIZING', N'FINALIZED', N'REOPENED'))
    );
    CREATE INDEX IX_AiLogicalCalls_StateStable
        ON dbo.AiLogicalCalls(PipelineState, StableAfterUtc)
        INCLUDE(RecordingFile, SourceMaxRawCdrId);
END;

IF OBJECT_ID(N'dbo.AiPipelineRuns', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AiPipelineRuns
    (
        RunId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_AiPipelineRuns PRIMARY KEY,
        LogicalCallId bigint NOT NULL,
        RunNumber int NOT NULL,
        SourceMaxRawCdrId bigint NOT NULL,
        RecordingFile nvarchar(500) NOT NULL,
        RunStatus nvarchar(20) NOT NULL CONSTRAINT DF_AiPipelineRuns_Status DEFAULT(N'QUEUED'),
        AttemptCount int NOT NULL CONSTRAINT DF_AiPipelineRuns_Attempts DEFAULT(0),
        LeaseOwner nvarchar(200) NULL,
        LeaseExpiresAtUtc datetime2(3) NULL,
        Engine nvarchar(100) NULL,
        ModelName nvarchar(200) NULL,
        LastError nvarchar(2000) NULL,
        QueuedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_AiPipelineRuns_Queued DEFAULT(SYSUTCDATETIME()),
        StartedAtUtc datetime2(3) NULL,
        CompletedAtUtc datetime2(3) NULL,
        UpdatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_AiPipelineRuns_Updated DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT FK_AiPipelineRuns_Call FOREIGN KEY(LogicalCallId)
            REFERENCES dbo.AiLogicalCalls(LogicalCallId),
        CONSTRAINT UX_AiPipelineRuns_Source UNIQUE(LogicalCallId, SourceMaxRawCdrId),
        CONSTRAINT UX_AiPipelineRuns_Number UNIQUE(LogicalCallId, RunNumber),
        CONSTRAINT CK_AiPipelineRuns_Status CHECK
            (RunStatus IN (N'QUEUED', N'LEASED', N'TRANSCRIBING', N'COMPLETED', N'FAILED', N'CANCELLED'))
    );
    CREATE INDEX IX_AiPipelineRuns_Queue
        ON dbo.AiPipelineRuns(RunStatus, LeaseExpiresAtUtc, QueuedAtUtc)
        INCLUDE(LogicalCallId, RecordingFile, AttemptCount);
END;

IF OBJECT_ID(N'dbo.AiTranscripts', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AiTranscripts
    (
        TranscriptId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_AiTranscripts PRIMARY KEY,
        RunId bigint NOT NULL,
        LanguageCode nvarchar(16) NULL,
        TranscriptText nvarchar(max) NOT NULL,
        SegmentsJson nvarchar(max) NULL,
        AudioDurationSeconds decimal(12,3) NULL,
        ProcessingSeconds decimal(12,3) NULL,
        CreatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_AiTranscripts_Created DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT FK_AiTranscripts_Run FOREIGN KEY(RunId) REFERENCES dbo.AiPipelineRuns(RunId),
        CONSTRAINT UX_AiTranscripts_Run UNIQUE(RunId),
        CONSTRAINT CK_AiTranscripts_SegmentsJson CHECK
            (SegmentsJson IS NULL OR ISJSON(SegmentsJson)=1)
    );
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_AiDiscoverLogicalCalls
    @StabilizationSeconds int = 300,
    @BatchSize int = 500
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SET @StabilizationSeconds = CASE
        WHEN @StabilizationSeconds < 60 THEN 60
        WHEN @StabilizationSeconds > 86400 THEN 86400
        ELSE @StabilizationSeconds END;
    SET @BatchSize = CASE
        WHEN @BatchSize < 1 THEN 1
        WHEN @BatchSize > 5000 THEN 5000
        ELSE @BatchSize END;

    DECLARE @Now datetime2(3)=SYSUTCDATETIME();
    DECLARE @Discovered int=0, @Finalized int=0, @Queued int=0;

    CREATE TABLE #Source
    (
        CallKey nvarchar(310) NOT NULL PRIMARY KEY,
        LinkedId nvarchar(150) NULL,
        StartedAt datetime2(3) NULL,
        LastLegAt datetime2(3) NULL,
        LastReceivedAtUtc datetime2(3) NOT NULL,
        LegCount int NOT NULL,
        SourceMaxRawCdrId bigint NOT NULL,
        MaxDurationSeconds int NULL,
        MaxBillsecSeconds int NULL,
        RecordingFile nvarchar(500) NULL,
        RecordingReferenceCount int NOT NULL
    );

    ;WITH UngroupedKeys AS
    (
        SELECT
            CASE
                WHEN NULLIF(LTRIM(RTRIM(LinkedId)),N'') IS NOT NULL THEN N'linked:'+LinkedId
                WHEN NULLIF(LTRIM(RTRIM(UniqueId)),N'') IS NOT NULL THEN N'unique:'+UniqueId
                ELSE N'raw:'+CONVERT(nvarchar(30),RawCDRId)
            END AS CallKey,
            RawCDRId
        FROM dbo.RawCDR r
        WHERE NOT EXISTS
        (
            SELECT 1 FROM dbo.AiLogicalCalls c
            WHERE c.CallKey=CASE
                WHEN NULLIF(LTRIM(RTRIM(r.LinkedId)),N'') IS NOT NULL THEN N'linked:'+r.LinkedId
                WHEN NULLIF(LTRIM(RTRIM(r.UniqueId)),N'') IS NOT NULL THEN N'unique:'+r.UniqueId
                ELSE N'raw:'+CONVERT(nvarchar(30),r.RawCDRId) END
              AND c.SourceMaxRawCdrId>=r.RawCDRId
        )
    ),
    CandidateKeys AS
    (
        SELECT TOP(@BatchSize) CallKey
        FROM UngroupedKeys
        GROUP BY CallKey
        ORDER BY MIN(RawCDRId)
    )
    INSERT #Source
    SELECT
        k.CallKey,
        MAX(NULLIF(LTRIM(RTRIM(r.LinkedId)),N'')),
        MIN(r.Calldate),
        MAX(DATEADD(second,ISNULL(r.Duration,0),r.Calldate)),
        MAX(r.ReceivedAtUtc),
        COUNT(*),
        MAX(r.RawCDRId),
        MAX(r.Duration),
        MAX(r.Billsec),
        MAX(NULLIF(LTRIM(RTRIM(r.RecordingFile)),N'')),
        COUNT(DISTINCT NULLIF(LTRIM(RTRIM(r.RecordingFile)),N''))
    FROM CandidateKeys k
    JOIN dbo.RawCDR r ON k.CallKey=CASE
        WHEN NULLIF(LTRIM(RTRIM(r.LinkedId)),N'') IS NOT NULL THEN N'linked:'+r.LinkedId
        WHEN NULLIF(LTRIM(RTRIM(r.UniqueId)),N'') IS NOT NULL THEN N'unique:'+r.UniqueId
        ELSE N'raw:'+CONVERT(nvarchar(30),r.RawCDRId) END
    GROUP BY k.CallKey;

    BEGIN TRANSACTION;

    UPDATE target WITH (UPDLOCK, SERIALIZABLE)
    SET LinkedId=source.LinkedId,
        StartedAt=source.StartedAt,
        LastLegAt=source.LastLegAt,
        LastReceivedAtUtc=source.LastReceivedAtUtc,
        StableAfterUtc=DATEADD(second,@StabilizationSeconds,source.LastReceivedAtUtc),
        LegCount=source.LegCount,
        SourceMaxRawCdrId=source.SourceMaxRawCdrId,
        MaxDurationSeconds=source.MaxDurationSeconds,
        MaxBillsecSeconds=source.MaxBillsecSeconds,
        RecordingFile=source.RecordingFile,
        RecordingReferenceCount=source.RecordingReferenceCount,
        PipelineState=CASE WHEN target.PipelineState=N'FINALIZED' THEN N'REOPENED' ELSE N'STABILIZING' END,
        FinalizedAtUtc=CASE WHEN target.PipelineState=N'FINALIZED' THEN NULL ELSE target.FinalizedAtUtc END,
        UpdatedAtUtc=@Now
    FROM dbo.AiLogicalCalls target
    JOIN #Source source ON source.CallKey=target.CallKey
    WHERE source.SourceMaxRawCdrId>target.SourceMaxRawCdrId;
    SET @Discovered += @@ROWCOUNT;

    INSERT dbo.AiLogicalCalls
    (
        CallKey,LinkedId,StartedAt,LastLegAt,LastReceivedAtUtc,StableAfterUtc,
        LegCount,SourceMaxRawCdrId,MaxDurationSeconds,MaxBillsecSeconds,
        RecordingFile,RecordingReferenceCount,PipelineState,UpdatedAtUtc
    )
    SELECT
        source.CallKey,source.LinkedId,source.StartedAt,source.LastLegAt,
        source.LastReceivedAtUtc,DATEADD(second,@StabilizationSeconds,source.LastReceivedAtUtc),
        source.LegCount,source.SourceMaxRawCdrId,source.MaxDurationSeconds,
        source.MaxBillsecSeconds,source.RecordingFile,source.RecordingReferenceCount,
        N'STABILIZING',@Now
    FROM #Source source
    WHERE NOT EXISTS (SELECT 1 FROM dbo.AiLogicalCalls target WHERE target.CallKey=source.CallKey);
    SET @Discovered += @@ROWCOUNT;

    UPDATE dbo.AiLogicalCalls
    SET PipelineState=N'FINALIZED',FinalizedAtUtc=@Now,UpdatedAtUtc=@Now
    WHERE PipelineState IN (N'STABILIZING',N'REOPENED')
      AND StableAfterUtc<=@Now;
    SET @Finalized=@@ROWCOUNT;

    INSERT dbo.AiPipelineRuns
        (LogicalCallId,RunNumber,SourceMaxRawCdrId,RecordingFile,RunStatus,QueuedAtUtc,UpdatedAtUtc)
    SELECT
        c.LogicalCallId,
        ISNULL((SELECT MAX(previous.RunNumber) FROM dbo.AiPipelineRuns previous
                WHERE previous.LogicalCallId=c.LogicalCallId),0)+1,
        c.SourceMaxRawCdrId,
        c.RecordingFile,
        N'QUEUED',@Now,@Now
    FROM dbo.AiLogicalCalls c
    WHERE c.PipelineState=N'FINALIZED'
      AND NULLIF(LTRIM(RTRIM(c.RecordingFile)),N'') IS NOT NULL
      AND c.RecordingReferenceCount=1
      AND NOT EXISTS
      (
          SELECT 1 FROM dbo.AiPipelineRuns run
          WHERE run.LogicalCallId=c.LogicalCallId
            AND run.SourceMaxRawCdrId=c.SourceMaxRawCdrId
      );
    SET @Queued=@@ROWCOUNT;

    COMMIT TRANSACTION;

    SELECT @Discovered AS CallsDiscovered,@Finalized AS CallsFinalized,
           @Queued AS RunsQueued,@Now AS ExecutedAtUtc;
END;
GO
