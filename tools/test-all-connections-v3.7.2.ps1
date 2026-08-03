param(
    [string]$RepositoryRoot = "D:\DigiAhan\CDR3.1.0git",
    [string]$ServerUrl = "http://localhost:5088",
    [switch]$Strict
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$sourceRoot = Join-Path $RepositoryRoot "Source"
$logRoot = Join-Path $RepositoryRoot "Logs"
New-Item -ItemType Directory -Force -Path $logRoot | Out-Null

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$textReport = Join-Path $logRoot "MVP-v3.7.2-$stamp.txt"
$jsonReport = Join-Path $logRoot "MVP-v3.7.2-$stamp.json"
$results = New-Object System.Collections.Generic.List[object]

function Add-Result {
    param(
        [string]$Component,
        [string]$Status,
        [string]$Detail,
        [bool]$Required = $true
    )

    $row = [pscustomobject]@{
        Component = $Component
        Status = $Status
        Required = $Required
        Detail = $Detail
        CheckedAt = (Get-Date).ToString("s")
    }

    $results.Add($row)
    $color = switch ($Status) {
        "PASS" { "Green" }
        "WARN" { "Yellow" }
        "SKIP" { "DarkYellow" }
        default { "Red" }
    }

    Write-Host ("[{0}] {1}: {2}" -f $Status,$Component,$Detail) -ForegroundColor $color
}

function Read-JsonSettings {
    param([string]$SourcePath)

    $settings = [ordered]@{
        DigiAhanCdr = $null
        AccountingLegacyAdo = $null
        VoipApiToken = $null
    }

    Get-ChildItem $SourcePath -Filter "appsettings*.json" -File -ErrorAction SilentlyContinue |
        Sort-Object FullName |
        ForEach-Object {
            try {
                $json = Get-Content $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json

                $connectionStringsProperty = $json.PSObject.Properties["ConnectionStrings"]
                if ($connectionStringsProperty) {
                    $connectionStrings = $connectionStringsProperty.Value

                    $destinationProperty = $connectionStrings.PSObject.Properties["DigiAhanCdr"]
                    if ($destinationProperty -and $destinationProperty.Value) {
                        $settings.DigiAhanCdr = [string]$destinationProperty.Value
                    }

                    $adoProperty = $connectionStrings.PSObject.Properties["AccountingLegacyAdo"]
                    if ($adoProperty -and $adoProperty.Value) {
                        $settings.AccountingLegacyAdo = [string]$adoProperty.Value
                    }
                }

                $voipProperty = $json.PSObject.Properties["Voip"]
                if ($voipProperty) {
                    $tokenProperty = $voipProperty.Value.PSObject.Properties["ApiToken"]
                    if ($tokenProperty -and $tokenProperty.Value) {
                        $settings.VoipApiToken = [string]$tokenProperty.Value
                    }
                }
            }
            catch {
                Add-Result "Config:$($_.Name)" "WARN" $_.Exception.Message $false
            }
        }

    return $settings
}

function Open-SqlConnection {
    param([string]$ConnectionString)
    Add-Type -AssemblyName System.Data
    $connection = New-Object System.Data.SqlClient.SqlConnection $ConnectionString
    $connection.Open()
    return $connection
}

function Invoke-SqlTable {
    param(
        [System.Data.SqlClient.SqlConnection]$Connection,
        [string]$Sql,
        [hashtable]$Parameters = @{}
    )

    $command = $Connection.CreateCommand()
    $command.CommandText = $Sql
    $command.CommandTimeout = 120

    foreach ($key in $Parameters.Keys) {
        $parameter = $command.Parameters.AddWithValue(
            $key,
            $(if ($null -eq $Parameters[$key]) { [DBNull]::Value } else { $Parameters[$key] })
        )
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

$settings = Read-JsonSettings -SourcePath $sourceRoot
$destination = $null

# 1. Direct SQL2000 / accounting test.
if ([string]::IsNullOrWhiteSpace($settings.AccountingLegacyAdo)) {
    Add-Result "Accounting SQL2000" "FAIL" "AccountingLegacyAdo is missing."
}
elseif ($settings.AccountingLegacyAdo -match '(?i)Integrated\s+Security|SSPI|Trusted_Connection') {
    Add-Result "Accounting SQL2000" "FAIL" "ADO string still contains SSPI/Integrated Security."
}
else {
    $ado = $null
    try {
        $ado = New-Object -ComObject ADODB.Connection
        $ado.ConnectionTimeout = 15
        $ado.CommandTimeout = 120
        $ado.Open($settings.AccountingLegacyAdo)

        $rs = $ado.Execute(@"
SELECT
    DB_NAME() AS DbName,
    SYSTEM_USER AS LoginName,
    (SELECT MAX(fdate) FROM factor WHERE typeindex=1) AS LatestFactorDate,
    (SELECT COUNT(*) FROM customer) AS CustomerCount
"@)

        try {
            $db = [string]$rs.Fields.Item("DbName").Value
            $login = [string]$rs.Fields.Item("LoginName").Value
            $latest = [string]$rs.Fields.Item("LatestFactorDate").Value
            $customers = [string]$rs.Fields.Item("CustomerCount").Value

            if ($login -match '(?i)^sa$') {
                Add-Result "Accounting SQL2000" "PASS" "Database=$db Login=$login LatestFactor=$latest Customers=$customers"
            }
            else {
                Add-Result "Accounting SQL2000" "FAIL" "Unexpected login=$login; expected sa."
            }
        }
        finally {
            $rs.Close()
            [void][Runtime.InteropServices.Marshal]::ReleaseComObject($rs)
        }
    }
    catch {
        Add-Result "Accounting SQL2000" "FAIL" $_.Exception.Message
    }
    finally {
        if ($ado) {
            try { $ado.Close() } catch {}
            try { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($ado) } catch {}
        }
    }
}

# 2. Destination SQL and imported data.
if ([string]::IsNullOrWhiteSpace($settings.DigiAhanCdr)) {
    Add-Result "DigiAhan_CDR SQL" "FAIL" "DigiAhanCdr connection string is missing."
}
else {
    try {
        $destination = Open-SqlConnection $settings.DigiAhanCdr
        Add-Result "DigiAhan_CDR SQL" "PASS" "Destination database connected."

        $data = Invoke-SqlTable $destination @"
SELECT
    (SELECT COUNT(*) FROM dbo.AccountingCustomers) AS AccountingCustomers,
    (SELECT COUNT(*) FROM dbo.AccountingInvoices) AS AccountingInvoices,
    (SELECT COUNT(*) FROM dbo.AccountingInvoiceItems) AS AccountingItems,
    (SELECT MAX(FactorDate) FROM dbo.AccountingInvoices) AS LatestImportedFactor,
    (SELECT COUNT(*) FROM dbo.RawCDR) AS CdrRows,
    (SELECT MAX(ReceivedAtUtc) FROM dbo.RawCDR) AS LatestCdrReceived,
    (SELECT COUNT(*) FROM dbo.DidarContacts WHERE ISNULL(IsDeleted,0)=0) AS DidarContacts,
    (SELECT COUNT(*) FROM dbo.DidarContactPhones) AS DidarPhones;
"@

        $row = $data.Rows[0]
        $accountingCount = [int64]$row.AccountingInvoices
        $didarCount = [int64]$row.DidarContacts
        $cdrCount = [int64]$row.CdrRows

        if ($accountingCount -gt 0) {
            Add-Result "Accounting import" "PASS" ("Customers={0} Invoices={1} Items={2} LatestFactor={3}" -f `
                $row.AccountingCustomers,$row.AccountingInvoices,$row.AccountingItems,$row.LatestImportedFactor)
        }
        else {
            Add-Result "Accounting import" "FAIL" "No invoice exists in destination."
        }

        if ($didarCount -gt 0) {
            Add-Result "Didar CRM data" "PASS" ("Contacts={0} Phones={1}" -f $row.DidarContacts,$row.DidarPhones)
        }
        else {
            Add-Result "Didar CRM data" "FAIL" "DidarContacts is empty."
        }

        if ($cdrCount -gt 0) {
            Add-Result "Issabel CDR data" "PASS" ("Rows={0} LatestReceived={1}" -f $row.CdrRows,$row.LatestCdrReceived)
        }
        else {
            Add-Result "Issabel CDR data" "FAIL" "RawCDR is empty."
        }
    }
    catch {
        Add-Result "DigiAhan_CDR SQL" "FAIL" $_.Exception.Message
    }
}

# 3. Customer identity mapping samples.
if ($destination -and $destination.State -eq [System.Data.ConnectionState]::Open) {
    try {
        $directoryExists = Invoke-SqlTable $destination @"
SELECT CASE WHEN OBJECT_ID(N'dbo.CustomerPhoneDirectory',N'V') IS NULL THEN 0 ELSE 1 END AS ExistsFlag;
"@

        if ([int]$directoryExists.Rows[0].ExistsFlag -eq 0) {
            Add-Result "Customer identity" "FAIL" "CustomerPhoneDirectory view does not exist."
        }
        else {
            $samples = Invoke-SqlTable $destination @"
SELECT
    p.Phone,
    d.IdentityId,
    d.DisplayName,
    d.AccountingDetailCode,
    d.DidarContactCode,
    d.MatchSource,
    d.IsVerified
FROM
(
    VALUES
        (N'09121395663'),
        (N'33133470'),
        (N'09127127489'),
        (N'09121235826'),
        (N'33134120'),
        (N'33501752'),
        (N'09123231679'),
        (N'55898562')
) p(Phone)
LEFT JOIN dbo.CustomerPhoneDirectory d
    ON d.NormalizedPhone=dbo.NormalizeIranPhone(p.Phone);
"@

            $karami = $samples.Select("Phone='09121395663'") | Select-Object -First 1
            if ($karami -and $karami.IdentityId -isnot [DBNull] -and
                ([string]$karami.AccountingDetailCode) -match '000010') {
                Add-Result "Mapping: فتح‌الله کرمی" "PASS" ("Identity={0} Code={1}" -f $karami.IdentityId,$karami.AccountingDetailCode)
            }
            else {
                Add-Result "Mapping: فتح‌الله کرمی" "FAIL" "09121395663 is not linked to accounting code 000010."
            }

            $kasiriRows = $samples.Select("Phone IN ('33133470','09127127489','09121235826','33134120','33501752')")
            $kasiriIds = @($kasiriRows | Where-Object { $_.IdentityId -isnot [DBNull] } | ForEach-Object { [string]$_.IdentityId } | Sort-Object -Unique)

            if ($kasiriRows.Count -eq 5 -and $kasiriIds.Count -eq 1) {
                Add-Result "Mapping: محمد کثیری" "PASS" "All five test numbers use Identity=$($kasiriIds[0])."
            }
            else {
                Add-Result "Mapping: محمد کثیری" "FAIL" "The test numbers are not all linked to one identity."
            }

            $yaravRows = $samples.Select("Phone IN ('09123231679','55898562')")
            $yaravIds = @($yaravRows | Where-Object { $_.IdentityId -isnot [DBNull] } | ForEach-Object { [string]$_.IdentityId } | Sort-Object -Unique)

            if ($yaravRows.Count -eq 2 -and $yaravIds.Count -eq 1) {
                Add-Result "Mapping: یاراو مصطفی" "PASS" "Mobile and office number use Identity=$($yaravIds[0])."
            }
            else {
                Add-Result "Mapping: یاراو مصطفی" "FAIL" "09123231679 and 55898562 are not linked."
            }
        }
    }
    catch {
        Add-Result "Customer identity" "FAIL" $_.Exception.Message
    }
}

# 4. Receiver application and APIs.
$appHealthy = $false
try {
    $health = Invoke-RestMethod "$ServerUrl/health" -TimeoutSec 10
    $appHealthy = $health.status -eq "healthy"

    if ($appHealthy) {
        Add-Result "Receiver application" "PASS" ("Version={0} Database={1}" -f $health.version,$health.database)
    }
    else {
        Add-Result "Receiver application" "FAIL" "Health endpoint did not return healthy."
    }
}
catch {
    Add-Result "Receiver application" "FAIL" $_.Exception.Message
}

if ($appHealthy) {
    try {
        $accountingStatus = Invoke-RestMethod "$ServerUrl/api/accounting/status" -TimeoutSec 10
        Add-Result "Accounting status API" "PASS" ("Configured={0} Status={1} Invoices={2}" -f `
            $accountingStatus.configured,$accountingStatus.lastSyncStatus,$accountingStatus.invoiceCount)
    }
    catch {
        Add-Result "Accounting status API" "FAIL" $_.Exception.Message
    }

    try {
        $recent = Invoke-RestMethod "$ServerUrl/api/sales/recent-invoices?take=1" -TimeoutSec 10
        if (@($recent).Count -gt 0) {
            $invoice = @($recent)[0]
            Add-Result "Sales dashboard API" "PASS" ("LatestFactor={0} Customer={1}" -f $invoice.factorDate,$invoice.customerName)
        }
        else {
            Add-Result "Sales dashboard API" "FAIL" "Recent invoice API returned no rows."
        }
    }
    catch {
        Add-Result "Sales dashboard API" "FAIL" $_.Exception.Message
    }

    if ([string]::IsNullOrWhiteSpace($settings.VoipApiToken)) {
        Add-Result "VoIP event API" "SKIP" "Voip:ApiToken was not found." $false
    }
    else {
        try {
            $headers = @{ "X-Voip-Token" = $settings.VoipApiToken }
            $body = @{
                extension = "201"
                callerNumber = "09121395663"
                linkedId = "MVP-372-$stamp"
                channel = "MVP"
                eventTimeUtc = [datetime]::UtcNow
            } | ConvertTo-Json

            $event = Invoke-RestMethod "$ServerUrl/api/voip/events" `
                -Method Post `
                -Headers $headers `
                -ContentType "application/json" `
                -Body $body `
                -TimeoutSec 20

            Start-Sleep -Milliseconds 500
            $current = Invoke-RestMethod "$ServerUrl/api/agent/201/current" -TimeoutSec 10
            $card = $current.card

            if ($card.callerNumber -eq "09121395663" -and
                ([string]$card.accountingCustomerCode) -match '000010') {
                Add-Result "VoIP → Identity → Accounting" "PASS" ("Name={0} Code={1}" -f `
                    $card.customerName,$card.accountingCustomerCode)
            }
            else {
                Add-Result "VoIP → Identity → Accounting" "FAIL" ("Phone={0} Code={1}" -f `
                    $card.callerNumber,$card.accountingCustomerCode)
            }
        }
        catch {
            Add-Result "VoIP event API" "FAIL" $_.Exception.Message
        }
    }
}

if ($destination) {
    try { $destination.Close() } catch {}
    try { $destination.Dispose() } catch {}
}

$requiredFailures = @($results | Where-Object { $_.Required -and $_.Status -eq "FAIL" })
$summary = [pscustomobject]@{
    Version = "3.7.2-MVP"
    Passed = @($results | Where-Object Status -eq "PASS").Count
    Warnings = @($results | Where-Object { $_.Status -in @("WARN","SKIP") }).Count
    Failed = @($results | Where-Object Status -eq "FAIL").Count
    RequiredFailures = $requiredFailures.Count
    ActualIssabelTransport = "MANUAL_TEST_REQUIRED"
    Results = $results
}

$lines = @()
$lines += "DigiAhan MVP Connectivity v3.7.2"
$lines += "Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
$lines += ""
foreach ($item in $results) {
    $lines += "[{0}] {1} | {2}" -f $item.Status,$item.Component,$item.Detail
}
$lines += ""
$lines += "PASS=$($summary.Passed) WARN/SKIP=$($summary.Warnings) FAIL=$($summary.Failed)"
$lines += "Actual Issabel transport still requires: digiahan-test-ring 201 09121395663"

$lines | Set-Content -Path $textReport -Encoding UTF8
$summary | ConvertTo-Json -Depth 8 | Set-Content -Path $jsonReport -Encoding UTF8

Write-Host ""
Write-Host "Report: $textReport" -ForegroundColor Cyan
Write-Host "JSON:   $jsonReport" -ForegroundColor DarkGray
Write-Host ("Summary: PASS={0} WARN/SKIP={1} FAIL={2}" -f `
    $summary.Passed,$summary.Warnings,$summary.Failed) -ForegroundColor White

if ($Strict -and $requiredFailures.Count -gt 0) {
    exit 1
}

exit 0
