SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION;

DECLARE @NewDidar TABLE
(
    IdentityId bigint NOT NULL,
    DidarContactCode nvarchar(100) NOT NULL
);

;WITH MissingDidar AS
(
    SELECT d.DidarContactCode,d.FullName,d.CompanyName,d.OwnerName
    FROM dbo.DidarContacts d
    WHERE ISNULL(d.IsDeleted,0)=0
      AND NULLIF(LTRIM(RTRIM(d.DidarContactCode)),N'') IS NOT NULL
      AND NOT EXISTS
      (
          SELECT 1 FROM dbo.CustomerIdentityDidarLinks l
          WHERE l.DidarContactCode=d.DidarContactCode
      )
)
MERGE dbo.CustomerIdentities AS target
USING MissingDidar AS source ON 1=0
WHEN NOT MATCHED THEN
    INSERT(DisplayName,CompanyName,OwnerName)
    VALUES(source.FullName,source.CompanyName,source.OwnerName)
OUTPUT inserted.IdentityId,source.DidarContactCode
INTO @NewDidar(IdentityId,DidarContactCode);

INSERT dbo.CustomerIdentityDidarLinks(IdentityId,DidarContactCode,IsVerified)
SELECT IdentityId,DidarContactCode,1 FROM @NewDidar;

UPDATE i
SET DisplayName=COALESCE(NULLIF(d.FullName,N''),i.DisplayName),
    CompanyName=COALESCE(NULLIF(d.CompanyName,N''),i.CompanyName),
    OwnerName=COALESCE(NULLIF(d.OwnerName,N''),i.OwnerName),
    UpdatedAtUtc=SYSUTCDATETIME()
FROM dbo.CustomerIdentities i
INNER JOIN dbo.CustomerIdentityDidarLinks l ON l.IdentityId=i.IdentityId
INNER JOIN dbo.DidarContacts d ON d.DidarContactCode=l.DidarContactCode
WHERE ISNULL(d.IsDeleted,0)=0;

INSERT dbo.CustomerIdentityPhones
    (IdentityId,NormalizedPhone,RawPhone,PhoneType,SourceSystem,IsPrimary,IsVerified,Priority)
SELECT l.IdentityId,p.NormalizedPhone,p.OriginalPhone,
       COALESCE(NULLIF(p.PhoneType,N''),N'DIDAR'),N'DIDAR',p.IsPrimary,1,10
FROM dbo.DidarContactPhones p
INNER JOIN dbo.CustomerIdentityDidarLinks l ON l.DidarContactCode=p.DidarContactCode
INNER JOIN dbo.DidarContacts d ON d.DidarContactCode=p.DidarContactCode AND ISNULL(d.IsDeleted,0)=0
WHERE NULLIF(LTRIM(RTRIM(p.NormalizedPhone)),N'') IS NOT NULL
  AND NOT EXISTS
  (
      SELECT 1 FROM dbo.CustomerIdentityPhones existing
      WHERE existing.IdentityId=l.IdentityId
        AND existing.NormalizedPhone=p.NormalizedPhone
        AND existing.SourceSystem=N'DIDAR'
  );

UPDATE p
SET RawPhone=COALESCE(NULLIF(dp.OriginalPhone,N''),p.RawPhone),
    PhoneType=COALESCE(NULLIF(dp.PhoneType,N''),p.PhoneType),
    IsPrimary=dp.IsPrimary,IsVerified=1,Priority=10
FROM dbo.CustomerIdentityPhones p
INNER JOIN dbo.CustomerIdentityDidarLinks l ON l.IdentityId=p.IdentityId
INNER JOIN dbo.DidarContactPhones dp
    ON dp.DidarContactCode=l.DidarContactCode AND dp.NormalizedPhone=p.NormalizedPhone
WHERE p.SourceSystem=N'DIDAR';

COMMIT TRANSACTION;

SELECT
    TotalActiveDidar=(SELECT COUNT_BIG(*) FROM dbo.DidarContacts WHERE ISNULL(IsDeleted,0)=0),
    LinkedDidar=(SELECT COUNT_BIG(*) FROM dbo.CustomerIdentityDidarLinks l INNER JOIN dbo.DidarContacts d ON d.DidarContactCode=l.DidarContactCode WHERE ISNULL(d.IsDeleted,0)=0),
    TotalIdentities=(SELECT COUNT_BIG(*) FROM dbo.CustomerIdentities),
    DidarPhones=(SELECT COUNT_BIG(*) FROM dbo.CustomerIdentityPhones WHERE SourceSystem=N'DIDAR'),
    CreatedIdentities=(SELECT COUNT_BIG(*) FROM @NewDidar);
