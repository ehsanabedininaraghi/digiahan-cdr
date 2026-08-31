SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'dbo.CustomerIdentities',N'U') IS NULL
    THROW 51440,N'Customer identity schema must be installed before Journey Kernel.',1;

BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.JourneySchemaVersions',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.JourneySchemaVersions
    (
        Version nvarchar(30) NOT NULL CONSTRAINT PK_JourneySchemaVersions PRIMARY KEY,
        AppliedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_JourneySchemaVersions_AppliedAtUtc DEFAULT(SYSUTCDATETIME())
    );
END;

IF OBJECT_ID(N'dbo.JourneySlaPolicies',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.JourneySlaPolicies
    (
        PolicyKey nvarchar(60) NOT NULL CONSTRAINT PK_JourneySlaPolicies PRIMARY KEY,
        DisplayName nvarchar(200) NOT NULL,
        DueMinutes int NOT NULL,
        IsEnabled bit NOT NULL CONSTRAINT DF_JourneySlaPolicies_IsEnabled DEFAULT(1),
        UpdatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_JourneySlaPolicies_UpdatedAtUtc DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT CK_JourneySlaPolicies_DueMinutes CHECK(DueMinutes BETWEEN 1 AND 525600)
    );
END;

IF OBJECT_ID(N'dbo.JourneyLeads',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.JourneyLeads
    (
        LeadId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_JourneyLeads PRIMARY KEY,
        IdempotencyKey uniqueidentifier NOT NULL,
        IdentityId bigint NOT NULL,
        SourceSystem nvarchar(30) NOT NULL,
        SourceReference nvarchar(120) NULL,
        SourceInteractionId bigint NULL,
        OwnerSellerKey nvarchar(80) NOT NULL,
        Title nvarchar(300) NOT NULL,
        Status nvarchar(30) NOT NULL,
        Priority tinyint NOT NULL CONSTRAINT DF_JourneyLeads_Priority DEFAULT(2),
        NextActionType nvarchar(60) NOT NULL,
        NextActionAtUtc datetime2(0) NOT NULL,
        SlaDueAtUtc datetime2(0) NOT NULL,
        ProductSummary nvarchar(500) NULL,
        Note nvarchar(1500) NULL,
        ClosedReason nvarchar(300) NULL,
        CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_JourneyLeads_CreatedAtUtc DEFAULT(SYSUTCDATETIME()),
        UpdatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_JourneyLeads_UpdatedAtUtc DEFAULT(SYSUTCDATETIME()),
        RowVersion rowversion NOT NULL,
        CONSTRAINT UQ_JourneyLeads_Idempotency UNIQUE(IdempotencyKey),
        CONSTRAINT FK_JourneyLeads_Identity FOREIGN KEY(IdentityId) REFERENCES dbo.CustomerIdentities(IdentityId),
        CONSTRAINT CK_JourneyLeads_Status CHECK(Status IN (N'OPEN',N'QUALIFIED',N'DISQUALIFIED',N'CONVERTED',N'CLOSED')),
        CONSTRAINT CK_JourneyLeads_Priority CHECK(Priority BETWEEN 1 AND 4),
        CONSTRAINT CK_JourneyLeads_Owner CHECK(LEN(LTRIM(RTRIM(OwnerSellerKey)))>0)
    );
END;

IF OBJECT_ID(N'dbo.JourneyOpportunities',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.JourneyOpportunities
    (
        OpportunityId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_JourneyOpportunities PRIMARY KEY,
        IdempotencyKey uniqueidentifier NOT NULL,
        IdentityId bigint NOT NULL,
        LeadId bigint NULL,
        SourceInteractionId bigint NULL,
        OwnerSellerKey nvarchar(80) NOT NULL,
        Title nvarchar(300) NOT NULL,
        Stage nvarchar(30) NOT NULL,
        NextActionType nvarchar(60) NOT NULL,
        NextActionAtUtc datetime2(0) NOT NULL,
        SlaDueAtUtc datetime2(0) NOT NULL,
        ExpectedCloseAtUtc datetime2(0) NULL,
        EstimatedAmount decimal(19,4) NULL,
        ProductSummary nvarchar(500) NULL,
        WonAtUtc datetime2(0) NULL,
        LostAtUtc datetime2(0) NULL,
        LostReason nvarchar(300) NULL,
        Note nvarchar(1500) NULL,
        CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_JourneyOpportunities_CreatedAtUtc DEFAULT(SYSUTCDATETIME()),
        UpdatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_JourneyOpportunities_UpdatedAtUtc DEFAULT(SYSUTCDATETIME()),
        RowVersion rowversion NOT NULL,
        CONSTRAINT UQ_JourneyOpportunities_Idempotency UNIQUE(IdempotencyKey),
        CONSTRAINT FK_JourneyOpportunities_Identity FOREIGN KEY(IdentityId) REFERENCES dbo.CustomerIdentities(IdentityId),
        CONSTRAINT FK_JourneyOpportunities_Lead FOREIGN KEY(LeadId) REFERENCES dbo.JourneyLeads(LeadId),
        CONSTRAINT CK_JourneyOpportunities_Stage CHECK(Stage IN
          (N'DISCOVERY',N'NEEDS_CONFIRMED',N'PRICE_GIVEN',N'QUOTE_SENT',N'DECISION',N'NEGOTIATION',N'WON',N'LOST',N'ON_HOLD')),
        CONSTRAINT CK_JourneyOpportunities_Amount CHECK(EstimatedAmount IS NULL OR EstimatedAmount>=0),
        CONSTRAINT CK_JourneyOpportunities_Owner CHECK(LEN(LTRIM(RTRIM(OwnerSellerKey)))>0)
    );
END;

IF OBJECT_ID(N'dbo.JourneyOpportunityProducts',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.JourneyOpportunityProducts
    (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_JourneyOpportunityProducts PRIMARY KEY,
        OpportunityId bigint NOT NULL,
        ProductName nvarchar(200) NOT NULL,
        Quantity decimal(18,3) NULL,
        QuantityUnit nvarchar(30) NULL,
        CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_JourneyOpportunityProducts_CreatedAtUtc DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT FK_JourneyOpportunityProducts_Opportunity FOREIGN KEY(OpportunityId) REFERENCES dbo.JourneyOpportunities(OpportunityId),
        CONSTRAINT CK_JourneyOpportunityProducts_Quantity CHECK(Quantity IS NULL OR Quantity>=0)
    );
END;

IF OBJECT_ID(N'dbo.JourneyOpportunityStageHistory',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.JourneyOpportunityStageHistory
    (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_JourneyOpportunityStageHistory PRIMARY KEY,
        OpportunityId bigint NOT NULL,
        FromStage nvarchar(30) NULL,
        ToStage nvarchar(30) NOT NULL,
        ChangedBySellerKey nvarchar(80) NOT NULL,
        Reason nvarchar(500) NULL,
        IdempotencyKey uniqueidentifier NOT NULL,
        ChangedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_JourneyOpportunityStageHistory_ChangedAtUtc DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT FK_JourneyOpportunityStageHistory_Opportunity FOREIGN KEY(OpportunityId) REFERENCES dbo.JourneyOpportunities(OpportunityId),
        CONSTRAINT UQ_JourneyOpportunityStageHistory_Idempotency UNIQUE(IdempotencyKey)
    );
END;

IF OBJECT_ID(N'dbo.JourneyWorkItems',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.JourneyWorkItems
    (
        WorkItemId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_JourneyWorkItems PRIMARY KEY,
        IdempotencyKey uniqueidentifier NOT NULL,
        IdentityId bigint NOT NULL,
        LeadId bigint NULL,
        OpportunityId bigint NULL,
        SourceInteractionId bigint NULL,
        OwnerSellerKey nvarchar(80) NOT NULL,
        WorkType nvarchar(60) NOT NULL,
        Title nvarchar(300) NOT NULL,
        Description nvarchar(1000) NULL,
        Status nvarchar(20) NOT NULL CONSTRAINT DF_JourneyWorkItems_Status DEFAULT(N'OPEN'),
        Priority tinyint NOT NULL CONSTRAINT DF_JourneyWorkItems_Priority DEFAULT(2),
        DueAtUtc datetime2(0) NOT NULL,
        SlaDueAtUtc datetime2(0) NOT NULL,
        CompletedAtUtc datetime2(0) NULL,
        CompletedBySellerKey nvarchar(80) NULL,
        Outcome nvarchar(40) NULL,
        CompletionNote nvarchar(1000) NULL,
        CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_JourneyWorkItems_CreatedAtUtc DEFAULT(SYSUTCDATETIME()),
        UpdatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_JourneyWorkItems_UpdatedAtUtc DEFAULT(SYSUTCDATETIME()),
        RowVersion rowversion NOT NULL,
        CONSTRAINT UQ_JourneyWorkItems_Idempotency UNIQUE(IdempotencyKey),
        CONSTRAINT FK_JourneyWorkItems_Identity FOREIGN KEY(IdentityId) REFERENCES dbo.CustomerIdentities(IdentityId),
        CONSTRAINT FK_JourneyWorkItems_Lead FOREIGN KEY(LeadId) REFERENCES dbo.JourneyLeads(LeadId),
        CONSTRAINT FK_JourneyWorkItems_Opportunity FOREIGN KEY(OpportunityId) REFERENCES dbo.JourneyOpportunities(OpportunityId),
        CONSTRAINT CK_JourneyWorkItems_Status CHECK(Status IN (N'OPEN',N'IN_PROGRESS',N'COMPLETED',N'CANCELLED')),
        CONSTRAINT CK_JourneyWorkItems_Priority CHECK(Priority BETWEEN 1 AND 4),
        CONSTRAINT CK_JourneyWorkItems_Owner CHECK(LEN(LTRIM(RTRIM(OwnerSellerKey)))>0)
    );
END;

IF OBJECT_ID(N'dbo.JourneyEvents',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.JourneyEvents
    (
        EventId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_JourneyEvents PRIMARY KEY,
        EventKey uniqueidentifier NOT NULL,
        IdentityId bigint NOT NULL,
        AggregateType nvarchar(40) NOT NULL,
        AggregateId bigint NULL,
        EventType nvarchar(60) NOT NULL,
        SourceSystem nvarchar(30) NOT NULL,
        SourceReference nvarchar(120) NULL,
        ActorType nvarchar(30) NOT NULL,
        ActorKey nvarchar(80) NULL,
        CorrelationId uniqueidentifier NOT NULL,
        OccurredAtUtc datetime2(0) NOT NULL,
        PayloadJson nvarchar(max) NULL,
        CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_JourneyEvents_CreatedAtUtc DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT UQ_JourneyEvents_EventKey UNIQUE(EventKey),
        CONSTRAINT FK_JourneyEvents_Identity FOREIGN KEY(IdentityId) REFERENCES dbo.CustomerIdentities(IdentityId),
        CONSTRAINT CK_JourneyEvents_PayloadJson CHECK(PayloadJson IS NULL OR ISJSON(PayloadJson)=1)
    );
END;

IF OBJECT_ID(N'dbo.JourneyOutbox',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.JourneyOutbox
    (
        OutboxId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_JourneyOutbox PRIMARY KEY,
        EventId bigint NOT NULL,
        Destination nvarchar(60) NOT NULL,
        Status nvarchar(20) NOT NULL CONSTRAINT DF_JourneyOutbox_Status DEFAULT(N'PENDING'),
        AttemptCount int NOT NULL CONSTRAINT DF_JourneyOutbox_AttemptCount DEFAULT(0),
        NextAttemptAtUtc datetime2(0) NOT NULL CONSTRAINT DF_JourneyOutbox_NextAttemptAtUtc DEFAULT(SYSUTCDATETIME()),
        LastError nvarchar(2000) NULL,
        ProcessedAtUtc datetime2(0) NULL,
        CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_JourneyOutbox_CreatedAtUtc DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT UQ_JourneyOutbox_EventDestination UNIQUE(EventId,Destination),
        CONSTRAINT FK_JourneyOutbox_Event FOREIGN KEY(EventId) REFERENCES dbo.JourneyEvents(EventId),
        CONSTRAINT CK_JourneyOutbox_Status CHECK(Status IN (N'PENDING',N'PROCESSING',N'PROCESSED',N'FAILED'))
    );
END;

IF OBJECT_ID(N'dbo.JourneyLegacyLinks',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.JourneyLegacyLinks
    (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_JourneyLegacyLinks PRIMARY KEY,
        LegacyEntityType nvarchar(40) NOT NULL,
        LegacyEntityId bigint NOT NULL,
        JourneyEntityType nvarchar(40) NOT NULL,
        JourneyEntityId bigint NOT NULL,
        CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_JourneyLegacyLinks_CreatedAtUtc DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT UQ_JourneyLegacyLinks_Legacy UNIQUE(LegacyEntityType,LegacyEntityId,JourneyEntityType)
    );
END;

IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.JourneyLeads') AND name=N'UX_JourneyLeads_SourceInteraction')
    CREATE UNIQUE INDEX UX_JourneyLeads_SourceInteraction ON dbo.JourneyLeads(SourceInteractionId) WHERE SourceInteractionId IS NOT NULL;
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.JourneyLeads') AND name=N'IX_JourneyLeads_OwnerStatusNext')
    CREATE INDEX IX_JourneyLeads_OwnerStatusNext ON dbo.JourneyLeads(OwnerSellerKey,Status,NextActionAtUtc) INCLUDE(IdentityId,Priority,SlaDueAtUtc,Title);
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.JourneyOpportunities') AND name=N'IX_JourneyOpportunities_OwnerStageNext')
    CREATE INDEX IX_JourneyOpportunities_OwnerStageNext ON dbo.JourneyOpportunities(OwnerSellerKey,Stage,NextActionAtUtc) INCLUDE(IdentityId,LeadId,SlaDueAtUtc,Title);
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.JourneyOpportunities') AND name=N'UX_JourneyOpportunities_Lead')
    CREATE UNIQUE INDEX UX_JourneyOpportunities_Lead ON dbo.JourneyOpportunities(LeadId) WHERE LeadId IS NOT NULL;
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.JourneyWorkItems') AND name=N'IX_JourneyWorkItems_OwnerStatusDue')
    CREATE INDEX IX_JourneyWorkItems_OwnerStatusDue ON dbo.JourneyWorkItems(OwnerSellerKey,Status,DueAtUtc) INCLUDE(IdentityId,LeadId,OpportunityId,Priority,SlaDueAtUtc,WorkType,Title);
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.JourneyWorkItems') AND name=N'IX_JourneyWorkItems_OpenSla')
    CREATE INDEX IX_JourneyWorkItems_OpenSla ON dbo.JourneyWorkItems(Status,SlaDueAtUtc) INCLUDE(OwnerSellerKey,IdentityId,WorkType,Title,DueAtUtc);
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.JourneyEvents') AND name=N'IX_JourneyEvents_IdentityOccurred')
    CREATE INDEX IX_JourneyEvents_IdentityOccurred ON dbo.JourneyEvents(IdentityId,OccurredAtUtc DESC) INCLUDE(AggregateType,AggregateId,EventType,SourceSystem);
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.JourneyOutbox') AND name=N'IX_JourneyOutbox_StatusAttempt')
    CREATE INDEX IX_JourneyOutbox_StatusAttempt ON dbo.JourneyOutbox(Status,NextAttemptAtUtc) INCLUDE(EventId,Destination,AttemptCount);

MERGE dbo.JourneySlaPolicies AS target
USING (VALUES
    (N'NEW_LEAD',N'سرنخ جدید',120),
    (N'FOLLOW_UP',N'پیگیری فروش',1440),
    (N'QUOTE_FOLLOW_UP',N'پیگیری پیش‌فاکتور',720),
    (N'DECISION_FOLLOW_UP',N'پیگیری تصمیم مشتری',1440),
    (N'CUSTOMER_RECOVERY',N'بازیابی مشتری از دست رفته',2880)
) AS source(PolicyKey,DisplayName,DueMinutes)
ON target.PolicyKey=source.PolicyKey
WHEN NOT MATCHED THEN INSERT(PolicyKey,DisplayName,DueMinutes) VALUES(source.PolicyKey,source.DisplayName,source.DueMinutes);

IF NOT EXISTS(SELECT 1 FROM dbo.JourneySchemaVersions WHERE Version=N'4.4.0')
    INSERT dbo.JourneySchemaVersions(Version) VALUES(N'4.4.0');

COMMIT TRANSACTION;
