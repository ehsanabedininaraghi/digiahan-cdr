USE [digiahan_cdr];
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'dbo.DidarContacts', N'U') IS NULL
    THROW 51000, 'dbo.DidarContacts does not exist. Run the Didar import package first.', 1;

IF OBJECT_ID(N'dbo.DidarContactPhones', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.DidarContactPhones
    (
        Id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DidarContactPhones PRIMARY KEY,
        DidarContactCode NVARCHAR(100) NOT NULL,
        OriginalPhone NVARCHAR(500) NOT NULL,
        NormalizedPhone NVARCHAR(50) NOT NULL,
        PhoneType NVARCHAR(50) NOT NULL,
        IsPrimary BIT NOT NULL CONSTRAINT DF_DidarContactPhones_IsPrimary DEFAULT 0,
        CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_DidarContactPhones_CreatedAt DEFAULT SYSUTCDATETIME()
    );
END;
GO

IF COL_LENGTH(N'dbo.DidarContactPhones', N'SourceColumn') IS NULL
    ALTER TABLE dbo.DidarContactPhones ADD SourceColumn NVARCHAR(50) NULL;
GO

IF COL_LENGTH(N'dbo.DidarContactPhones', N'LastSyncedAtUtc') IS NULL
    ALTER TABLE dbo.DidarContactPhones ADD LastSyncedAtUtc DATETIME2(0) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.DidarContactPhones') AND name=N'IX_DidarContactPhones_NormalizedPhone')
    CREATE INDEX IX_DidarContactPhones_NormalizedPhone
        ON dbo.DidarContactPhones(NormalizedPhone)
        INCLUDE (DidarContactCode, IsPrimary, PhoneType);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.DidarContactPhones') AND name=N'IX_DidarContactPhones_DidarContactCode')
    CREATE INDEX IX_DidarContactPhones_DidarContactCode
        ON dbo.DidarContactPhones(DidarContactCode);
GO

CREATE OR ALTER FUNCTION dbo.NormalizeIranPhone(@Phone NVARCHAR(500))
RETURNS NVARCHAR(50)
AS
BEGIN
    DECLARE @source NVARCHAR(500) = ISNULL(@Phone,N'');
    DECLARE @result NVARCHAR(50) = N'';
    DECLARE @i INT = 1;
    DECLARE @c NCHAR(1);

    SET @source = REPLACE(@source,N'۰',N'0'); SET @source = REPLACE(@source,N'۱',N'1');
    SET @source = REPLACE(@source,N'۲',N'2'); SET @source = REPLACE(@source,N'۳',N'3');
    SET @source = REPLACE(@source,N'۴',N'4'); SET @source = REPLACE(@source,N'۵',N'5');
    SET @source = REPLACE(@source,N'۶',N'6'); SET @source = REPLACE(@source,N'۷',N'7');
    SET @source = REPLACE(@source,N'۸',N'8'); SET @source = REPLACE(@source,N'۹',N'9');
    SET @source = REPLACE(@source,N'٠',N'0'); SET @source = REPLACE(@source,N'١',N'1');
    SET @source = REPLACE(@source,N'٢',N'2'); SET @source = REPLACE(@source,N'٣',N'3');
    SET @source = REPLACE(@source,N'٤',N'4'); SET @source = REPLACE(@source,N'٥',N'5');
    SET @source = REPLACE(@source,N'٦',N'6'); SET @source = REPLACE(@source,N'٧',N'7');
    SET @source = REPLACE(@source,N'٨',N'8'); SET @source = REPLACE(@source,N'٩',N'9');

    WHILE @i <= LEN(@source) AND LEN(@result) < 50
    BEGIN
        SET @c = SUBSTRING(@source,@i,1);
        IF @c >= N'0' AND @c <= N'9' SET @result += @c;
        SET @i += 1;
    END;

    IF LEFT(@result,4)=N'0098' SET @result=SUBSTRING(@result,5,50);
    ELSE IF LEFT(@result,2)=N'98' SET @result=SUBSTRING(@result,3,50);

    IF LEN(@result)=10 AND LEFT(@result,1) IN (N'9',N'2') SET @result=N'0'+@result;

    RETURN NULLIF(@result,N'');
END;
GO
