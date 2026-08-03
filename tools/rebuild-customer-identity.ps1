param(
    [string]$RepositoryRoot = "D:\DigiAhan\CDR3.1.0git",
    [string]$MappingsFile = "",
    [switch]$KeepExistingAutoData
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Get-ConnectionStrings {
    param([string]$SourceRoot)

    $result = @{
        DigiAhanCdr = $null
        AccountingLegacy = $null
        AccountingServer = "COREI5"
        AccountingDatabase = "daftar1405"
        FiscalYear = 1405
    }

    $files = Get-ChildItem -Path $SourceRoot -Filter "appsettings*.json" -File -ErrorAction SilentlyContinue |
        Sort-Object FullName

    foreach ($file in $files) {
        try {
            $json = Get-Content $file.FullName -Raw -Encoding UTF8 | ConvertFrom-Json

            $connectionStringsProperty = $json.PSObject.Properties["ConnectionStrings"]
            if ($connectionStringsProperty) {
                $connectionStrings = $connectionStringsProperty.Value

                $destinationProperty = $connectionStrings.PSObject.Properties["DigiAhanCdr"]
                if ($destinationProperty -and $destinationProperty.Value) {
                    $result.DigiAhanCdr = [string]$destinationProperty.Value
                }

                $legacyProperty = $connectionStrings.PSObject.Properties["AccountingLegacy"]
                if ($legacyProperty -and $legacyProperty.Value) {
                    $result.AccountingLegacy = [string]$legacyProperty.Value
                }
            }

            $accountingProperty = $json.PSObject.Properties["Accounting"]
            if ($accountingProperty) {
                $accounting = $accountingProperty.Value

                $serverProperty = $accounting.PSObject.Properties["Server"]
                if ($serverProperty -and $serverProperty.Value) {
                    $result.AccountingServer = [string]$serverProperty.Value
                }

                $databaseProperty = $accounting.PSObject.Properties["Database"]
                if ($databaseProperty -and $databaseProperty.Value) {
                    $result.AccountingDatabase = [string]$databaseProperty.Value
                }

                $yearProperty = $accounting.PSObject.Properties["FiscalYear"]
                if ($yearProperty -and $yearProperty.Value) {
                    $result.FiscalYear = [int]$yearProperty.Value
                }
            }
        }
        catch {
            Write-Warning "Could not parse $($file.FullName): $($_.Exception.Message)"
        }
    }

    if ([string]::IsNullOrWhiteSpace($result.DigiAhanCdr)) {
        throw "ConnectionStrings:DigiAhanCdr was not found in Source\appsettings*.json."
    }

    return $result
}

function Open-SqlConnection {
    param([string]$ConnectionString)
    Add-Type -AssemblyName System.Data
    $connection = New-Object System.Data.SqlClient.SqlConnection $ConnectionString
    $connection.Open()
    return $connection
}

function Invoke-SqlNonQuery {
    param(
        [System.Data.SqlClient.SqlConnection]$Connection,
        [string]$Sql,
        [hashtable]$Parameters = @{},
        [System.Data.SqlClient.SqlTransaction]$Transaction = $null
    )

    $command = $Connection.CreateCommand()
    $command.CommandText = $Sql
    $command.CommandTimeout = 180
    if ($Transaction) { $command.Transaction = $Transaction }

    foreach ($key in $Parameters.Keys) {
        $value = $Parameters[$key]
        $parameter = $command.Parameters.AddWithValue($key, $(if ($null -eq $value) { [DBNull]::Value } else { $value }))
        [void]$parameter
    }

    try { return $command.ExecuteNonQuery() }
    finally { $command.Dispose() }
}

function Invoke-SqlScalar {
    param(
        [System.Data.SqlClient.SqlConnection]$Connection,
        [string]$Sql,
        [hashtable]$Parameters = @{},
        [System.Data.SqlClient.SqlTransaction]$Transaction = $null
    )

    $command = $Connection.CreateCommand()
    $command.CommandText = $Sql
    $command.CommandTimeout = 180
    if ($Transaction) { $command.Transaction = $Transaction }

    foreach ($key in $Parameters.Keys) {
        $value = $Parameters[$key]
        $parameter = $command.Parameters.AddWithValue($key, $(if ($null -eq $value) { [DBNull]::Value } else { $value }))
        [void]$parameter
    }

    try { return $command.ExecuteScalar() }
    finally { $command.Dispose() }
}

function Invoke-SqlTable {
    param(
        [System.Data.SqlClient.SqlConnection]$Connection,
        [string]$Sql,
        [hashtable]$Parameters = @{},
        [System.Data.SqlClient.SqlTransaction]$Transaction = $null
    )

    $command = $Connection.CreateCommand()
    $command.CommandText = $Sql
    $command.CommandTimeout = 180
    if ($Transaction) { $command.Transaction = $Transaction }

    foreach ($key in $Parameters.Keys) {
        $value = $Parameters[$key]
        $parameter = $command.Parameters.AddWithValue($key, $(if ($null -eq $value) { [DBNull]::Value } else { $value }))
        [void]$parameter
    }

    $adapter = New-Object System.Data.SqlClient.SqlDataAdapter $command
    $table = New-Object System.Data.DataTable

    try {
        [void]$adapter.Fill($table)
        return ,$table
    }
    finally {
        $adapter.Dispose()
        $command.Dispose()
    }
}

function Invoke-SqlScript {
    param(
        [System.Data.SqlClient.SqlConnection]$Connection,
        [string]$Path
    )

    $text = Get-Content $Path -Raw -Encoding UTF8
    $batches = [regex]::Split($text, '(?im)^\s*GO\s*;?\s*$')

    foreach ($batch in $batches) {
        if (-not [string]::IsNullOrWhiteSpace($batch)) {
            Invoke-SqlNonQuery -Connection $Connection -Sql $batch | Out-Null
        }
    }
}

function Convert-PersianDigits {
    param([string]$Value)

    if ($null -eq $Value) { return "" }

    $map = @{
        '۰'='0';'۱'='1';'۲'='2';'۳'='3';'۴'='4';
        '۵'='5';'۶'='6';'۷'='7';'۸'='8';'۹'='9';
        '٠'='0';'١'='1';'٢'='2';'٣'='3';'٤'='4';
        '٥'='5';'٦'='6';'٧'='7';'٨'='8';'٩'='9'
    }

    $builder = New-Object System.Text.StringBuilder
    foreach ($char in $Value.ToCharArray()) {
        $key = [string]$char
        if ($map.ContainsKey($key)) { [void]$builder.Append($map[$key]) }
        else { [void]$builder.Append($char) }
    }

    return $builder.ToString()
}

function Normalize-Phone {
    param([string]$Value)

    $digits = [regex]::Replace((Convert-PersianDigits $Value), '\D+', '')
    if ([string]::IsNullOrWhiteSpace($digits)) { return $null }

    if ($digits.StartsWith("0098") -and $digits.Length -gt 4) {
        $digits = "0" + $digits.Substring(4)
    }
    elseif ($digits.StartsWith("98") -and $digits.Length -ge 12) {
        $digits = "0" + $digits.Substring(2)
    }
    elseif ($digits.Length -eq 10 -and -not $digits.StartsWith("0")) {
        $digits = "0" + $digits
    }

    if ($digits.Length -lt 7 -or $digits.Length -gt 13) { return $null }
    return $digits
}

function Test-NormalizedPhone {
    param([string]$Phone)

    if ([string]::IsNullOrWhiteSpace($Phone)) { return $false }

    return (
        $Phone -match '^09\d{9}$' -or
        $Phone -match '^0\d{10}$' -or
        $Phone -match '^\d{8}$' -or
        $Phone -match '^\d{7}$'
    )
}

function Extract-Phones {
    param([string]$Value)

    $text = Convert-PersianDigits $Value
    if ([string]::IsNullOrWhiteSpace($text)) { return @() }

    # A hyphen between two complete numbers is a separator, not phone formatting.
    $text = [regex]::Replace(
        $text,
        '(?<=\d{7})\s*-\s*(?=(?:0098|98|0)?\d{7,})',
        '|'
    )

    $result = [System.Collections.Generic.HashSet[string]]::new()
    $parts = [regex]::Split($text, '[,;/|\r\n]+')

    foreach ($part in $parts) {
        $direct = Normalize-Phone $part
        if (Test-NormalizedPhone $direct) {
            [void]$result.Add($direct)
        }

        $patterns = @(
            '(?<!\d)0098\d{10}(?!\d)',
            '(?<!\d)98\d{10}(?!\d)',
            '(?<!\d)09\d{9}(?!\d)',
            '(?<!\d)0\d{10}(?!\d)',
            '(?<!\d)\d{8}(?!\d)',
            '(?<!\d)\d{7}(?!\d)'
        )

        foreach ($pattern in $patterns) {
            foreach ($match in [regex]::Matches($part, $pattern)) {
                $phone = Normalize-Phone $match.Value
                if (Test-NormalizedPhone $phone) {
                    [void]$result.Add($phone)
                }
            }
        }
    }

    return @($result)
}

function Get-AccountingCodeKeys {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) { return @() }

    $valueClean = $Value.Trim().ToUpperInvariant()
    $keys = [System.Collections.Generic.HashSet[string]]::new()
    [void]$keys.Add($valueClean)

    if ($valueClean.Contains("/")) {
        $tail = $valueClean.Substring($valueClean.LastIndexOf("/") + 1)
        if (-not [string]::IsNullOrWhiteSpace($tail)) {
            [void]$keys.Add($tail)
        }
    }

    return @($keys)
}

function New-Identity {
    param(
        [System.Data.SqlClient.SqlConnection]$Connection,
        [System.Data.SqlClient.SqlTransaction]$Transaction,
        [string]$DisplayName,
        [string]$CompanyName,
        [string]$OwnerName
    )

    $sql = @"
INSERT INTO dbo.CustomerIdentities(DisplayName,CompanyName,OwnerName)
OUTPUT inserted.IdentityId
VALUES(@display,@company,@owner);
"@

    return [int64](Invoke-SqlScalar -Connection $Connection -Transaction $Transaction -Sql $sql -Parameters @{
        "@display" = $DisplayName
        "@company" = $CompanyName
        "@owner" = $OwnerName
    })
}

function Add-Phone {
    param(
        [System.Data.SqlClient.SqlConnection]$Connection,
        [System.Data.SqlClient.SqlTransaction]$Transaction,
        [int64]$IdentityId,
        [string]$Phone,
        [string]$RawPhone,
        [string]$PhoneType,
        [string]$SourceSystem,
        [bool]$Verified,
        [int]$Priority
    )

    $normalized = Normalize-Phone $Phone
    if (-not $normalized) { return }

    $sql = @"
IF NOT EXISTS
(
    SELECT 1
    FROM dbo.CustomerIdentityPhones
    WHERE IdentityId=@identity AND NormalizedPhone=@phone AND SourceSystem=@source
)
INSERT INTO dbo.CustomerIdentityPhones
    (IdentityId,NormalizedPhone,RawPhone,PhoneType,SourceSystem,IsPrimary,IsVerified,Priority)
VALUES
    (@identity,@phone,@raw,@type,@source,0,@verified,@priority);
"@

    Invoke-SqlNonQuery -Connection $Connection -Transaction $Transaction -Sql $sql -Parameters @{
        "@identity" = $IdentityId
        "@phone" = $normalized
        "@raw" = $RawPhone
        "@type" = $PhoneType
        "@source" = $SourceSystem
        "@verified" = $Verified
        "@priority" = $Priority
    } | Out-Null
}

$sourceRoot = Join-Path $RepositoryRoot "Source"
$schemaPath = Join-Path $sourceRoot "Sql\CustomerIdentitySchema.sql"

if (-not (Test-Path $schemaPath)) {
    $schemaPath = Join-Path $RepositoryRoot "patch\Source\Sql\CustomerIdentitySchema.sql"
}
if (-not (Test-Path $schemaPath)) {
    throw "CustomerIdentitySchema.sql was not found."
}

if ([string]::IsNullOrWhiteSpace($MappingsFile)) {
    $MappingsFile = Join-Path $RepositoryRoot "config\manual-customer-mappings.csv"
}
if (-not (Test-Path $MappingsFile)) {
    Write-Warning "Manual mappings file not found: $MappingsFile"
}

$config = Get-ConnectionStrings -SourceRoot $sourceRoot
$connection = Open-SqlConnection -ConnectionString $config.DigiAhanCdr

try {
    Write-Host "[Identity 1/6] Ensuring schema..." -ForegroundColor Cyan
    Invoke-SqlScript -Connection $connection -Path $schemaPath

    if (-not $KeepExistingAutoData) {
        Write-Host "[Identity 2/6] Clearing generated identity data..." -ForegroundColor Cyan
        $clearSql = @"
DELETE FROM dbo.CustomerIdentityConflicts;
DELETE FROM dbo.CustomerIdentityPhones;
DELETE FROM dbo.CustomerIdentityDidarLinks;
DELETE FROM dbo.CustomerIdentityAccountingLinks;
DELETE FROM dbo.CustomerIdentities;
DBCC CHECKIDENT ('dbo.CustomerIdentities', RESEED, 0);
"@
        Invoke-SqlNonQuery -Connection $connection -Sql $clearSql | Out-Null
    }

    $accountingRows = Invoke-SqlTable -Connection $connection -Sql @"
SELECT SourceDatabase,FiscalYear,DetailCode,ShortCode,CustomerName,ManagerName,CustomerTel
FROM dbo.AccountingCustomers
ORDER BY FiscalYear DESC,DetailCode;
"@

    $didarRows = Invoke-SqlTable -Connection $connection -Sql @"
SELECT DidarContactCode,FullName,CompanyName,OwnerName
FROM dbo.DidarContacts
WHERE ISNULL(IsDeleted,0)=0;
"@

    $didarPhoneRows = Invoke-SqlTable -Connection $connection -Sql @"
SELECT
    p.DidarContactCode,
    p.OriginalPhone AS RawPhone,
    p.NormalizedPhone,
    p.PhoneType,
    p.IsPrimary
FROM dbo.DidarContactPhones p
INNER JOIN dbo.DidarContacts d
    ON d.DidarContactCode=p.DidarContactCode
   AND ISNULL(d.IsDeleted,0)=0;
"@

    $phoneToIdentity = @{}
    $accountingCodeToIdentity = @{}
    $didarCodeToIdentity = @{}

    $transaction = $connection.BeginTransaction()
    try {
        Write-Host "[Identity 3/6] Building accounting identities..." -ForegroundColor Cyan

        foreach ($row in $accountingRows.Rows) {
            $display = [string]$row.CustomerName
            if ([string]::IsNullOrWhiteSpace($display)) { $display = [string]$row.ManagerName }

            $identityId = New-Identity -Connection $connection -Transaction $transaction `
                -DisplayName $display -CompanyName ([string]$row.CustomerName) -OwnerName $null

            $accountingLinkSql = @"
INSERT INTO dbo.CustomerIdentityAccountingLinks
    (IdentityId,SourceDatabase,FiscalYear,DetailCode,ShortCode,CustomerName,IsVerified)
VALUES
    (@identity,@db,@fy,@detail,@short,@name,0);
"@
            Invoke-SqlNonQuery -Connection $connection -Transaction $transaction `
                -Sql $accountingLinkSql -Parameters @{
                    "@identity" = $identityId
                    "@db" = [string]$row.SourceDatabase
                    "@fy" = [int]$row.FiscalYear
                    "@detail" = [string]$row.DetailCode
                    "@short" = [string]$row.ShortCode
                    "@name" = [string]$row.CustomerName
                } | Out-Null

            foreach ($code in @([string]$row.DetailCode,[string]$row.ShortCode)) {
                foreach ($codeKey in (Get-AccountingCodeKeys $code)) {
                    $accountingCodeToIdentity[$codeKey] = $identityId
                }
            }

            foreach ($phone in (Extract-Phones ([string]$row.CustomerTel))) {
                Add-Phone -Connection $connection -Transaction $transaction -IdentityId $identityId `
                    -Phone $phone -RawPhone ([string]$row.CustomerTel) -PhoneType "ACCOUNTING" `
                    -SourceSystem "ACCOUNTING" -Verified:$false -Priority 20

                if (-not $phoneToIdentity.ContainsKey($phone)) {
                    $phoneToIdentity[$phone] = $identityId
                }
                elseif ($phoneToIdentity[$phone] -ne $identityId) {
                    $conflictSql = @"
INSERT INTO dbo.CustomerIdentityConflicts
    (NormalizedPhone,ExistingIdentityId,CandidateIdentityId,SourceSystem,Description)
VALUES
    (@phone,@existing,@candidate,N'ACCOUNTING',N'یک شماره در بیش از یک مشتری حسابداری دیده شد.');
"@
                    Invoke-SqlNonQuery -Connection $connection -Transaction $transaction `
                        -Sql $conflictSql -Parameters @{
                            "@phone" = $phone
                            "@existing" = $phoneToIdentity[$phone]
                            "@candidate" = $identityId
                        } | Out-Null
                }
            }
        }

        Write-Host "[Identity 4/6] Linking Didar contacts and all phone fields..." -ForegroundColor Cyan

        $didarPhonesByCode = @{}
        foreach ($row in $didarPhoneRows.Rows) {
            $code = [string]$row.DidarContactCode
            if (-not $didarPhonesByCode.ContainsKey($code)) {
                $didarPhonesByCode[$code] = [System.Collections.Generic.List[object]]::new()
            }
            $didarPhonesByCode[$code].Add($row)
        }

        $phoneColumnNames = @()
        $columnSql = @"
SELECT c.name
FROM sys.columns c
INNER JOIN sys.types t ON t.user_type_id=c.user_type_id
WHERE c.object_id=OBJECT_ID(N'dbo.DidarContacts')
  AND t.name IN (N'nvarchar',N'varchar',N'nchar',N'char')
  AND
  (
       LOWER(c.name) LIKE N'%phone%'
    OR LOWER(c.name) LIKE N'%tel%'
    OR LOWER(c.name) LIKE N'%mobile%'
    OR LOWER(c.name) LIKE N'%telephone%'
    OR c.name LIKE N'%تلفن%'
    OR c.name LIKE N'%شماره%'
  );
"@
        $columnRows = Invoke-SqlTable -Connection $connection -Sql $columnSql -Transaction $transaction
        foreach ($columnRow in $columnRows.Rows) {
            $phoneColumnNames += [string]$columnRow.name
        }

        $extraDidarRows = $null
        if ($phoneColumnNames.Count -gt 0) {
            $escapedColumns = $phoneColumnNames | ForEach-Object { "[" + $_.Replace("]", "]]") + "]" }
            $extraDidarRows = Invoke-SqlTable -Connection $connection -Transaction $transaction -Sql (
                "SELECT DidarContactCode," + ($escapedColumns -join ",") +
                " FROM dbo.DidarContacts WHERE ISNULL(IsDeleted,0)=0;"
            )
        }

        $extraByCode = @{}
        if ($extraDidarRows) {
            foreach ($row in $extraDidarRows.Rows) {
                $extraByCode[[string]$row.DidarContactCode] = $row
            }
        }

        foreach ($contact in $didarRows.Rows) {
            $code = [string]$contact.DidarContactCode
            $phones = [System.Collections.Generic.HashSet[string]]::new()

            if ($didarPhonesByCode.ContainsKey($code)) {
                foreach ($phoneRow in $didarPhonesByCode[$code]) {
                    foreach ($candidate in @([string]$phoneRow.NormalizedPhone,[string]$phoneRow.RawPhone)) {
                        foreach ($phone in (Extract-Phones $candidate)) {
                            [void]$phones.Add($phone)
                        }
                    }
                }
            }

            if ($extraByCode.ContainsKey($code)) {
                $extraRow = $extraByCode[$code]
                foreach ($columnName in $phoneColumnNames) {
                    foreach ($phone in (Extract-Phones ([string]$extraRow[$columnName]))) {
                        [void]$phones.Add($phone)
                    }
                }
            }

            $identityId = $null
            foreach ($phone in $phones) {
                if ($phoneToIdentity.ContainsKey($phone)) {
                    $identityId = [int64]$phoneToIdentity[$phone]
                    break
                }
            }

            if (-not $identityId) {
                $identityId = New-Identity -Connection $connection -Transaction $transaction `
                    -DisplayName ([string]$contact.FullName) `
                    -CompanyName ([string]$contact.CompanyName) `
                    -OwnerName ([string]$contact.OwnerName)
            }
            else {
                $updateIdentitySql = @"
UPDATE dbo.CustomerIdentities
SET DisplayName=COALESCE(NULLIF(@display,N''),DisplayName),
    CompanyName=COALESCE(NULLIF(@company,N''),CompanyName),
    OwnerName=COALESCE(NULLIF(@owner,N''),OwnerName),
    UpdatedAtUtc=SYSUTCDATETIME()
WHERE IdentityId=@identity;
"@
                Invoke-SqlNonQuery -Connection $connection -Transaction $transaction `
                    -Sql $updateIdentitySql -Parameters @{
                        "@display" = [string]$contact.FullName
                        "@company" = [string]$contact.CompanyName
                        "@owner" = [string]$contact.OwnerName
                        "@identity" = $identityId
                    } | Out-Null
            }

            $didarLinkSql = @"
IF NOT EXISTS
(
    SELECT 1 FROM dbo.CustomerIdentityDidarLinks WHERE DidarContactCode=@code
)
INSERT INTO dbo.CustomerIdentityDidarLinks(IdentityId,DidarContactCode,IsVerified)
VALUES(@identity,@code,0);
"@
            Invoke-SqlNonQuery -Connection $connection -Transaction $transaction `
                -Sql $didarLinkSql -Parameters @{
                    "@identity" = $identityId
                    "@code" = $code
                } | Out-Null

            $didarCodeToIdentity[$code] = $identityId

            foreach ($phone in $phones) {
                Add-Phone -Connection $connection -Transaction $transaction -IdentityId $identityId `
                    -Phone $phone -RawPhone $phone -PhoneType "DIDAR" `
                    -SourceSystem "DIDAR" -Verified:$false -Priority 30

                if (-not $phoneToIdentity.ContainsKey($phone)) {
                    $phoneToIdentity[$phone] = $identityId
                }
            }
        }

        Write-Host "[Identity 5/6] Applying verified manual mappings..." -ForegroundColor Cyan

        if (Test-Path $MappingsFile) {
            $manualRows = Import-Csv $MappingsFile

            foreach ($mapping in $manualRows) {
                $phone = Normalize-Phone ([string]$mapping.Phone)
                if (-not $phone) { continue }

                $identityId = $null
                $accountingCode = ([string]$mapping.AccountingCode).Trim().ToUpperInvariant()
                $relatedPhone = Normalize-Phone ([string]$mapping.RelatedPhone)
                $didarCode = ([string]$mapping.DidarContactCode).Trim()

                if ($accountingCode) {
                    foreach ($codeKey in (Get-AccountingCodeKeys $accountingCode)) {
                        if ($accountingCodeToIdentity.ContainsKey($codeKey)) {
                            $identityId = [int64]$accountingCodeToIdentity[$codeKey]
                            break
                        }
                    }
                }

                if (-not $identityId -and $relatedPhone -and $phoneToIdentity.ContainsKey($relatedPhone)) {
                    $identityId = [int64]$phoneToIdentity[$relatedPhone]
                }

                if (-not $identityId -and $didarCode -and $didarCodeToIdentity.ContainsKey($didarCode)) {
                    $identityId = [int64]$didarCodeToIdentity[$didarCode]
                }

                if (-not $identityId -and $phoneToIdentity.ContainsKey($phone)) {
                    $identityId = [int64]$phoneToIdentity[$phone]
                }

                if (-not $identityId) {
                    $identityId = New-Identity -Connection $connection -Transaction $transaction `
                        -DisplayName ([string]$mapping.DisplayName) `
                        -CompanyName $null -OwnerName $null
                }

                $manualIdentitySql = @"
UPDATE dbo.CustomerIdentities
SET DisplayName=COALESCE(NULLIF(@display,N''),DisplayName),
    UpdatedAtUtc=SYSUTCDATETIME()
WHERE IdentityId=@identity;
"@
                Invoke-SqlNonQuery -Connection $connection -Transaction $transaction `
                    -Sql $manualIdentitySql -Parameters @{
                        "@display" = [string]$mapping.DisplayName
                        "@identity" = $identityId
                    } | Out-Null

                Add-Phone -Connection $connection -Transaction $transaction -IdentityId $identityId `
                    -Phone $phone -RawPhone ([string]$mapping.Phone) -PhoneType "MANUAL" `
                    -SourceSystem "MANUAL" -Verified:$true -Priority 0

                $phoneToIdentity[$phone] = $identityId

                $manualMappingSql = @"
IF NOT EXISTS
(
    SELECT 1
    FROM dbo.CustomerIdentityManualMappings
    WHERE Phone=@phone
      AND ISNULL(AccountingCode,N'')=ISNULL(@accounting,N'')
      AND ISNULL(RelatedPhone,N'')=ISNULL(@related,N'')
      AND ISNULL(DidarContactCode,N'')=ISNULL(@didar,N'')
)
INSERT INTO dbo.CustomerIdentityManualMappings
    (DisplayName,Phone,AccountingCode,RelatedPhone,DidarContactCode,IsVerified,IsActive)
VALUES
    (@display,@phone,@accounting,@related,@didar,1,1);
"@
                Invoke-SqlNonQuery -Connection $connection -Transaction $transaction `
                    -Sql $manualMappingSql -Parameters @{
                        "@display" = [string]$mapping.DisplayName
                        "@phone" = $phone
                        "@accounting" = $accountingCode
                        "@related" = $relatedPhone
                        "@didar" = $didarCode
                    } | Out-Null
            }
        }

        $transaction.Commit()
    }
    catch {
        $transaction.Rollback()
        throw
    }
    finally {
        $transaction.Dispose()
    }

    Write-Host "[Identity 6/6] Validating directory..." -ForegroundColor Cyan
    $stats = Invoke-SqlTable -Connection $connection -Sql @"
SELECT
    (SELECT COUNT(*) FROM dbo.CustomerIdentities) AS Identities,
    (SELECT COUNT(*) FROM dbo.CustomerIdentityPhones) AS Phones,
    (SELECT COUNT(*) FROM dbo.CustomerIdentityAccountingLinks) AS AccountingLinks,
    (SELECT COUNT(*) FROM dbo.CustomerIdentityDidarLinks) AS DidarLinks,
    (SELECT COUNT(*) FROM dbo.CustomerIdentityConflicts) AS Conflicts,
    (SELECT COUNT(*) FROM dbo.CustomerPhoneDirectory) AS DirectoryRows;
"@

    $s = $stats.Rows[0]
    Write-Host ("Identity rebuild completed. Identities={0} Phones={1} AccountingLinks={2} DidarLinks={3} Conflicts={4} DirectoryRows={5}" -f `
        $s.Identities,$s.Phones,$s.AccountingLinks,$s.DidarLinks,$s.Conflicts,$s.DirectoryRows) -ForegroundColor Green
}
finally {
    $connection.Close()
    $connection.Dispose()
}
