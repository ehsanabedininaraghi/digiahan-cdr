USE [digiahan_cdr];
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;

CREATE OR ALTER PROCEDURE dbo.usp_RebuildDidarContactPhones
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRY
        BEGIN TRAN;

        TRUNCATE TABLE dbo.DidarContactPhones;

        ;WITH RawPhones AS
        (
            SELECT DidarContactCode, MobilePhone AS RawPhone, N'Mobile' AS PhoneType, N'MobilePhone' AS SourceColumn, CAST(1 AS bit) AS IsPrimary FROM dbo.DidarContacts WHERE IsDeleted=0
            UNION ALL SELECT DidarContactCode, LandlinePhone, N'Landline', N'LandlinePhone', 0 FROM dbo.DidarContacts WHERE IsDeleted=0
            UNION ALL SELECT DidarContactCode, CompanyPhone, N'Company', N'CompanyPhone', 0 FROM dbo.DidarContacts WHERE IsDeleted=0
            UNION ALL SELECT DidarContactCode, Fax, N'Fax', N'Fax', 0 FROM dbo.DidarContacts WHERE IsDeleted=0
            UNION ALL SELECT DidarContactCode, OtherPhones, N'Other', N'OtherPhones', 0 FROM dbo.DidarContacts WHERE IsDeleted=0
            UNION ALL SELECT DidarContactCode, Phones2, N'Other2', N'Phones2', 0 FROM dbo.DidarContacts WHERE IsDeleted=0
        ),
        Prepared AS
        (
            SELECT DidarContactCode, PhoneType, SourceColumn, IsPrimary,
                   CleanList = REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                       ISNULL(RawPhone,N''), N'،', N','), N';', N','), N'؛', N','), N'|', N','),
                       CHAR(13), N','), CHAR(10), N','), CHAR(9), N','), N'/', N',')
            FROM RawPhones
        ),
        SplitPhones AS
        (
            SELECT DidarContactCode,
                   LTRIM(RTRIM(value)) AS OriginalPhone,
                   PhoneType,
                   SourceColumn,
                   IsPrimary
            FROM Prepared
            CROSS APPLY STRING_SPLIT(CleanList,N',')
            WHERE LTRIM(RTRIM(value))<>N''
        ),
        ValidPhones AS
        (
            SELECT DidarContactCode, OriginalPhone, PhoneType, SourceColumn, IsPrimary,
                   dbo.NormalizeIranPhone(OriginalPhone) AS NormalizedPhone
            FROM SplitPhones
        ),
        Dedup AS
        (
            SELECT *, ROW_NUMBER() OVER
            (
                PARTITION BY DidarContactCode, NormalizedPhone
                ORDER BY IsPrimary DESC,
                         CASE PhoneType WHEN N'Mobile' THEN 1 WHEN N'Company' THEN 2 WHEN N'Landline' THEN 3 WHEN N'Other' THEN 4 WHEN N'Other2' THEN 5 ELSE 6 END
            ) AS rn
            FROM ValidPhones
            WHERE NormalizedPhone IS NOT NULL AND LEN(NormalizedPhone) BETWEEN 7 AND 15
        )
        INSERT dbo.DidarContactPhones
            (DidarContactCode,OriginalPhone,NormalizedPhone,PhoneType,IsPrimary,SourceColumn,LastSyncedAtUtc)
        SELECT DidarContactCode,OriginalPhone,NormalizedPhone,PhoneType,IsPrimary,SourceColumn,SYSUTCDATETIME()
        FROM Dedup
        WHERE rn=1;

        COMMIT;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT>0 ROLLBACK;
        THROW;
    END CATCH
END;
GO

EXEC dbo.usp_RebuildDidarContactPhones;
GO
