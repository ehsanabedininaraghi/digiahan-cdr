param(
    [string]$RepositoryRoot = "D:\DigiAhan\CDR3.1.0git"
)

$ErrorActionPreference = "Stop"
$sourceRoot = Join-Path $RepositoryRoot "Source"
$destination = $null

Get-ChildItem $sourceRoot -Filter "appsettings*.json" -File -ErrorAction SilentlyContinue |
    Sort-Object FullName |
    ForEach-Object {
        try {
            $json = Get-Content $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($json.ConnectionStrings.DigiAhanCdr) {
                $destination = [string]$json.ConnectionStrings.DigiAhanCdr
            }
        }
        catch {}
    }

if ([string]::IsNullOrWhiteSpace($destination)) {
    throw "ConnectionStrings:DigiAhanCdr was not found."
}

Add-Type -AssemblyName System.Data
$connection = New-Object System.Data.SqlClient.SqlConnection $destination
$connection.Open()

try {
    $command = $connection.CreateCommand()
    $command.CommandTimeout = 120
    $command.CommandText = @"
SELECT
    p.Phone,
    d.DisplayName,
    d.CompanyName,
    d.AccountingDetailCode,
    d.AccountingShortCode,
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
    ON d.NormalizedPhone=dbo.NormalizeIranPhone(p.Phone)
ORDER BY p.Phone;

SELECT TOP(1)
    FactorDate,FactorCode,CustomerName,Amount
FROM dbo.AccountingInvoices
ORDER BY FactorDate DESC,FactorCode DESC;

SELECT TOP(3)
    StartedAtUtc,FinishedAtUtc,Status,CustomerCount,InvoiceCount,InvoiceItemCount,ErrorMessage
FROM dbo.AccountingSyncRuns
ORDER BY StartedAtUtc DESC;
"@

    $reader = $command.ExecuteReader()

    Write-Host "`nSample identity verification:" -ForegroundColor Cyan
    $rows = @()
    while ($reader.Read()) {
        $rows += [pscustomobject]@{
            Phone = [string]$reader["Phone"]
            Name = if ($reader["DisplayName"] -is [DBNull]) { "" } else { [string]$reader["DisplayName"] }
            AccountingCode = if ($reader["AccountingDetailCode"] -is [DBNull]) { "" } else { [string]$reader["AccountingDetailCode"] }
            DidarCode = if ($reader["DidarContactCode"] -is [DBNull]) { "" } else { [string]$reader["DidarContactCode"] }
            Source = if ($reader["MatchSource"] -is [DBNull]) { "" } else { [string]$reader["MatchSource"] }
            Verified = if ($reader["IsVerified"] -is [DBNull]) { $false } else { [bool]$reader["IsVerified"] }
        }
    }
    $rows | Format-Table -AutoSize

    [void]$reader.NextResult()
    Write-Host "`nLatest imported invoice:" -ForegroundColor Cyan
    if ($reader.Read()) {
        [pscustomobject]@{
            FactorDate=[string]$reader["FactorDate"]
            FactorCode=[string]$reader["FactorCode"]
            CustomerName=[string]$reader["CustomerName"]
            Amount=[string]$reader["Amount"]
        } | Format-List
    }
    else {
        Write-Warning "No accounting invoice exists."
    }

    [void]$reader.NextResult()
    Write-Host "`nLatest accounting sync runs:" -ForegroundColor Cyan
    $syncRows = @()
    while ($reader.Read()) {
        $syncRows += [pscustomobject]@{
            StartedAtUtc=[string]$reader["StartedAtUtc"]
            FinishedAtUtc=if($reader["FinishedAtUtc"] -is [DBNull]){""}else{[string]$reader["FinishedAtUtc"]}
            Status=[string]$reader["Status"]
            Customers=[string]$reader["CustomerCount"]
            Invoices=[string]$reader["InvoiceCount"]
            Items=[string]$reader["InvoiceItemCount"]
            Error=if($reader["ErrorMessage"] -is [DBNull]){""}else{[string]$reader["ErrorMessage"]}
        }
    }
    $syncRows | Format-Table -AutoSize

    $reader.Close()
    $command.Dispose()
}
finally {
    $connection.Close()
    $connection.Dispose()
}
