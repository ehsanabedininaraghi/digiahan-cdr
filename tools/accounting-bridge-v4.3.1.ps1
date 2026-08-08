param(
    [string]$RepositoryRoot = "D:\DigiAhan\CDR3.1.0git",
    [int]$Days = 45,
    [switch]$FullFiscalYear,
    [switch]$SkipIdentityRebuild
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Get-Configuration {
    param([string]$SourceRoot)

    $result = @{
        DigiAhanCdr = $null
        AccountingLegacy = $null
        AccountingLegacyAdo = $null
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

                $legacyAdoProperty = $connectionStrings.PSObject.Properties["AccountingLegacyAdo"]
                if ($legacyAdoProperty -and $legacyAdoProperty.Value) {
                    $result.AccountingLegacyAdo = [string]$legacyAdoProperty.Value
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
        throw "ConnectionStrings:DigiAhanCdr was not found."
    }

    if ([string]::IsNullOrWhiteSpace($result.AccountingLegacyAdo)) {
        throw "ConnectionStrings:AccountingLegacyAdo was not found. Run RUN-v3.7.4.ps1 first."
    }

    if ($result.AccountingLegacyAdo -match '(?i)Integrated\s+Security|SSPI|Trusted_Connection') {
        throw "AccountingLegacyAdo must use SQL login and must not contain SSPI or Integrated Security."
    }

    return $result
}

function To-PersianDate {
    param([datetime]$Date)

    $calendar = New-Object System.Globalization.PersianCalendar
    return "{0:0000}/{1:00}/{2:00}" -f `
        $calendar.GetYear($Date), `
        $calendar.GetMonth($Date), `
        $calendar.GetDayOfMonth($Date)
}

function Db-Value {
    param($Value)
    if ($null -eq $Value -or $Value -is [DBNull]) { return [DBNull]::Value }
    if ([string]::IsNullOrWhiteSpace([string]$Value)) { return [DBNull]::Value }
    return $Value
}

function Int-Value {
    param($Value)
    if ($null -eq $Value -or $Value -is [DBNull] -or [string]::IsNullOrWhiteSpace([string]$Value)) {
        return [DBNull]::Value
    }
    return [int]$Value
}

function Decimal-Value {
    param($Value)
    if ($null -eq $Value -or $Value -is [DBNull] -or [string]::IsNullOrWhiteSpace([string]$Value)) {
        return [DBNull]::Value
    }
    return [decimal]$Value
}

function Double-Value {
    param($Value)
    if ($null -eq $Value -or $Value -is [DBNull] -or [string]::IsNullOrWhiteSpace([string]$Value)) {
        return [DBNull]::Value
    }
    return [double]$Value
}

function New-DataTable {
    param([string]$Name,[System.Collections.IDictionary]$Columns)

    $table = New-Object System.Data.DataTable $Name
    foreach ($entry in $Columns.GetEnumerator()) {
        [void]$table.Columns.Add($entry.Key,$entry.Value)
    }
    return ,$table
}

function Add-DataRow {
    param([System.Data.DataTable]$Table,[hashtable]$Values)

    $row = $Table.NewRow()
    foreach ($column in $Table.Columns) {
        $name = [string]$column.ColumnName
        $row[$name] = if ($Values.ContainsKey($name)) { Db-Value $Values[$name] } else { [DBNull]::Value }
    }
    [void]$Table.Rows.Add($row)
}

function Open-AdoConnection {
    param([string]$ConnectionString)

    $connection = New-Object -ComObject ADODB.Connection
    $connection.ConnectionTimeout = 15
    $connection.CommandTimeout = 300
    $connection.Open($ConnectionString)
    return $connection
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
    $command.CommandTimeout = 300
    if ($Transaction) { $command.Transaction = $Transaction }

    foreach ($key in $Parameters.Keys) {
        $parameter = $command.Parameters.AddWithValue(
            $key,
            $(if ($null -eq $Parameters[$key]) { [DBNull]::Value } else { $Parameters[$key] })
        )
        [void]$parameter
    }

    try { return $command.ExecuteNonQuery() }
    finally { $command.Dispose() }
}

function Invoke-SqlScript {
    param(
        [System.Data.SqlClient.SqlConnection]$Connection,
        [string]$Path
    )

    if (-not (Test-Path $Path)) {
        throw "SQL schema file not found: $Path"
    }

    $text = Get-Content $Path -Raw -Encoding UTF8
    $batches = [regex]::Split($text, '(?im)^\s*GO\s*;?\s*$')

    foreach ($batch in $batches) {
        if (-not [string]::IsNullOrWhiteSpace($batch)) {
            Invoke-SqlNonQuery -Connection $Connection -Sql $batch | Out-Null
        }
    }
}

function Write-Bulk {
    param(
        [System.Data.SqlClient.SqlConnection]$Connection,
        [System.Data.SqlClient.SqlTransaction]$Transaction,
        [System.Data.DataTable]$Table,
        [string]$Destination
    )

    if ($Table.Rows.Count -eq 0) { return }

    $bulk = [System.Data.SqlClient.SqlBulkCopy]::new(
        $Connection,
        [System.Data.SqlClient.SqlBulkCopyOptions]::CheckConstraints,
        $Transaction
    )

    try {
        $bulk.DestinationTableName = $Destination
        $bulk.BatchSize = 1000
        $bulk.BulkCopyTimeout = 600

        foreach ($column in $Table.Columns) {
            [void]$bulk.ColumnMappings.Add($column.ColumnName,$column.ColumnName)
        }

        $bulk.WriteToServer($Table)
    }
    finally {
        $bulk.Close()
        $bulk.Dispose()
    }
}

function Read-Visitors {
    param($Ado,[string]$Database,[int]$FiscalYear,[datetime]$ImportedAt)

    $table = New-DataTable "Visitors" ([ordered]@{
        SourceDatabase=[string];FiscalYear=[int];VisitorId=[int];VisitorName=[string];
        RoleType=[string];IsActive=[bool];ImportedAtUtc=[datetime]
    })

    $rs = $Ado.Execute("SELECT visitorid,visitorname FROM visitor ORDER BY visitorid")
    try {
        while (-not $rs.EOF) {
            $id = [int]$rs.Fields.Item("visitorid").Value
            Add-DataRow $table @{
                SourceDatabase=$Database;FiscalYear=$FiscalYear;VisitorId=$id;
                VisitorName=[string]$rs.Fields.Item("visitorname").Value;
                RoleType=$(if ($id -eq 6) {"SHARED"} elseif ($id -eq 7) {"COLLECTIONS"} else {"SALES"});
                IsActive=($id -ne 8);ImportedAtUtc=$ImportedAt
            }
            $rs.MoveNext()
        }
    }
    finally {
        $rs.Close()
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($rs)
    }

    return ,$table
}

function Read-Customers {
    param($Ado,[string]$Database,[int]$FiscalYear,[datetime]$ImportedAt)

    $table = New-DataTable "Customers" ([ordered]@{
        SourceDatabase=[string];FiscalYear=[int];DetailCode=[string];ShortCode=[string];
        CustomerName=[string];ManagerName=[string];EconomicCode=[string];CustomerTel=[string];
        CustomerAddress=[string];ImportedAtUtc=[datetime]
    })

    $sql = @"
SELECT
    detailcode,shortcode,customername,managername,
    economiccode,customertel,customeraddress
FROM customer
ORDER BY detailcode
"@

    $rs = $Ado.Execute($sql)
    try {
        while (-not $rs.EOF) {
            $detail = [string]$rs.Fields.Item("detailcode").Value
            if (-not [string]::IsNullOrWhiteSpace($detail)) {
                Add-DataRow $table @{
                    SourceDatabase=$Database;FiscalYear=$FiscalYear;DetailCode=$detail;
                    ShortCode=[string]$rs.Fields.Item("shortcode").Value;
                    CustomerName=[string]$rs.Fields.Item("customername").Value;
                    ManagerName=[string]$rs.Fields.Item("managername").Value;
                    EconomicCode=[string]$rs.Fields.Item("economiccode").Value;
                    CustomerTel=[string]$rs.Fields.Item("customertel").Value;
                    CustomerAddress=[string]$rs.Fields.Item("customeraddress").Value;
                    ImportedAtUtc=$ImportedAt
                }
            }
            $rs.MoveNext()
        }
    }
    finally {
        $rs.Close()
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($rs)
    }

    return ,$table
}

function Read-Invoices {
    param($Ado,[string]$Database,[int]$FiscalYear,[string]$Cutoff,[datetime]$ImportedAt)

    if ($Cutoff -notmatch '^\d{4}/\d{2}/\d{2}$') { throw "Invalid cutoff date." }

    $table = New-DataTable "Invoices" ([ordered]@{
        SourceDatabase=[string];FiscalYear=[int];FactorCode=[int];DocumentNumber=[decimal];
        FactorNumber=[decimal];FactorDate=[string];TypeIndex=[int];TypeDescription=[string];
        FactorDescription=[string];
        CustomerShortCode=[string];CustomerDetailCode=[string];CustomerName=[string];
        Amount=[decimal];VisitorId=[int];VisitorName=[string];ImportedAtUtc=[datetime]
    })

    $sql = @"
SELECT
    f.Code AS FactorCode,
    f.dnumber,
    f.fnumber,
    f.fdate,
    f.typeindex,
    f.type,
    f.description AS FactorDescription,
    f.customercode,
    c.detailcode,
    f.customername,
    f.amount,
    f.visitorid,
    v.visitorname
FROM factor f
LEFT JOIN visitor v ON v.visitorid=f.visitorid
LEFT JOIN customer c ON c.shortcode=f.customercode
WHERE f.typeindex=1
  AND f.fdate>='$Cutoff'
ORDER BY f.Code
"@

    $rs = $Ado.Execute($sql)
    try {
        while (-not $rs.EOF) {
            Add-DataRow $table @{
                SourceDatabase=$Database;FiscalYear=$FiscalYear;
                FactorCode=[int]$rs.Fields.Item("FactorCode").Value;
                DocumentNumber=Decimal-Value $rs.Fields.Item("dnumber").Value;
                FactorNumber=Decimal-Value $rs.Fields.Item("fnumber").Value;
                FactorDate=[string]$rs.Fields.Item("fdate").Value;
                TypeIndex=Int-Value $rs.Fields.Item("typeindex").Value;
                TypeDescription=[string]$rs.Fields.Item("type").Value;
                FactorDescription=[string]$rs.Fields.Item("FactorDescription").Value;
                CustomerShortCode=[string]$rs.Fields.Item("customercode").Value;
                CustomerDetailCode=[string]$rs.Fields.Item("detailcode").Value;
                CustomerName=[string]$rs.Fields.Item("customername").Value;
                Amount=Decimal-Value $rs.Fields.Item("amount").Value;
                VisitorId=Int-Value $rs.Fields.Item("visitorid").Value;
                VisitorName=[string]$rs.Fields.Item("visitorname").Value;
                ImportedAtUtc=$ImportedAt
            }
            $rs.MoveNext()
        }
    }
    finally {
        $rs.Close()
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($rs)
    }

    return ,$table
}

function Read-Items {
    param($Ado,[string]$Database,[int]$FiscalYear,[string]$Cutoff,[datetime]$ImportedAt)

    if ($Cutoff -notmatch '^\d{4}/\d{2}/\d{2}$') { throw "Invalid cutoff date." }

    $table = New-DataTable "Items" ([ordered]@{
        SourceDatabase=[string];FiscalYear=[int];ItemCode=[int];FactorCode=[int];
        FactorDate=[string];ItemRow=[int];ProductCode=[string];ProductName=[string];
        Description=[string];Quantity=[double];UnitPrice=[decimal];TotalPrice=[decimal];
        ImportedAtUtc=[datetime]
    })

    $sql = @"
SELECT
    i.Code AS ItemCode,
    f.Code AS FactorCode,
    f.fdate,
    i.row AS ItemRow,
    i.scode,
    i.name,
    i.des,
    i.count1,
    i.facprice,
    i.factprice
FROM factor f
INNER JOIN facitem i
    ON i.docno=f.dnumber
   AND i.facno=f.fnumber
WHERE f.typeindex=1
  AND f.fdate>='$Cutoff'
ORDER BY i.Code
"@

    $rs = $Ado.Execute($sql)
    try {
        while (-not $rs.EOF) {
            Add-DataRow $table @{
                SourceDatabase=$Database;FiscalYear=$FiscalYear;
                ItemCode=[int]$rs.Fields.Item("ItemCode").Value;
                FactorCode=[int]$rs.Fields.Item("FactorCode").Value;
                FactorDate=[string]$rs.Fields.Item("fdate").Value;
                ItemRow=Int-Value $rs.Fields.Item("ItemRow").Value;
                ProductCode=[string]$rs.Fields.Item("scode").Value;
                ProductName=[string]$rs.Fields.Item("name").Value;
                Description=[string]$rs.Fields.Item("des").Value;
                Quantity=Double-Value $rs.Fields.Item("count1").Value;
                UnitPrice=Decimal-Value $rs.Fields.Item("facprice").Value;
                TotalPrice=Decimal-Value $rs.Fields.Item("factprice").Value;
                ImportedAtUtc=$ImportedAt
            }
            $rs.MoveNext()
        }
    }
    finally {
        $rs.Close()
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($rs)
    }

    return ,$table
}

$sourceRoot = Join-Path $RepositoryRoot "Source"
$config = Get-Configuration -SourceRoot $sourceRoot
$sourceAdoString = $config.AccountingLegacyAdo

$Days = [Math]::Max(1,[Math]::Min(365,$Days))
$cutoff = if ($FullFiscalYear) {
    "{0:0000}/01/01" -f $config.FiscalYear
}
else {
    To-PersianDate (Get-Date).Date.AddDays(-($Days - 1))
}

$runId = [guid]::NewGuid()
$started = [datetime]::UtcNow
$importedAt = [datetime]::UtcNow
$ado = $null
$sqlConnection = $null

try {
    Write-Host "Accounting Bridge v4.3.1 (invoice notification hotfix)" -ForegroundColor Cyan
    Write-Host "Source: $($config.AccountingServer)/$($config.AccountingDatabase)" -ForegroundColor DarkGray
    Write-Host "Fiscal year: $($config.FiscalYear) | Cutoff: $cutoff" -ForegroundColor DarkGray

    $maskedAdo = [regex]::Replace($sourceAdoString, '(?i)(Password|PWD)=[^;]*', '$1=***')
    Write-Host "ADO connection: $maskedAdo" -ForegroundColor DarkGray

    $ado = Open-AdoConnection -ConnectionString $sourceAdoString

    $probe = $ado.Execute("SELECT DB_NAME() AS DbName, SYSTEM_USER AS LoginName")
    try {
        $actualDb = [string]$probe.Fields.Item("DbName").Value
        $actualLogin = [string]$probe.Fields.Item("LoginName").Value
    }
    finally {
        $probe.Close()
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($probe)
    }

    if ($actualLogin -notmatch '(?i)^sa$') {
        throw "Unexpected accounting login: $actualLogin. Expected sa."
    }

    if ($actualDb -ne $config.AccountingDatabase) {
        throw "Unexpected accounting database: $actualDb. Expected $($config.AccountingDatabase)."
    }

    Write-Host "[1/6] Legacy SQL connected. Login=$actualLogin Database=$actualDb" -ForegroundColor Green

    Write-Host "[2/6] Reading all customers and visitors..." -ForegroundColor Cyan
    $visitors = Read-Visitors $ado $config.AccountingDatabase $config.FiscalYear $importedAt
    $customers = Read-Customers $ado $config.AccountingDatabase $config.FiscalYear $importedAt

    Write-Host "[3/6] Reading invoices and items..." -ForegroundColor Cyan
    $invoices = Read-Invoices $ado $config.AccountingDatabase $config.FiscalYear $cutoff $importedAt
    $items = Read-Items $ado $config.AccountingDatabase $config.FiscalYear $cutoff $importedAt

    Write-Host ("Read: Customers={0}, Visitors={1}, Invoices={2}, Items={3}" -f `
        $customers.Rows.Count,$visitors.Rows.Count,$invoices.Rows.Count,$items.Rows.Count) -ForegroundColor Green

    $sqlConnection = Open-SqlConnection -ConnectionString $config.DigiAhanCdr

    $accountingSchema = Join-Path $sourceRoot "Sql\AccountingSchema.sql"
    Invoke-SqlScript -Connection $sqlConnection -Path $accountingSchema

    $compatibilitySql = @"
IF COL_LENGTH(N'dbo.AccountingSyncRuns',N'CutoffPersianDate') IS NULL
BEGIN
    ALTER TABLE dbo.AccountingSyncRuns
        ADD CutoffPersianDate nvarchar(10) NULL;

    IF COL_LENGTH(N'dbo.AccountingSyncRuns',N'CutoffDate') IS NOT NULL
        EXEC(N'UPDATE dbo.AccountingSyncRuns
               SET CutoffPersianDate=CutoffDate
               WHERE CutoffPersianDate IS NULL;');
END;
"@
    Invoke-SqlNonQuery -Connection $sqlConnection -Sql $compatibilitySql | Out-Null

    $startRunSql = @"
INSERT INTO dbo.AccountingSyncRuns
    (RunId,StartedAtUtc,SourceServer,SourceDatabase,FiscalYear,CutoffPersianDate,Status,
     VisitorCount,CustomerCount,InvoiceCount,InvoiceItemCount)
VALUES
    (@run,@started,@server,@db,@fy,@cutoff,N'RUNNING',0,0,0,0);
"@
    Invoke-SqlNonQuery -Connection $sqlConnection -Sql $startRunSql -Parameters @{
        "@run"=$runId;"@started"=$started;"@server"=$config.AccountingServer;
        "@db"=$config.AccountingDatabase;"@fy"=$config.FiscalYear;"@cutoff"=$cutoff
    } | Out-Null

    Write-Host "[4/6] Replacing destination snapshot..." -ForegroundColor Cyan

    # SqlBulkCopy cannot reliably see a local #temp table on every SqlClient /
    # PowerShell combination. Use unique dbo staging tables inside one
    # transaction, then drop them before commit.
    $stageSuffix = $runId.ToString("N")
    $visitorStage = "dbo.AccountingVisitorsStage_$stageSuffix"
    $customerStage = "dbo.AccountingCustomersStage_$stageSuffix"

    $transaction = $sqlConnection.BeginTransaction()
    try {
        $replaceSql = @"
DELETE FROM dbo.AccountingInvoiceItems
WHERE SourceDatabase=@db AND FiscalYear=@fy AND FactorDate>=@cutoff;

DELETE FROM dbo.AccountingInvoices
WHERE SourceDatabase=@db AND FiscalYear=@fy AND FactorDate>=@cutoff;

SELECT TOP(0)
    SourceDatabase,FiscalYear,VisitorId,VisitorName,RoleType,IsActive,ImportedAtUtc
INTO $visitorStage
FROM dbo.AccountingVisitors;

SELECT TOP(0)
    SourceDatabase,FiscalYear,DetailCode,ShortCode,CustomerName,ManagerName,
    EconomicCode,CustomerTel,CustomerAddress,ImportedAtUtc
INTO $customerStage
FROM dbo.AccountingCustomers;
"@
        Invoke-SqlNonQuery -Connection $sqlConnection -Sql $replaceSql -Parameters @{
            "@db"=$config.AccountingDatabase
            "@fy"=$config.FiscalYear
            "@cutoff"=$cutoff
        } -Transaction $transaction | Out-Null

        Write-Bulk -Connection $sqlConnection -Transaction $transaction `
            -Table $visitors -Destination $visitorStage

        Write-Bulk -Connection $sqlConnection -Transaction $transaction `
            -Table $customers -Destination $customerStage

        $mergeSql = @"
MERGE dbo.AccountingVisitors AS target
USING $visitorStage AS source
ON target.SourceDatabase=source.SourceDatabase
AND target.FiscalYear=source.FiscalYear
AND target.VisitorId=source.VisitorId
WHEN MATCHED THEN UPDATE SET
    VisitorName=source.VisitorName,
    RoleType=source.RoleType,
    IsActive=source.IsActive,
    ImportedAtUtc=source.ImportedAtUtc
WHEN NOT MATCHED BY TARGET THEN
    INSERT(SourceDatabase,FiscalYear,VisitorId,VisitorName,RoleType,IsActive,ImportedAtUtc)
    VALUES(source.SourceDatabase,source.FiscalYear,source.VisitorId,source.VisitorName,
           source.RoleType,source.IsActive,source.ImportedAtUtc);

MERGE dbo.AccountingCustomers AS target
USING $customerStage AS source
ON target.SourceDatabase=source.SourceDatabase
AND target.FiscalYear=source.FiscalYear
AND target.DetailCode=source.DetailCode
WHEN MATCHED THEN UPDATE SET
    ShortCode=source.ShortCode,
    CustomerName=source.CustomerName,
    ManagerName=source.ManagerName,
    EconomicCode=source.EconomicCode,
    CustomerTel=source.CustomerTel,
    CustomerAddress=source.CustomerAddress,
    ImportedAtUtc=source.ImportedAtUtc
WHEN NOT MATCHED BY TARGET THEN
    INSERT(SourceDatabase,FiscalYear,DetailCode,ShortCode,CustomerName,ManagerName,
           EconomicCode,CustomerTel,CustomerAddress,ImportedAtUtc)
    VALUES(source.SourceDatabase,source.FiscalYear,source.DetailCode,source.ShortCode,
           source.CustomerName,source.ManagerName,source.EconomicCode,source.CustomerTel,
           source.CustomerAddress,source.ImportedAtUtc);

DROP TABLE $visitorStage;
DROP TABLE $customerStage;
"@
        Invoke-SqlNonQuery -Connection $sqlConnection -Sql $mergeSql `
            -Transaction $transaction | Out-Null

        Write-Bulk -Connection $sqlConnection -Transaction $transaction `
            -Table $invoices -Destination "dbo.AccountingInvoices"

        Write-Bulk -Connection $sqlConnection -Transaction $transaction `
            -Table $items -Destination "dbo.AccountingInvoiceItems"

        $transaction.Commit()
    }
    catch {
        try { $transaction.Rollback() } catch {}
        throw
    }
    finally {
        $transaction.Dispose()
    }

    $finished = [datetime]::UtcNow

    $finishRunSql = @"
UPDATE dbo.AccountingSyncRuns
SET FinishedAtUtc=@finished,
    Status=N'SUCCESS',
    VisitorCount=@visitors,
    CustomerCount=@customers,
    InvoiceCount=@invoices,
    InvoiceItemCount=@items,
    ErrorMessage=NULL
WHERE RunId=@run;
"@
    Invoke-SqlNonQuery -Connection $sqlConnection -Sql $finishRunSql -Parameters @{
        "@finished"=$finished;"@visitors"=$visitors.Rows.Count;"@customers"=$customers.Rows.Count;
        "@invoices"=$invoices.Rows.Count;"@items"=$items.Rows.Count;"@run"=$runId
    } | Out-Null

    Write-Host "[5/6] Accounting synchronization completed successfully against the real schema and persistent staging tables." -ForegroundColor Green

    if (-not $SkipIdentityRebuild) {
        Write-Host "[6/6] Rebuilding customer identity directory..." -ForegroundColor Cyan
        $identityScript = Join-Path $RepositoryRoot "tools\rebuild-customer-identity.ps1"
        if (-not (Test-Path $identityScript)) {
            throw "Identity rebuild script not found: $identityScript"
        }

        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $identityScript `
            -RepositoryRoot $RepositoryRoot `
            -MappingsFile (Join-Path $RepositoryRoot "config\manual-customer-mappings.csv")

        if ($LASTEXITCODE -ne 0) {
            throw "Accounting sync succeeded, but identity rebuild failed."
        }
    }

    $latestFactorDate = "none"
    if ($invoices.Rows.Count -gt 0) {
        $latestFactorDate = $invoices.Rows |
            ForEach-Object { [string]$_.FactorDate } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Sort-Object -Descending |
            Select-Object -First 1
    }

    Write-Host "SUCCESS | Latest imported factor date: $latestFactorDate" -ForegroundColor Green
}
catch {
    $errorMessage = $_.Exception.ToString()
    Write-Host $errorMessage -ForegroundColor Red

    try {
        if ($sqlConnection -and $sqlConnection.State -eq [System.Data.ConnectionState]::Open) {
            $failRunSql = @"
IF EXISTS(SELECT 1 FROM dbo.AccountingSyncRuns WHERE RunId=@run)
UPDATE dbo.AccountingSyncRuns
SET FinishedAtUtc=SYSUTCDATETIME(),Status=N'FAILED',ErrorMessage=@error
WHERE RunId=@run;
"@
            Invoke-SqlNonQuery -Connection $sqlConnection -Sql $failRunSql `
                -Parameters @{"@run"=$runId;"@error"=$errorMessage} | Out-Null
        }
    }
    catch {
        Write-Warning "Could not record failed sync: $($_.Exception.Message)"
    }

    exit 1
}
finally {
    if ($ado) {
        try { $ado.Close() } catch {}
        try { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($ado) } catch {}
    }

    if ($sqlConnection) {
        try { $sqlConnection.Close() } catch {}
        try { $sqlConnection.Dispose() } catch {}
    }
}
