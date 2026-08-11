
SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.CustomerIdentities',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomerIdentities
    (
        IdentityId bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
        DisplayName nvarchar(300) NULL,
        CompanyName nvarchar(300) NULL,
        OwnerName nvarchar(200) NULL,
        MasterSource nvarchar(30) NOT NULL
            CONSTRAINT DF_CustomerIdentities_MasterSource DEFAULT(N'LEGACY'),
        IsActive bit NOT NULL
            CONSTRAINT DF_CustomerIdentities_IsActive DEFAULT(1),
        CreatedAtUtc datetime2(0) NOT NULL
            CONSTRAINT DF_CustomerIdentities_CreatedAtUtc DEFAULT(SYSUTCDATETIME()),
        UpdatedAtUtc datetime2(0) NOT NULL
            CONSTRAINT DF_CustomerIdentities_UpdatedAtUtc DEFAULT(SYSUTCDATETIME())
    );
END;

IF COL_LENGTH(N'dbo.CustomerIdentities',N'MasterSource') IS NULL
    ALTER TABLE dbo.CustomerIdentities ADD MasterSource nvarchar(30) NOT NULL
        CONSTRAINT DF_CustomerIdentities_MasterSource DEFAULT(N'LEGACY');
IF COL_LENGTH(N'dbo.CustomerIdentities',N'IsActive') IS NULL
    ALTER TABLE dbo.CustomerIdentities ADD IsActive bit NOT NULL
        CONSTRAINT DF_CustomerIdentities_IsActive DEFAULT(1);

IF OBJECT_ID(N'dbo.CustomerIdentityPhones',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomerIdentityPhones
    (
        Id bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
        IdentityId bigint NOT NULL,
        NormalizedPhone nvarchar(32) NOT NULL,
        RawPhone nvarchar(200) NULL,
        PhoneType nvarchar(30) NULL,
        SourceSystem nvarchar(30) NOT NULL,
        IsPrimary bit NOT NULL CONSTRAINT DF_CustomerIdentityPhones_IsPrimary DEFAULT(0),
        IsVerified bit NOT NULL CONSTRAINT DF_CustomerIdentityPhones_IsVerified DEFAULT(0),
        Priority int NOT NULL CONSTRAINT DF_CustomerIdentityPhones_Priority DEFAULT(50),
        CreatedAtUtc datetime2(0) NOT NULL
            CONSTRAINT DF_CustomerIdentityPhones_CreatedAtUtc DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT FK_CustomerIdentityPhones_Identity
            FOREIGN KEY(IdentityId) REFERENCES dbo.CustomerIdentities(IdentityId)
    );

    CREATE INDEX IX_CustomerIdentityPhones_Normalized
        ON dbo.CustomerIdentityPhones(NormalizedPhone,IsVerified DESC,Priority,Id);
    CREATE UNIQUE INDEX UX_CustomerIdentityPhones_IdentityPhoneSource
        ON dbo.CustomerIdentityPhones(IdentityId,NormalizedPhone,SourceSystem);
END;

IF OBJECT_ID(N'dbo.CustomerIdentityAccountingLinks',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomerIdentityAccountingLinks
    (
        Id bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
        IdentityId bigint NOT NULL,
        SourceDatabase nvarchar(128) NOT NULL,
        FiscalYear int NOT NULL,
        DetailCode nvarchar(30) NOT NULL,
        ShortCode nvarchar(30) NULL,
        CustomerName nvarchar(300) NULL,
        IsVerified bit NOT NULL CONSTRAINT DF_CustomerIdentityAccountingLinks_Verified DEFAULT(0),
        CreatedAtUtc datetime2(0) NOT NULL
            CONSTRAINT DF_CustomerIdentityAccountingLinks_CreatedAtUtc DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT FK_CustomerIdentityAccountingLinks_Identity
            FOREIGN KEY(IdentityId) REFERENCES dbo.CustomerIdentities(IdentityId)
    );

    CREATE UNIQUE INDEX UX_CustomerIdentityAccountingLinks_Source
        ON dbo.CustomerIdentityAccountingLinks(SourceDatabase,FiscalYear,DetailCode);
    CREATE INDEX IX_CustomerIdentityAccountingLinks_Identity
        ON dbo.CustomerIdentityAccountingLinks(IdentityId);
END;

IF OBJECT_ID(N'dbo.CustomerIdentityDidarLinks',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomerIdentityDidarLinks
    (
        Id bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
        IdentityId bigint NOT NULL,
        DidarContactCode nvarchar(100) NOT NULL,
        IsVerified bit NOT NULL CONSTRAINT DF_CustomerIdentityDidarLinks_Verified DEFAULT(0),
        CreatedAtUtc datetime2(0) NOT NULL
            CONSTRAINT DF_CustomerIdentityDidarLinks_CreatedAtUtc DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT FK_CustomerIdentityDidarLinks_Identity
            FOREIGN KEY(IdentityId) REFERENCES dbo.CustomerIdentities(IdentityId)
    );

    CREATE UNIQUE INDEX UX_CustomerIdentityDidarLinks_Code
        ON dbo.CustomerIdentityDidarLinks(DidarContactCode);
    CREATE INDEX IX_CustomerIdentityDidarLinks_Identity
        ON dbo.CustomerIdentityDidarLinks(IdentityId);
END;

IF OBJECT_ID(N'dbo.CustomerIdentityConflicts',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomerIdentityConflicts
    (
        Id bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
        NormalizedPhone nvarchar(32) NOT NULL,
        ExistingIdentityId bigint NULL,
        CandidateIdentityId bigint NULL,
        SourceSystem nvarchar(30) NULL,
        Description nvarchar(1000) NULL,
        CreatedAtUtc datetime2(0) NOT NULL
            CONSTRAINT DF_CustomerIdentityConflicts_CreatedAtUtc DEFAULT(SYSUTCDATETIME())
    );
END;

IF OBJECT_ID(N'dbo.CustomerIdentityManualMappings',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomerIdentityManualMappings
    (
        Id bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
        DisplayName nvarchar(300) NULL,
        Phone nvarchar(32) NOT NULL,
        AccountingCode nvarchar(30) NULL,
        RelatedPhone nvarchar(32) NULL,
        DidarContactCode nvarchar(100) NULL,
        IsVerified bit NOT NULL CONSTRAINT DF_CustomerIdentityManualMappings_Verified DEFAULT(1),
        IsActive bit NOT NULL CONSTRAINT DF_CustomerIdentityManualMappings_Active DEFAULT(1),
        CreatedAtUtc datetime2(0) NOT NULL
            CONSTRAINT DF_CustomerIdentityManualMappings_CreatedAtUtc DEFAULT(SYSUTCDATETIME())
    );

    CREATE UNIQUE INDEX UX_CustomerIdentityManualMappings_PhoneCode
        ON dbo.CustomerIdentityManualMappings(Phone,AccountingCode,RelatedPhone,DidarContactCode);
END;
GO

CREATE OR ALTER VIEW dbo.CustomerPhoneDirectory
AS
WITH RankedPhone AS
(
    SELECT
        p.IdentityId,
        p.NormalizedPhone,
        p.SourceSystem,
        p.IsVerified,
        p.Priority,
        rn=ROW_NUMBER() OVER
        (
            PARTITION BY p.NormalizedPhone
            ORDER BY p.IsVerified DESC,p.Priority ASC,p.Id ASC
        )
    FROM dbo.CustomerIdentityPhones p
),
DidarLink AS
(
    SELECT
        l.IdentityId,
        l.DidarContactCode,
        rn=ROW_NUMBER() OVER
        (
            PARTITION BY l.IdentityId
            ORDER BY l.IsVerified DESC,l.Id ASC
        )
    FROM dbo.CustomerIdentityDidarLinks l
),
AccountingLink AS
(
    SELECT
        l.IdentityId,
        l.SourceDatabase,
        l.FiscalYear,
        l.DetailCode,
        l.ShortCode,
        l.CustomerName,
        rn=ROW_NUMBER() OVER
        (
            PARTITION BY l.IdentityId
            ORDER BY l.IsVerified DESC,l.FiscalYear DESC,l.Id ASC
        )
    FROM dbo.CustomerIdentityAccountingLinks l
)
SELECT
    p.NormalizedPhone,
    i.IdentityId,
    COALESCE(NULLIF(i.DisplayName,N''),NULLIF(d.FullName,N''),NULLIF(a.CustomerName,N''),NULLIF(d.CompanyName,N'')) AS DisplayName,
    COALESCE(NULLIF(i.CompanyName,N''),NULLIF(d.CompanyName,N''),NULLIF(a.CustomerName,N'')) AS CompanyName,
    COALESCE(NULLIF(i.OwnerName,N''),NULLIF(d.OwnerName,N'')) AS OwnerName,
    d.DidarContactCode,
    a.SourceDatabase,
    a.FiscalYear,
    a.DetailCode AS AccountingDetailCode,
    a.ShortCode AS AccountingShortCode,
    p.SourceSystem AS MatchSource,
    p.IsVerified
FROM RankedPhone p
INNER JOIN dbo.CustomerIdentities i ON i.IdentityId=p.IdentityId
LEFT JOIN DidarLink dl ON dl.IdentityId=i.IdentityId AND dl.rn=1
LEFT JOIN dbo.DidarContacts d
    ON d.DidarContactCode=dl.DidarContactCode
   AND ISNULL(d.IsDeleted,0)=0
LEFT JOIN AccountingLink a ON a.IdentityId=i.IdentityId AND a.rn=1
WHERE p.rn=1;
GO
