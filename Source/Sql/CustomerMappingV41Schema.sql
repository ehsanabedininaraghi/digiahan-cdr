SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.CustomerAccountingMappings',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomerAccountingMappings
    (
        AccountingCode char(6) NOT NULL PRIMARY KEY,
        CustomerName nvarchar(300) NULL,
        NormalizedPhone nvarchar(32) NULL,
        IdentityId bigint NULL,
        Status nvarchar(20) NOT NULL,
        ErrorMessage nvarchar(1000) NULL,
        SourceFile nvarchar(260) NULL,
        SourceRow int NULL,
        UpdatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_CustomerAccountingMappings_Updated DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT CK_CustomerAccountingMappings_Code CHECK(AccountingCode LIKE '[0-9][0-9][0-9][0-9][0-9][0-9]' AND AccountingCode<>'000000')
    );
    CREATE INDEX IX_CustomerAccountingMappings_Status ON dbo.CustomerAccountingMappings(Status,AccountingCode);
END;

IF OBJECT_ID(N'dbo.CustomerMappingImports',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomerMappingImports
    (
        ImportId uniqueidentifier NOT NULL PRIMARY KEY,
        FileName nvarchar(260) NOT NULL,
        FileHash char(64) NOT NULL,
        ImportedAtUtc datetime2(0) NOT NULL,
        TotalRows int NOT NULL,
        LinkedRows int NOT NULL,
        UnmappedRows int NOT NULL,
        ConflictRows int NOT NULL,
        InvalidRows int NOT NULL
    );
    CREATE UNIQUE INDEX UX_CustomerMappingImports_Hash ON dbo.CustomerMappingImports(FileHash);
END;

IF OBJECT_ID(N'dbo.DataGatheringRuns',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.DataGatheringRuns
    (
        RunId uniqueidentifier NOT NULL PRIMARY KEY,
        StartedAtUtc datetime2(0) NOT NULL,
        FinishedAtUtc datetime2(0) NULL,
        Status nvarchar(20) NOT NULL,
        AccountingStatus nvarchar(20) NULL,
        LinkedCodes int NOT NULL CONSTRAINT DF_DataGatheringRuns_Linked DEFAULT(0),
        UnmappedCodes int NOT NULL CONSTRAINT DF_DataGatheringRuns_Unmapped DEFAULT(0),
        ErrorMessage nvarchar(2000) NULL
    );
END;
