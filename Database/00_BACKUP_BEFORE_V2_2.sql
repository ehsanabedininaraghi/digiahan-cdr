USE [digiahan_cdr];
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Suffix varchar(30) = REPLACE(REPLACE(REPLACE(CONVERT(varchar(19),GETDATE(),120),'-',''),':',''),' ','_');
DECLARE @sql nvarchar(max);

IF OBJECT_ID(N'dbo.DidarContacts',N'U') IS NOT NULL
BEGIN
    SET @sql = N'SELECT * INTO dbo.DidarContacts_Backup_' + @Suffix + N' FROM dbo.DidarContacts;';
    EXEC sys.sp_executesql @sql;
END;

IF OBJECT_ID(N'dbo.DidarContactPhones',N'U') IS NOT NULL
BEGIN
    SET @sql = N'SELECT * INTO dbo.DidarContactPhones_Backup_' + @Suffix + N' FROM dbo.DidarContactPhones;';
    EXEC sys.sp_executesql @sql;
END;

PRINT N'Backup tables created successfully. Suffix: ' + @Suffix;
GO
