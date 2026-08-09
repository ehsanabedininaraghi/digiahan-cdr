SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.CustomerIdentities',N'U') IS NOT NULL
AND OBJECT_ID(N'dbo.CustomerIdentityPhones',N'U') IS NOT NULL
AND COL_LENGTH(N'dbo.CustomerIdentities',N'PrimaryMobilePhoneId') IS NULL
BEGIN
    ALTER TABLE dbo.CustomerIdentities ADD PrimaryMobilePhoneId bigint NULL;
END;

-- SQL Server compiles a batch before executing ALTER TABLE. Statements that
-- reference the new column must therefore be compiled separately. Dynamic SQL
-- also keeps this migration safe when a previous run added only part of it.
IF OBJECT_ID(N'dbo.CustomerIdentities',N'U') IS NOT NULL
AND OBJECT_ID(N'dbo.CustomerIdentityPhones',N'U') IS NOT NULL
AND COL_LENGTH(N'dbo.CustomerIdentities',N'PrimaryMobilePhoneId') IS NOT NULL
AND NOT EXISTS
(
    SELECT 1
    FROM sys.foreign_keys
    WHERE parent_object_id=OBJECT_ID(N'dbo.CustomerIdentities')
      AND name=N'FK_CustomerIdentities_PrimaryMobilePhone'
)
BEGIN
    EXEC(N'ALTER TABLE dbo.CustomerIdentities WITH NOCHECK
        ADD CONSTRAINT FK_CustomerIdentities_PrimaryMobilePhone
        FOREIGN KEY(PrimaryMobilePhoneId) REFERENCES dbo.CustomerIdentityPhones(Id);');
END;

IF OBJECT_ID(N'dbo.CustomerIdentities',N'U') IS NOT NULL
AND COL_LENGTH(N'dbo.CustomerIdentities',N'PrimaryMobilePhoneId') IS NOT NULL
AND NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id=OBJECT_ID(N'dbo.CustomerIdentities')
      AND name=N'IX_CustomerIdentities_PrimaryMobilePhone'
)
BEGIN
    EXEC(N'CREATE INDEX IX_CustomerIdentities_PrimaryMobilePhone
        ON dbo.CustomerIdentities(PrimaryMobilePhoneId)
        WHERE PrimaryMobilePhoneId IS NOT NULL;');
END;

IF OBJECT_ID(N'dbo.InvoiceNotifications',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.InvoiceNotifications
    (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_InvoiceNotifications PRIMARY KEY,
        SourceDatabase nvarchar(128) NOT NULL,
        FiscalYear int NOT NULL,
        FactorCode int NOT NULL,
        AccountingCustomerCode nvarchar(30) NULL,
        IdentityId bigint NULL,
        InvoiceNumber nvarchar(50) NULL,
        FactorDate nvarchar(10) NULL,
        DeliveryVoucherNumber nvarchar(100) NOT NULL,
        CustomerNameSnapshot nvarchar(400) NULL,
        ProductSummarySnapshot nvarchar(800) NULL,
        PrimaryPhoneSnapshot nvarchar(32) NULL,
        SmsStatus nvarchar(30) NOT NULL CONSTRAINT DF_InvoiceNotifications_Status DEFAULT(N'READY'),
        SmsSentAt datetime2(0) NULL,
        SmsProviderId nvarchar(200) NULL,
        PublicTokenHash binary(32) NULL,
        TokenExpiresAtUtc datetime2(0) NULL,
        MessageBodySnapshot nvarchar(2000) NULL,
        PreparedAtUtc datetime2(0) NULL,
        PreparedBy nvarchar(100) NULL,
        LastError nvarchar(1000) NULL,
        CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_InvoiceNotifications_Created DEFAULT(SYSUTCDATETIME()),
        UpdatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_InvoiceNotifications_Updated DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT FK_InvoiceNotifications_Identity FOREIGN KEY(IdentityId)
            REFERENCES dbo.CustomerIdentities(IdentityId),
        CONSTRAINT CK_InvoiceNotifications_Status CHECK
            (SmsStatus IN (N'READY',N'NEEDS_IDENTITY',N'NEEDS_PHONE',N'PREPARED',N'MANUALLY_SENT',N'CANCELLED'))
    );

    CREATE UNIQUE INDEX UX_InvoiceNotifications_SourceInvoice
        ON dbo.InvoiceNotifications(SourceDatabase,FiscalYear,FactorCode);
    CREATE INDEX IX_InvoiceNotifications_StatusCreated
        ON dbo.InvoiceNotifications(SmsStatus,CreatedAtUtc DESC);
    CREATE UNIQUE INDEX UX_InvoiceNotifications_PublicTokenHash
        ON dbo.InvoiceNotifications(PublicTokenHash)
        WHERE PublicTokenHash IS NOT NULL;
END;

-- v4.3.2 keeps the entire accounting description as the voucher reference.
-- The earlier 100-character column can truncate legitimate descriptions.
IF OBJECT_ID(N'dbo.InvoiceNotifications',N'U') IS NOT NULL
AND COL_LENGTH(N'dbo.InvoiceNotifications',N'DeliveryVoucherNumber') < 2000
BEGIN
    ALTER TABLE dbo.InvoiceNotifications
        ALTER COLUMN DeliveryVoucherNumber nvarchar(1000) NOT NULL;
END;

IF OBJECT_ID(N'dbo.InvoiceNotificationAttempts',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.InvoiceNotificationAttempts
    (
        AttemptId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_InvoiceNotificationAttempts PRIMARY KEY,
        NotificationId bigint NOT NULL,
        Action nvarchar(30) NOT NULL,
        Status nvarchar(30) NOT NULL,
        PhoneSnapshot nvarchar(32) NULL,
        Actor nvarchar(100) NULL,
        ProviderId nvarchar(200) NULL,
        Detail nvarchar(1000) NULL,
        CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_InvoiceNotificationAttempts_Created DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT FK_InvoiceNotificationAttempts_Notification FOREIGN KEY(NotificationId)
            REFERENCES dbo.InvoiceNotifications(Id)
    );
    CREATE INDEX IX_InvoiceNotificationAttempts_Notification
        ON dbo.InvoiceNotificationAttempts(NotificationId,CreatedAtUtc DESC);
END;
