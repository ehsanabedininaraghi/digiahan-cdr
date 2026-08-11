SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.AccountingCustomers',N'U') IS NOT NULL
AND COL_LENGTH(N'dbo.AccountingCustomers',N'Debit') IS NULL
    ALTER TABLE dbo.AccountingCustomers ADD Debit decimal(19,4) NULL;

IF OBJECT_ID(N'dbo.AccountingCustomers',N'U') IS NOT NULL
AND COL_LENGTH(N'dbo.AccountingCustomers',N'Credit') IS NULL
    ALTER TABLE dbo.AccountingCustomers ADD Credit decimal(19,4) NULL;

IF OBJECT_ID(N'dbo.AccountingCustomers',N'U') IS NOT NULL
AND COL_LENGTH(N'dbo.AccountingCustomers',N'CreditLimit') IS NULL
    ALTER TABLE dbo.AccountingCustomers ADD CreditLimit decimal(19,4) NULL;

IF OBJECT_ID(N'dbo.AccountingCustomers',N'U') IS NOT NULL
AND COL_LENGTH(N'dbo.AccountingCustomers',N'AccountBalance') IS NULL
    ALTER TABLE dbo.AccountingCustomers ADD AccountBalance decimal(19,4) NULL;

IF OBJECT_ID(N'dbo.CustomerIdentities',N'U') IS NOT NULL
AND COL_LENGTH(N'dbo.CustomerIdentities',N'MasterSource') IS NULL
    ALTER TABLE dbo.CustomerIdentities ADD MasterSource nvarchar(30) NOT NULL
        CONSTRAINT DF_CustomerIdentities_MasterSource DEFAULT(N'LEGACY');

IF OBJECT_ID(N'dbo.CustomerIdentities',N'U') IS NOT NULL
AND COL_LENGTH(N'dbo.CustomerIdentities',N'IsActive') IS NULL
    ALTER TABLE dbo.CustomerIdentities ADD IsActive bit NOT NULL
        CONSTRAINT DF_CustomerIdentities_IsActive DEFAULT(1);

IF OBJECT_ID(N'dbo.SellerInteractions',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SellerInteractions
    (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_SellerInteractions PRIMARY KEY,
        IdempotencyKey uniqueidentifier NOT NULL,
        SellerKey nvarchar(80) NOT NULL,
        SellerDisplayName nvarchar(200) NOT NULL,
        SellerExtension nvarchar(10) NOT NULL,
        CustomerIdentityId bigint NULL,
        CustomerPhone nvarchar(32) NOT NULL,
        CallLinkedId nvarchar(100) NULL,
        Outcome nvarchar(30) NOT NULL,
        LossReason nvarchar(30) NULL,
        CompetitorName nvarchar(200) NULL,
        CompetitorPrice decimal(19,4) NULL,
        Note nvarchar(1000) NULL,
        OccurredAtUtc datetime2(0) NOT NULL,
        CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_SellerInteractions_CreatedAtUtc DEFAULT(SYSUTCDATETIME()),
        UpdatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_SellerInteractions_UpdatedAtUtc DEFAULT(SYSUTCDATETIME()),
        RowVersion rowversion NOT NULL,
        CONSTRAINT UQ_SellerInteractions_Idempotency UNIQUE(IdempotencyKey),
        CONSTRAINT CK_SellerInteractions_Outcome CHECK (Outcome IN
            (N'ORDER',N'DECIDING',N'FOLLOW_UP',N'LOST',N'NON_SALES')),
        CONSTRAINT CK_SellerInteractions_LossReason CHECK
            ((Outcome=N'LOST' AND LossReason IS NOT NULL) OR (Outcome<>N'LOST' AND LossReason IS NULL))
    );
END;

IF OBJECT_ID(N'dbo.SellerInteractionProducts',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SellerInteractionProducts
    (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_SellerInteractionProducts PRIMARY KEY,
        InteractionId bigint NOT NULL,
        ProductName nvarchar(120) NOT NULL,
        ProductSize nvarchar(60) NULL,
        ProductBrand nvarchar(120) NULL,
        Quantity decimal(18,3) NULL,
        QuantityUnit nvarchar(30) NULL,
        CONSTRAINT FK_SellerInteractionProducts_Interaction FOREIGN KEY(InteractionId)
            REFERENCES dbo.SellerInteractions(Id)
    );
END;

IF OBJECT_ID(N'dbo.SellerInteractionActions',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SellerInteractionActions
    (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_SellerInteractionActions PRIMARY KEY,
        InteractionId bigint NOT NULL,
        ActionCode nvarchar(40) NOT NULL,
        CONSTRAINT FK_SellerInteractionActions_Interaction FOREIGN KEY(InteractionId)
            REFERENCES dbo.SellerInteractions(Id),
        CONSTRAINT UQ_SellerInteractionActions UNIQUE(InteractionId,ActionCode)
    );
END;

IF OBJECT_ID(N'dbo.SellerFollowUps',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SellerFollowUps
    (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_SellerFollowUps PRIMARY KEY,
        InteractionId bigint NOT NULL,
        SellerKey nvarchar(80) NOT NULL,
        CustomerPhone nvarchar(32) NOT NULL,
        Subject nvarchar(300) NOT NULL,
        DueAtUtc datetime2(0) NOT NULL,
        Status nvarchar(20) NOT NULL CONSTRAINT DF_SellerFollowUps_Status DEFAULT(N'OPEN'),
        CompletedAtUtc datetime2(0) NULL,
        CompletedBySellerKey nvarchar(80) NULL,
        CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_SellerFollowUps_CreatedAtUtc DEFAULT(SYSUTCDATETIME()),
        UpdatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_SellerFollowUps_UpdatedAtUtc DEFAULT(SYSUTCDATETIME()),
        RowVersion rowversion NOT NULL,
        CONSTRAINT FK_SellerFollowUps_Interaction FOREIGN KEY(InteractionId)
            REFERENCES dbo.SellerInteractions(Id),
        CONSTRAINT CK_SellerFollowUps_Status CHECK(Status IN (N'OPEN',N'COMPLETED',N'CANCELLED'))
    );
END;

IF OBJECT_ID(N'dbo.SellerWorkspaceAudit',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SellerWorkspaceAudit
    (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_SellerWorkspaceAudit PRIMARY KEY,
        SellerKey nvarchar(80) NOT NULL,
        ActionType nvarchar(60) NOT NULL,
        EntityType nvarchar(60) NOT NULL,
        EntityId nvarchar(100) NOT NULL,
        IdempotencyKey uniqueidentifier NULL,
        DetailsJson nvarchar(max) NULL,
        CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_SellerWorkspaceAudit_CreatedAtUtc DEFAULT(SYSUTCDATETIME())
    );
END;

IF OBJECT_ID(N'dbo.SellerUsers',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SellerUsers
    (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_SellerUsers PRIMARY KEY,
        Username nvarchar(80) NOT NULL,
        NormalizedUsername nvarchar(80) NOT NULL,
        PasswordHash varbinary(32) NOT NULL,
        PasswordSalt varbinary(16) NOT NULL,
        PasswordIterations int NOT NULL,
        SellerKey nvarchar(80) NOT NULL,
        DisplayName nvarchar(200) NOT NULL,
        IsActive bit NOT NULL CONSTRAINT DF_SellerUsers_IsActive DEFAULT(1),
        CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_SellerUsers_CreatedAtUtc DEFAULT(SYSUTCDATETIME()),
        UpdatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_SellerUsers_UpdatedAtUtc DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT UQ_SellerUsers_NormalizedUsername UNIQUE(NormalizedUsername),
        CONSTRAINT CK_SellerUsers_Iterations CHECK(PasswordIterations BETWEEN 100000 AND 1000000)
    );
END;

IF OBJECT_ID(N'dbo.SellerUserExtensions',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SellerUserExtensions
    (
        SellerUserId bigint NOT NULL,
        Extension nvarchar(10) NOT NULL,
        CONSTRAINT PK_SellerUserExtensions PRIMARY KEY(SellerUserId,Extension),
        CONSTRAINT FK_SellerUserExtensions_User FOREIGN KEY(SellerUserId) REFERENCES dbo.SellerUsers(Id)
    );
    CREATE INDEX IX_SellerUserExtensions_Extension ON dbo.SellerUserExtensions(Extension) INCLUDE(SellerUserId);
END;

IF OBJECT_ID(N'dbo.SellerUserProductGroups',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SellerUserProductGroups
    (
        SellerUserId bigint NOT NULL,
        ProductGroup nvarchar(120) NOT NULL,
        CONSTRAINT PK_SellerUserProductGroups PRIMARY KEY(SellerUserId,ProductGroup),
        CONSTRAINT FK_SellerUserProductGroups_User FOREIGN KEY(SellerUserId) REFERENCES dbo.SellerUsers(Id)
    );
END;

IF OBJECT_ID(N'dbo.SellerSessions',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SellerSessions
    (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_SellerSessions PRIMARY KEY,
        SellerUserId bigint NOT NULL,
        TokenHash varbinary(32) NOT NULL,
        CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_SellerSessions_CreatedAtUtc DEFAULT(SYSUTCDATETIME()),
        LastSeenAtUtc datetime2(0) NOT NULL CONSTRAINT DF_SellerSessions_LastSeenAtUtc DEFAULT(SYSUTCDATETIME()),
        ExpiresAtUtc datetime2(0) NOT NULL,
        RevokedAtUtc datetime2(0) NULL,
        CONSTRAINT FK_SellerSessions_User FOREIGN KEY(SellerUserId) REFERENCES dbo.SellerUsers(Id),
        CONSTRAINT UQ_SellerSessions_TokenHash UNIQUE(TokenHash)
    );
    CREATE INDEX IX_SellerSessions_UserExpiry ON dbo.SellerSessions(SellerUserId,ExpiresAtUtc DESC)
        INCLUDE(RevokedAtUtc,LastSeenAtUtc);
END;

IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.SellerInteractions') AND name=N'IX_SellerInteractions_CustomerOccurred')
    CREATE INDEX IX_SellerInteractions_CustomerOccurred ON dbo.SellerInteractions(CustomerPhone,OccurredAtUtc DESC)
    INCLUDE(SellerKey,SellerDisplayName,Outcome,LossReason,CallLinkedId);

IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.SellerInteractions') AND name=N'IX_SellerInteractions_SellerOccurred')
    CREATE INDEX IX_SellerInteractions_SellerOccurred ON dbo.SellerInteractions(SellerKey,OccurredAtUtc DESC)
    INCLUDE(CustomerPhone,Outcome,LossReason);

IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.SellerFollowUps') AND name=N'IX_SellerFollowUps_SellerStatusDue')
    CREATE INDEX IX_SellerFollowUps_SellerStatusDue ON dbo.SellerFollowUps(SellerKey,Status,DueAtUtc)
    INCLUDE(CustomerPhone,Subject,InteractionId);

IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.SellerWorkspaceAudit') AND name=N'IX_SellerWorkspaceAudit_SellerCreated')
    CREATE INDEX IX_SellerWorkspaceAudit_SellerCreated ON dbo.SellerWorkspaceAudit(SellerKey,CreatedAtUtc DESC);

DELETE FROM dbo.SellerSessions
WHERE ExpiresAtUtc<DATEADD(day,-7,SYSUTCDATETIME()) OR RevokedAtUtc<DATEADD(day,-7,SYSUTCDATETIME());

-- Customer timelines must not scan and normalize the entire CDR table for every opened card.
IF OBJECT_ID(N'dbo.RawCDR',N'U') IS NOT NULL
AND NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.RawCDR') AND name=N'IX_RawCDR_Src_Calldate')
    CREATE INDEX IX_RawCDR_Src_Calldate ON dbo.RawCDR(Src,Calldate DESC)
    INCLUDE(Dst,Billsec,Disposition,LinkedId,UniqueId);

IF OBJECT_ID(N'dbo.RawCDR',N'U') IS NOT NULL
AND NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.RawCDR') AND name=N'IX_RawCDR_Dst_Calldate')
    CREATE INDEX IX_RawCDR_Dst_Calldate ON dbo.RawCDR(Dst,Calldate DESC)
    INCLUDE(Src,Billsec,Disposition,LinkedId,UniqueId);

IF OBJECT_ID(N'dbo.RawCDR',N'U') IS NOT NULL
AND NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.RawCDR') AND name=N'IX_RawCDR_Src_ReceivedAtUtc')
    CREATE INDEX IX_RawCDR_Src_ReceivedAtUtc ON dbo.RawCDR(Src,ReceivedAtUtc DESC)
    INCLUDE(Dst,Billsec,Disposition,LinkedId,UniqueId,RawCDRId);

IF OBJECT_ID(N'dbo.RawCDR',N'U') IS NOT NULL
AND NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.RawCDR') AND name=N'IX_RawCDR_Dst_ReceivedAtUtc')
    CREATE INDEX IX_RawCDR_Dst_ReceivedAtUtc ON dbo.RawCDR(Dst,ReceivedAtUtc DESC)
    INCLUDE(Src,Billsec,Disposition,LinkedId,UniqueId,RawCDRId);

COMMIT TRANSACTION;
