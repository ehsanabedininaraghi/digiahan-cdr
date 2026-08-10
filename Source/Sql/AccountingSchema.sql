IF OBJECT_ID(N'dbo.AccountingSyncRuns', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AccountingSyncRuns
    (
        RunId uniqueidentifier NOT NULL PRIMARY KEY,
        StartedAtUtc datetime2(0) NOT NULL,
        FinishedAtUtc datetime2(0) NULL,
        SourceServer nvarchar(128) NOT NULL,
        SourceDatabase nvarchar(128) NOT NULL,
        FiscalYear int NOT NULL,
        CutoffPersianDate nvarchar(10) NOT NULL,
        Status nvarchar(20) NOT NULL,
        VisitorCount int NOT NULL CONSTRAINT DF_AccountingSyncRuns_VisitorCount DEFAULT(0),
        CustomerCount int NOT NULL CONSTRAINT DF_AccountingSyncRuns_CustomerCount DEFAULT(0),
        InvoiceCount int NOT NULL CONSTRAINT DF_AccountingSyncRuns_InvoiceCount DEFAULT(0),
        InvoiceItemCount int NOT NULL CONSTRAINT DF_AccountingSyncRuns_InvoiceItemCount DEFAULT(0),
        ErrorMessage nvarchar(max) NULL
    );
END;

-- v3.7.4 compatibility: older bridge builds used CutoffDate.
-- The real DigiAhan_CDR schema uses CutoffPersianDate.
IF COL_LENGTH(N'dbo.AccountingSyncRuns',N'CutoffPersianDate') IS NULL
BEGIN
    ALTER TABLE dbo.AccountingSyncRuns
        ADD CutoffPersianDate nvarchar(10) NULL;

    IF COL_LENGTH(N'dbo.AccountingSyncRuns',N'CutoffDate') IS NOT NULL
        EXEC(N'UPDATE dbo.AccountingSyncRuns
               SET CutoffPersianDate=CutoffDate
               WHERE CutoffPersianDate IS NULL;');
END;

IF OBJECT_ID(N'dbo.AccountingVisitors', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AccountingVisitors
    (
        SourceDatabase nvarchar(128) NOT NULL,
        FiscalYear int NOT NULL,
        VisitorId int NOT NULL,
        VisitorName nvarchar(200) NULL,
        RoleType nvarchar(30) NULL,
        IsActive bit NOT NULL CONSTRAINT DF_AccountingVisitors_IsActive DEFAULT(1),
        ImportedAtUtc datetime2(0) NOT NULL,
        CONSTRAINT PK_AccountingVisitors PRIMARY KEY(SourceDatabase, FiscalYear, VisitorId)
    );
END;

IF OBJECT_ID(N'dbo.AccountingCustomers', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AccountingCustomers
    (
        SourceDatabase nvarchar(128) NOT NULL,
        FiscalYear int NOT NULL,
        DetailCode nvarchar(18) NOT NULL,
        ShortCode nvarchar(12) NULL,
        CustomerName nvarchar(400) NULL,
        ManagerName nvarchar(200) NULL,
        EconomicCode nvarchar(100) NULL,
        CustomerTel nvarchar(200) NULL,
        CustomerAddress nvarchar(400) NULL,
        Debit decimal(19,4) NULL,
        Credit decimal(19,4) NULL,
        AccountBalance decimal(19,4) NULL,
        CreditLimit decimal(19,4) NULL,
        ImportedAtUtc datetime2(0) NOT NULL,
        CONSTRAINT PK_AccountingCustomers PRIMARY KEY(SourceDatabase, FiscalYear, DetailCode)
    );
    CREATE INDEX IX_AccountingCustomers_ShortCode
        ON dbo.AccountingCustomers(SourceDatabase, FiscalYear, ShortCode);
END;

IF OBJECT_ID(N'dbo.AccountingInvoices', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AccountingInvoices
    (
        SourceDatabase nvarchar(128) NOT NULL,
        FiscalYear int NOT NULL,
        FactorCode int NOT NULL,
        DocumentNumber decimal(18,0) NULL,
        FactorNumber decimal(18,0) NULL,
        FactorDate nvarchar(10) NULL,
        TypeIndex int NULL,
        TypeDescription nvarchar(500) NULL,
        FactorDescription nvarchar(1000) NULL,
        CustomerShortCode nvarchar(12) NULL,
        CustomerDetailCode nvarchar(18) NULL,
        CustomerName nvarchar(400) NULL,
        Amount decimal(19,4) NULL,
        VisitorId int NULL,
        VisitorName nvarchar(400) NULL,
        ImportedAtUtc datetime2(0) NOT NULL,
        CONSTRAINT PK_AccountingInvoices PRIMARY KEY(SourceDatabase, FiscalYear, FactorCode)
    );
    CREATE INDEX IX_AccountingInvoices_Date
        ON dbo.AccountingInvoices(SourceDatabase, FiscalYear, FactorDate);
    CREATE INDEX IX_AccountingInvoices_Customer
        ON dbo.AccountingInvoices(SourceDatabase, FiscalYear, CustomerDetailCode);
END;

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

IF OBJECT_ID(N'dbo.AccountingInvoices', N'U') IS NOT NULL
AND COL_LENGTH(N'dbo.AccountingInvoices',N'FactorDescription') IS NULL
    ALTER TABLE dbo.AccountingInvoices ADD FactorDescription nvarchar(1000) NULL;

IF OBJECT_ID(N'dbo.AccountingInvoiceItems', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AccountingInvoiceItems
    (
        SourceDatabase nvarchar(128) NOT NULL,
        FiscalYear int NOT NULL,
        ItemCode int NOT NULL,
        FactorCode int NOT NULL,
        FactorDate nvarchar(10) NULL,
        ItemRow int NULL,
        ProductCode nvarchar(12) NULL,
        ProductName nvarchar(400) NULL,
        Description nvarchar(400) NULL,
        Quantity float NULL,
        UnitPrice decimal(19,4) NULL,
        TotalPrice decimal(19,4) NULL,
        ImportedAtUtc datetime2(0) NOT NULL,
        CONSTRAINT PK_AccountingInvoiceItems PRIMARY KEY(SourceDatabase, FiscalYear, ItemCode)
    );
    CREATE INDEX IX_AccountingInvoiceItems_Factor
        ON dbo.AccountingInvoiceItems(SourceDatabase, FiscalYear, FactorCode);
END;

IF OBJECT_ID(N'dbo.CustomerMappings', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomerMappings
    (
        MappingId bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
        DidarContactCode nvarchar(100) NOT NULL,
        AccountingSourceDatabase nvarchar(128) NOT NULL,
        AccountingFiscalYear int NOT NULL,
        AccountingDetailCode nvarchar(18) NOT NULL,
        AccountingShortCode nvarchar(12) NULL,
        IsVerified bit NOT NULL CONSTRAINT DF_CustomerMappings_IsVerified DEFAULT(0),
        VerifiedBy nvarchar(100) NULL,
        VerifiedAtUtc datetime2(0) NULL,
        Notes nvarchar(500) NULL,
        CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_CustomerMappings_CreatedAtUtc DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT UQ_CustomerMappings_Didar UNIQUE(DidarContactCode, AccountingSourceDatabase, AccountingFiscalYear),
        CONSTRAINT UQ_CustomerMappings_Accounting UNIQUE(AccountingSourceDatabase, AccountingFiscalYear, AccountingDetailCode)
    );
END;
