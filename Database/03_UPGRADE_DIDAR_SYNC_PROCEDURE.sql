
USE [digiahan_cdr];
GO

CREATE OR ALTER PROCEDURE dbo.usp_ProcessDidarContactsStage
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @StartedAt DATETIME2(0) = SYSUTCDATETIME();
    DECLARE @Received INT = (SELECT COUNT(*) FROM dbo.DidarContacts_Stage);
    DECLARE @Inserted INT = 0;
    DECLARE @Updated INT = 0;

    BEGIN TRY
        BEGIN TRAN;

        CREATE TABLE #MergeActions ([Action] NVARCHAR(10) NOT NULL);

        ;WITH S AS
        (
            SELECT *,
                   HASHBYTES('SHA2_256', CONCAT_WS(N'|',
                       ISNULL([RecordType],N''), ISNULL([DidarContactCode],N''), ISNULL([CustomerCode],N''), ISNULL([CustomerTitle],N''), ISNULL([FirstName],N''), ISNULL([LastName],N''), ISNULL([Email],N''), ISNULL([Fax],N''), ISNULL([JobTitle],N''), ISNULL([PostalCode],N''), ISNULL([PersonDescription],N''), ISNULL([MobilePhone],N''), ISNULL([Province],N''), ISNULL([County],N''), ISNULL([LandlinePhone],N''), ISNULL([CompanyPhone],N''), ISNULL([CompanyName],N''), ISNULL([DidarCompanyCode],N''), ISNULL([OwnerName],N''), ISNULL([ViewPermission],N''), ISNULL([NationalCode],N''), ISNULL([CreatedDateText],N''), ISNULL([BirthDateText],N''), ISNULL([ContactGroups],N''), ISNULL([OtherPhones],N''), ISNULL([Address1],N''), ISNULL([Websites],N''), ISNULL([OtherEmails],N''), ISNULL([JobTitle2],N''), ISNULL([CallerOwner],N''), ISNULL([Contractors],N''), ISNULL([Phones2],N''), ISNULL([CustomerAddress],N''), ISNULL([SourceCode],N''), ISNULL([City2],N''), ISNULL([Website2],N''), ISNULL([NewSource],N'')
                   )) AS CalcHash
            FROM dbo.DidarContacts_Stage
        )
        MERGE dbo.DidarContacts AS T
        USING S
          ON T.DidarContactCode = S.DidarContactCode
        WHEN MATCHED AND (T.SourceHash IS NULL OR T.SourceHash <> S.CalcHash) THEN
          UPDATE SET
            T.[RecordType] = S.[RecordType],
        T.[CustomerCode] = S.[CustomerCode],
        T.[CustomerTitle] = S.[CustomerTitle],
        T.[FirstName] = S.[FirstName],
        T.[LastName] = S.[LastName],
        T.[Email] = S.[Email],
        T.[Fax] = S.[Fax],
        T.[JobTitle] = S.[JobTitle],
        T.[PostalCode] = S.[PostalCode],
        T.[PersonDescription] = S.[PersonDescription],
        T.[MobilePhone] = S.[MobilePhone],
        T.[Province] = S.[Province],
        T.[County] = S.[County],
        T.[LandlinePhone] = S.[LandlinePhone],
        T.[CompanyPhone] = S.[CompanyPhone],
        T.[CompanyName] = S.[CompanyName],
        T.[DidarCompanyCode] = S.[DidarCompanyCode],
        T.[OwnerName] = S.[OwnerName],
        T.[ViewPermission] = S.[ViewPermission],
        T.[NationalCode] = S.[NationalCode],
        T.[CreatedDateText] = S.[CreatedDateText],
        T.[BirthDateText] = S.[BirthDateText],
        T.[ContactGroups] = S.[ContactGroups],
        T.[OtherPhones] = S.[OtherPhones],
        T.[Address1] = S.[Address1],
        T.[Websites] = S.[Websites],
        T.[OtherEmails] = S.[OtherEmails],
        T.[JobTitle2] = S.[JobTitle2],
        T.[CallerOwner] = S.[CallerOwner],
        T.[Contractors] = S.[Contractors],
        T.[Phones2] = S.[Phones2],
        T.[CustomerAddress] = S.[CustomerAddress],
        T.[SourceCode] = S.[SourceCode],
        T.[City2] = S.[City2],
        T.[Website2] = S.[Website2],
        T.[NewSource] = S.[NewSource],
            T.SourceHash = S.CalcHash,
            T.IsDeleted = 0,
            T.LastSyncedAt = SYSUTCDATETIME()
        WHEN NOT MATCHED BY TARGET THEN
          INSERT ([RecordType], [DidarContactCode], [CustomerCode], [CustomerTitle], [FirstName], [LastName], [Email], [Fax], [JobTitle], [PostalCode], [PersonDescription], [MobilePhone], [Province], [County], [LandlinePhone], [CompanyPhone], [CompanyName], [DidarCompanyCode], [OwnerName], [ViewPermission], [NationalCode], [CreatedDateText], [BirthDateText], [ContactGroups], [OtherPhones], [Address1], [Websites], [OtherEmails], [JobTitle2], [CallerOwner], [Contractors], [Phones2], [CustomerAddress], [SourceCode], [City2], [Website2], [NewSource], SourceHash, IsDeleted, FirstImportedAt, LastSyncedAt)
          VALUES (S.[RecordType], S.[DidarContactCode], S.[CustomerCode], S.[CustomerTitle], S.[FirstName], S.[LastName], S.[Email], S.[Fax], S.[JobTitle], S.[PostalCode], S.[PersonDescription], S.[MobilePhone], S.[Province], S.[County], S.[LandlinePhone], S.[CompanyPhone], S.[CompanyName], S.[DidarCompanyCode], S.[OwnerName], S.[ViewPermission], S.[NationalCode], S.[CreatedDateText], S.[BirthDateText], S.[ContactGroups], S.[OtherPhones], S.[Address1], S.[Websites], S.[OtherEmails], S.[JobTitle2], S.[CallerOwner], S.[Contractors], S.[Phones2], S.[CustomerAddress], S.[SourceCode], S.[City2], S.[Website2], S.[NewSource], S.CalcHash, 0, SYSUTCDATETIME(), SYSUTCDATETIME())
        OUTPUT $action INTO #MergeActions;

        SELECT @Inserted = COUNT(*) FROM #MergeActions WHERE [Action] = 'INSERT';
        SELECT @Updated = COUNT(*) FROM #MergeActions WHERE [Action] = 'UPDATE';

        EXEC dbo.usp_RebuildDidarContactPhones;

        INSERT INTO dbo.DidarSyncHistory
            (SyncType, StartedAt, FinishedAt, RecordsReceived, RecordsInserted, RecordsUpdated, RecordsFailed, Status)
        VALUES
            (N'InitialExcelImport', @StartedAt, SYSUTCDATETIME(), @Received, @Inserted, @Updated, 0, N'Success');

        TRUNCATE TABLE dbo.DidarContacts_Stage;

        COMMIT;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK;

        INSERT INTO dbo.DidarSyncHistory
            (SyncType, StartedAt, FinishedAt, RecordsReceived, RecordsFailed, Status, ErrorMessage)
        VALUES
            (N'InitialExcelImport', @StartedAt, SYSUTCDATETIME(), @Received, @Received, N'Failed', ERROR_MESSAGE());

        THROW;
    END CATCH
END;
GO
