SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'dbo.AiCallAssessments',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AiCallAssessments
    (
        AssessmentId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_AiCallAssessments PRIMARY KEY,
        RunId bigint NOT NULL,
        AudioClass nvarchar(40) NOT NULL,
        HasHumanSpeech bit NOT NULL,
        IsBusinessRelevant bit NOT NULL,
        Direction nvarchar(20) NOT NULL,
        InternalExtension nvarchar(32) NULL,
        QueueName nvarchar(32) NULL,
        Confidence decimal(5,4) NOT NULL,
        SpeechSeconds decimal(12,3) NULL,
        Summary nvarchar(2000) NULL,
        StructuredJson nvarchar(max) NULL,
        AnalyzerVersion nvarchar(100) NOT NULL,
        CreatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_AiCallAssessments_Created DEFAULT(SYSUTCDATETIME()),
        UpdatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_AiCallAssessments_Updated DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT FK_AiCallAssessments_Run FOREIGN KEY(RunId) REFERENCES dbo.AiPipelineRuns(RunId),
        CONSTRAINT UX_AiCallAssessments_Run UNIQUE(RunId),
        CONSTRAINT CK_AiCallAssessments_AudioClass CHECK
        (
            AudioClass IN
            (N'BUSINESS_CONVERSATION',N'QUEUE_ONLY',N'NON_SPEECH_OR_UNSUPPORTED',N'EMPTY',N'PROCESSING_ERROR')
        ),
        CONSTRAINT CK_AiCallAssessments_Direction CHECK
            (Direction IN (N'INBOUND',N'OUTBOUND',N'INTERNAL',N'UNKNOWN')),
        CONSTRAINT CK_AiCallAssessments_Confidence CHECK (Confidence>=0 AND Confidence<=1),
        CONSTRAINT CK_AiCallAssessments_Json CHECK
            (StructuredJson IS NULL OR ISJSON(StructuredJson)=1)
    );
    CREATE INDEX IX_AiCallAssessments_ClassCreated
        ON dbo.AiCallAssessments(AudioClass,CreatedAtUtc DESC)
        INCLUDE(IsBusinessRelevant,Direction,InternalExtension,Confidence);
END;

IF OBJECT_ID(N'dbo.AiExtractedFacts',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AiExtractedFacts
    (
        FactId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_AiExtractedFacts PRIMARY KEY,
        AssessmentId bigint NOT NULL,
        FactType nvarchar(50) NOT NULL,
        RawValue nvarchar(1000) NULL,
        NormalizedValue nvarchar(1000) NULL,
        Unit nvarchar(50) NULL,
        StartSeconds decimal(12,3) NULL,
        EndSeconds decimal(12,3) NULL,
        Confidence decimal(5,4) NOT NULL,
        ReviewStatus nvarchar(20) NOT NULL,
        ExtractionVersion nvarchar(100) NOT NULL,
        CreatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_AiExtractedFacts_Created DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT FK_AiExtractedFacts_Assessment FOREIGN KEY(AssessmentId)
            REFERENCES dbo.AiCallAssessments(AssessmentId),
        CONSTRAINT CK_AiExtractedFacts_Type CHECK
        (
            FactType IN
            (N'PRODUCT',N'BRAND',N'SIZE',N'QUANTITY',N'PRICE',N'PAYMENT_DATE',N'ACTION',N'PERSON',N'COMPANY',N'TOPIC')
        ),
        CONSTRAINT CK_AiExtractedFacts_Confidence CHECK (Confidence>=0 AND Confidence<=1),
        CONSTRAINT CK_AiExtractedFacts_Review CHECK
            (ReviewStatus IN (N'AUTO_ACCEPTED',N'REVIEW',N'CONFIRMED',N'REJECTED')),
        CONSTRAINT CK_AiExtractedFacts_Times CHECK
            (StartSeconds IS NULL OR EndSeconds IS NULL OR EndSeconds>=StartSeconds)
    );
    CREATE INDEX IX_AiExtractedFacts_AssessmentType
        ON dbo.AiExtractedFacts(AssessmentId,FactType,ReviewStatus)
        INCLUDE(NormalizedValue,Unit,Confidence,StartSeconds,EndSeconds);
END;

IF OBJECT_ID(N'dbo.AiReviewItems',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AiReviewItems
    (
        ReviewItemId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_AiReviewItems PRIMARY KEY,
        AssessmentId bigint NOT NULL,
        Category nvarchar(50) NOT NULL,
        Priority nvarchar(10) NOT NULL,
        ReasonCode nvarchar(100) NOT NULL,
        RawText nvarchar(2000) NULL,
        StartSeconds decimal(12,3) NULL,
        EndSeconds decimal(12,3) NULL,
        ReviewStatus nvarchar(20) NOT NULL CONSTRAINT DF_AiReviewItems_Status DEFAULT(N'OPEN'),
        Resolution nvarchar(2000) NULL,
        ResolvedBy nvarchar(100) NULL,
        ResolvedAtUtc datetime2(3) NULL,
        CreatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_AiReviewItems_Created DEFAULT(SYSUTCDATETIME()),
        UpdatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_AiReviewItems_Updated DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT FK_AiReviewItems_Assessment FOREIGN KEY(AssessmentId)
            REFERENCES dbo.AiCallAssessments(AssessmentId),
        CONSTRAINT CK_AiReviewItems_Priority CHECK (Priority IN (N'LOW',N'MEDIUM',N'HIGH')),
        CONSTRAINT CK_AiReviewItems_Status CHECK
            (ReviewStatus IN (N'OPEN',N'CONFIRMED',N'CORRECTED',N'REJECTED',N'DEFERRED')),
        CONSTRAINT CK_AiReviewItems_Times CHECK
            (StartSeconds IS NULL OR EndSeconds IS NULL OR EndSeconds>=StartSeconds)
    );
    CREATE INDEX IX_AiReviewItems_Open
        ON dbo.AiReviewItems(ReviewStatus,Priority,CreatedAtUtc)
        INCLUDE(AssessmentId,Category,ReasonCode,StartSeconds,EndSeconds);
END;
