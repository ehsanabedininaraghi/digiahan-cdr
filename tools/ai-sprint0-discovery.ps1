[CmdletBinding()]
param(
    [string]$SourceRoot = "D:\DigiAhan\CDR4.0\Source",
    [string]$OutputRoot,
    [string]$ConnectionString,
    [int]$CommandTimeoutSeconds = 30,
    [int]$WorkingHourStart = 8,
    [int]$WorkingHourEnd = 18
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $PSScriptRoot "..\docs\ai\sprint0\output"
}

function Write-JsonFile {
    param([Parameter(Mandatory)]$Value, [Parameter(Mandatory)][string]$Path)
    $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Convert-TableRows {
    param([Parameter(Mandatory)][System.Data.DataTable]$Table)

    $rows = @()
    foreach ($row in $Table.Rows) {
        $item = [ordered]@{}
        foreach ($column in $Table.Columns) {
            $value = $row[$column.ColumnName]
            $item[$column.ColumnName] = if ($value -is [DBNull]) { $null } else { $value }
        }
        $rows += [pscustomobject]$item
    }
    return $rows
}

function Invoke-ReadOnlyQuery {
    param(
        [Parameter(Mandatory)][System.Data.SqlClient.SqlConnection]$Connection,
        [Parameter(Mandatory)][string]$Sql,
        [hashtable]$Parameters = @{}
    )

    $command = $Connection.CreateCommand()
    $command.CommandTimeout = $CommandTimeoutSeconds
    $command.CommandText = @"
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SET LOCK_TIMEOUT 5000;
$Sql
"@

    foreach ($entry in $Parameters.GetEnumerator()) {
        [void]$command.Parameters.AddWithValue($entry.Key, $entry.Value)
    }

    $adapter = New-Object System.Data.SqlClient.SqlDataAdapter $command
    $dataSet = New-Object System.Data.DataSet
    [void]$adapter.Fill($dataSet)
    $command.Dispose()
    return $dataSet
}

function Get-ConfiguredConnectionString {
    param([Parameter(Mandatory)][string]$Root)

    $settingsPath = Join-Path $Root "appsettings.json"
    if (-not (Test-Path -LiteralPath $settingsPath)) {
        throw "appsettings.json was not found under SourceRoot."
    }

    $settings = Get-Content -Raw -LiteralPath $settingsPath | ConvertFrom-Json
    $configured = [string]$settings.ConnectionStrings.DigiAhanCdr
    if ([string]::IsNullOrWhiteSpace($configured)) {
        throw "ConnectionStrings:DigiAhanCdr is missing."
    }
    return $configured
}

function Get-SafeConnectionMetadata {
    param([Parameter(Mandatory)][System.Data.SqlClient.SqlConnectionStringBuilder]$Builder)

    [pscustomobject]@{
        data_source = [string]$Builder["Data Source"]
        database = [string]$Builder["Initial Catalog"]
        integrated_security = [bool]$Builder["Integrated Security"]
        application_name = [string]$Builder["Application Name"]
        contains_password = $Builder.ContainsKey("Password") -and -not [string]::IsNullOrEmpty([string]$Builder["Password"])
    }
}

function Test-CommandAvailable {
    param([Parameter(Mandatory)][string]$Name)
    return $null -ne (Get-Command $Name -ErrorAction SilentlyContinue)
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
    $ConnectionString = Get-ConfiguredConnectionString -Root $SourceRoot
}

$builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($ConnectionString)
if ([string]$builder["Initial Catalog"] -ne "DigiAhan_CDR") {
    throw "Refusing to inspect unexpected database '$([string]$builder["Initial Catalog"])'."
}

# Shared memory avoids machine/domain SPN problems and guarantees a local SQL connection.
if ([string]$builder["Data Source"] -in @("localhost", ".", "(local)")) {
    $builder["Data Source"] = "lpc:localhost"
}
$builder["Application Name"] = "DigiAhan-AI-Sprint0-ReadOnly"
$builder["Connect Timeout"] = 5
$builder["Enlist"] = $false

$connection = [System.Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
$startedAt = [DateTime]::UtcNow

try {
    $connection.Open()

    $schemaData = Invoke-ReadOnlyQuery -Connection $connection -Sql @'
SELECT
    c.column_id,
    c.name AS ColumnName,
    ty.name AS DataType,
    c.max_length AS MaxLength,
    c.precision AS NumericPrecision,
    c.scale AS NumericScale,
    c.is_nullable AS IsNullable
FROM sys.columns c
INNER JOIN sys.types ty ON c.user_type_id=ty.user_type_id
WHERE c.object_id=OBJECT_ID(N'dbo.RawCDR')
ORDER BY c.column_id;

SELECT
    i.name AS IndexName,
    i.is_primary_key AS IsPrimaryKey,
    i.is_unique AS IsUnique,
    STRING_AGG(CAST(col.name AS nvarchar(max)),N',')
        WITHIN GROUP (ORDER BY ic.key_ordinal) AS KeyColumns
FROM sys.indexes i
INNER JOIN sys.index_columns ic
    ON i.object_id=ic.object_id AND i.index_id=ic.index_id
INNER JOIN sys.columns col
    ON ic.object_id=col.object_id AND ic.column_id=col.column_id
WHERE i.object_id=OBJECT_ID(N'dbo.RawCDR')
  AND ic.is_included_column=0
GROUP BY i.name,i.is_primary_key,i.is_unique
ORDER BY i.is_primary_key DESC,i.name;

SELECT
    s.name AS SchemaName,
    t.name AS TableName,
    SUM(p.rows) AS ApproximateRows
FROM sys.tables t
INNER JOIN sys.schemas s ON t.schema_id=s.schema_id
LEFT JOIN sys.partitions p ON p.object_id=t.object_id AND p.index_id IN(0,1)
WHERE t.name=N'RawCDR'
   OR t.name LIKE N'%Customer%'
   OR t.name LIKE N'%Didar%'
   OR t.name LIKE N'%Accounting%'
GROUP BY s.name,t.name
ORDER BY t.name;

SELECT
    s.name AS SchemaName,
    t.name AS TableName,
    c.column_id,
    c.name AS ColumnName,
    ty.name AS DataType,
    c.is_nullable AS IsNullable
FROM sys.tables t
INNER JOIN sys.schemas s ON t.schema_id=s.schema_id
INNER JOIN sys.columns c ON c.object_id=t.object_id
INNER JOIN sys.types ty ON c.user_type_id=ty.user_type_id
WHERE t.name IN
(
    N'CustomerIdentities',N'CustomerIdentityPhones',N'CustomerIdentityAccountingLinks',
    N'CustomerIdentityDidarLinks',N'DidarContacts',N'DidarContactPhones',
    N'AccountingCustomers',N'AccountingInvoices',N'AccountingInvoiceItems'
)
ORDER BY t.name,c.column_id;
'@

    $baselineData = Invoke-ReadOnlyQuery -Connection $connection -Sql @'
WITH R AS
(
    SELECT
        RawCDRId,Calldate,ReceivedAtUtc,Duration,Billsec,Disposition,
        Src,Dst,Did,Dcontext,RecordingFile,LinkedId,UniqueId,
        CallKey=CASE
            WHEN NULLIF(LTRIM(RTRIM(LinkedId)),N'') IS NOT NULL THEN N'linked:'+LinkedId
            WHEN NULLIF(LTRIM(RTRIM(UniqueId)),N'') IS NOT NULL THEN N'unique:'+UniqueId
            ELSE N'raw:'+CONVERT(nvarchar(30),RawCDRId)
        END,
        Direction=CASE
            WHEN NULLIF(Did,N'') IS NOT NULL OR Dcontext LIKE N'%from-trunk%' THEN N'inbound'
            WHEN Dcontext LIKE N'%from-internal%' OR Dcontext LIKE N'%outbound%' THEN N'outbound'
            ELSE N'unknown'
        END
    FROM dbo.RawCDR
),
C AS
(
    SELECT
        CallKey,
        StartedAt=MIN(Calldate),
        CompletedAt=MAX(DATEADD(second,ISNULL(Duration,0),Calldate)),
        DurationSeconds=MAX(ISNULL(Duration,0)),
        Billsec=MAX(ISNULL(Billsec,0)),
        LegCount=COUNT_BIG(*),
        HasLinkedId=MAX(CASE WHEN NULLIF(LTRIM(RTRIM(LinkedId)),N'') IS NULL THEN 0 ELSE 1 END),
        HasUniqueId=MAX(CASE WHEN NULLIF(LTRIM(RTRIM(UniqueId)),N'') IS NULL THEN 0 ELSE 1 END),
        HasRecordingReference=MAX(CASE WHEN NULLIF(LTRIM(RTRIM(RecordingFile)),N'') IS NULL THEN 0 ELSE 1 END),
        RecordingReferenceCount=COUNT(DISTINCT NULLIF(LTRIM(RTRIM(RecordingFile)),N'')),
        Answered=MAX(CASE WHEN Disposition=N'ANSWERED' OR ISNULL(Billsec,0)>0 THEN 1 ELSE 0 END),
        IsInbound=MAX(CASE WHEN Direction=N'inbound' THEN 1 ELSE 0 END),
        IsOutbound=MAX(CASE WHEN Direction=N'outbound' THEN 1 ELSE 0 END),
        FirstReceivedAtUtc=MIN(ReceivedAtUtc),
        LastReceivedAtUtc=MAX(ReceivedAtUtc)
    FROM R
    GROUP BY CallKey
)
SELECT
    RawRows=(SELECT COUNT_BIG(*) FROM R),
    EligibleCalls=COUNT_BIG(*),
    AnsweredCalls=SUM(CONVERT(bigint,Answered)),
    InboundCalls=SUM(CONVERT(bigint,IsInbound)),
    OutboundCalls=SUM(CONVERT(bigint,IsOutbound)),
    UnknownDirectionCalls=SUM(CONVERT(bigint,CASE WHEN IsInbound=0 AND IsOutbound=0 THEN 1 ELSE 0 END)),
    CallsWithLinkedId=SUM(CONVERT(bigint,HasLinkedId)),
    CallsWithUniqueId=SUM(CONVERT(bigint,HasUniqueId)),
    CallsWithRecordingReference=SUM(CONVERT(bigint,HasRecordingReference)),
    CallsWithMultipleRecordingReferences=SUM(CONVERT(bigint,CASE WHEN RecordingReferenceCount>1 THEN 1 ELSE 0 END)),
    MultiLegCalls=SUM(CONVERT(bigint,CASE WHEN LegCount>1 THEN 1 ELSE 0 END)),
    CallsWithLegArrivalSpreadOver90s=SUM(CONVERT(bigint,CASE WHEN DATEDIFF(second,FirstReceivedAtUtc,LastReceivedAtUtc)>90 THEN 1 ELSE 0 END)),
    MinCallDate=MIN(StartedAt),
    MaxCallDate=MAX(StartedAt),
    MaxReceivedAtUtc=MAX(LastReceivedAtUtc)
FROM C;

SELECT
    RawRows=COUNT_BIG(*),
    LinkedIdPopulated=SUM(CONVERT(bigint,CASE WHEN NULLIF(LTRIM(RTRIM(LinkedId)),N'') IS NULL THEN 0 ELSE 1 END)),
    UniqueIdPopulated=SUM(CONVERT(bigint,CASE WHEN NULLIF(LTRIM(RTRIM(UniqueId)),N'') IS NULL THEN 0 ELSE 1 END)),
    RecordingFilePopulated=SUM(CONVERT(bigint,CASE WHEN NULLIF(LTRIM(RTRIM(RecordingFile)),N'') IS NULL THEN 0 ELSE 1 END)),
    MinCalldate=MIN(Calldate),
    MaxCalldate=MAX(Calldate),
    MinReceivedAtUtc=MIN(ReceivedAtUtc),
    MaxReceivedAtUtc=MAX(ReceivedAtUtc)
FROM dbo.RawCDR;
'@

    $distributionCte = @'
WITH R AS
(
    SELECT
        RawCDRId,Calldate,ReceivedAtUtc,Duration,Billsec,Disposition,
        Src,Dst,Did,Dcontext,RecordingFile,LinkedId,UniqueId,
        CallKey=CASE
            WHEN NULLIF(LTRIM(RTRIM(LinkedId)),N'') IS NOT NULL THEN N'linked:'+LinkedId
            WHEN NULLIF(LTRIM(RTRIM(UniqueId)),N'') IS NOT NULL THEN N'unique:'+UniqueId
            ELSE N'raw:'+CONVERT(nvarchar(30),RawCDRId)
        END,
        Direction=CASE
            WHEN NULLIF(Did,N'') IS NOT NULL OR Dcontext LIKE N'%from-trunk%' THEN N'inbound'
            WHEN Dcontext LIKE N'%from-internal%' OR Dcontext LIKE N'%outbound%' THEN N'outbound'
            ELSE N'unknown'
        END
    FROM dbo.RawCDR
),
C AS
(
    SELECT
        CallKey,
        StartedAt=MIN(Calldate),
        CompletedAt=MAX(DATEADD(second,ISNULL(Duration,0),Calldate)),
        DurationSeconds=MAX(ISNULL(Duration,0)),
        Billsec=MAX(ISNULL(Billsec,0)),
        LegCount=COUNT_BIG(*),
        Direction=CASE
            WHEN MAX(CASE WHEN Direction=N'inbound' THEN 1 ELSE 0 END)=1 THEN N'inbound'
            WHEN MAX(CASE WHEN Direction=N'outbound' THEN 1 ELSE 0 END)=1 THEN N'outbound'
            ELSE N'unknown'
        END,
        AnsweredExtension=MAX(CASE
            WHEN (Disposition=N'ANSWERED' OR ISNULL(Billsec,0)>0) AND LEN(ISNULL(Dst,N'')) BETWEEN 3 AND 4 THEN Dst
            WHEN (Disposition=N'ANSWERED' OR ISNULL(Billsec,0)>0) AND LEN(ISNULL(Src,N'')) BETWEEN 3 AND 4 THEN Src
        END)
    FROM R
    GROUP BY CallKey
)
'@

    $dailyData = Invoke-ReadOnlyQuery -Connection $connection -Sql ($distributionCte + @'
SELECT
    CallDate=CONVERT(date,StartedAt),
    Calls=COUNT_BIG(*),
    AnsweredCalls=SUM(CONVERT(bigint,CASE WHEN Billsec>0 THEN 1 ELSE 0 END)),
    TotalDurationSeconds=SUM(CONVERT(bigint,DurationSeconds))
FROM C
GROUP BY CONVERT(date,StartedAt)
ORDER BY CallDate;
'@)

    $hourlyData = Invoke-ReadOnlyQuery -Connection $connection -Sql ($distributionCte + @'
SELECT
    HourOfDay=DATEPART(hour,StartedAt),
    Calls=COUNT_BIG(*),
    ActiveDates=COUNT(DISTINCT CONVERT(date,StartedAt)),
    CallsPerActiveDay=CONVERT(decimal(18,2),COUNT_BIG(*))/NULLIF(COUNT(DISTINCT CONVERT(date,StartedAt)),0)
FROM C
GROUP BY DATEPART(hour,StartedAt)
ORDER BY HourOfDay;
'@)

    $durationData = Invoke-ReadOnlyQuery -Connection $connection -Sql ($distributionCte + @'
, D AS
(
    SELECT DISTINCT
        P50=PERCENTILE_CONT(0.50) WITHIN GROUP (ORDER BY DurationSeconds) OVER(),
        P95=PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY DurationSeconds) OVER(),
        P99=PERCENTILE_CONT(0.99) WITHIN GROUP (ORDER BY DurationSeconds) OVER()
    FROM C
)
SELECT * FROM D;
'@)

    $bucketData = Invoke-ReadOnlyQuery -Connection $connection -Sql ($distributionCte + @'
, B AS
(
    SELECT
        BucketStart=DATEADD(minute,(DATEDIFF(minute,0,CompletedAt)/5)*5,0),
        Calls=COUNT_BIG(*)
    FROM C
    WHERE CompletedAt IS NOT NULL
      AND DATEPART(hour,CompletedAt)>=@WorkingHourStart
      AND DATEPART(hour,CompletedAt)<@WorkingHourEnd
    GROUP BY DATEADD(minute,(DATEDIFF(minute,0,CompletedAt)/5)*5,0)
), P AS
(
    SELECT DISTINCT
        NonEmptyBucketCount=COUNT_BIG(*) OVER(),
        P50=PERCENTILE_CONT(0.50) WITHIN GROUP (ORDER BY Calls) OVER(),
        P95=PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY Calls) OVER(),
        P99=PERCENTILE_CONT(0.99) WITHIN GROUP (ORDER BY Calls) OVER(),
        Maximum=MAX(Calls) OVER()
    FROM B
)
SELECT * FROM P;
'@) -Parameters @{ "@WorkingHourStart"=$WorkingHourStart; "@WorkingHourEnd"=$WorkingHourEnd }

    $extensionData = Invoke-ReadOnlyQuery -Connection $connection -Sql ($distributionCte + @'
SELECT TOP(25)
    Extension=COALESCE(NULLIF(AnsweredExtension,N''),N'UNKNOWN'),
    Calls=COUNT_BIG(*),
    TotalDurationSeconds=SUM(CONVERT(bigint,DurationSeconds))
FROM C
GROUP BY COALESCE(NULLIF(AnsweredExtension,N''),N'UNKNOWN')
ORDER BY Calls DESC,Extension;
'@)

    $directionData = Invoke-ReadOnlyQuery -Connection $connection -Sql ($distributionCte + @'
SELECT Direction,Calls=COUNT_BIG(*)
FROM C
GROUP BY Direction
ORDER BY Calls DESC;
'@)

    $recordingData = Invoke-ReadOnlyQuery -Connection $connection -Sql @'
SELECT
    PathKind=CASE
        WHEN RecordingFile LIKE N'/%' THEN N'LINUX_ABSOLUTE'
        WHEN RecordingFile LIKE N'[A-Za-z]:\%' THEN N'WINDOWS_ABSOLUTE'
        WHEN NULLIF(LTRIM(RTRIM(RecordingFile)),N'') IS NULL THEN N'MISSING'
        ELSE N'RELATIVE_OR_FILENAME'
    END,
    Files=COUNT_BIG(*)
FROM dbo.RawCDR
GROUP BY CASE
    WHEN RecordingFile LIKE N'/%' THEN N'LINUX_ABSOLUTE'
    WHEN RecordingFile LIKE N'[A-Za-z]:\%' THEN N'WINDOWS_ABSOLUTE'
    WHEN NULLIF(LTRIM(RTRIM(RecordingFile)),N'') IS NULL THEN N'MISSING'
    ELSE N'RELATIVE_OR_FILENAME'
END
ORDER BY Files DESC;

SELECT
    Extension=CASE
        WHEN NULLIF(LTRIM(RTRIM(RecordingFile)),N'') IS NULL THEN N'(missing)'
        WHEN CHARINDEX(N'.',REVERSE(RecordingFile))=0 THEN N'(none)'
        ELSE LOWER(RIGHT(RecordingFile,CHARINDEX(N'.',REVERSE(RecordingFile))))
    END,
    Files=COUNT_BIG(*)
FROM dbo.RawCDR
GROUP BY CASE
    WHEN NULLIF(LTRIM(RTRIM(RecordingFile)),N'') IS NULL THEN N'(missing)'
    WHEN CHARINDEX(N'.',REVERSE(RecordingFile))=0 THEN N'(none)'
    ELSE LOWER(RIGHT(RecordingFile,CHARINDEX(N'.',REVERSE(RecordingFile))))
END
ORDER BY Files DESC;

SELECT TOP(20)
    RawCDRId,
    Calldate,
    MaskedSrc=CASE WHEN NULLIF(Src,N'') IS NULL THEN NULL
        WHEN LEN(Src)<=4 THEN REPLICATE(N'*',LEN(Src))
        ELSE LEFT(Src,2)+REPLICATE(N'*',LEN(Src)-4)+RIGHT(Src,2) END,
    MaskedDst=CASE WHEN NULLIF(Dst,N'') IS NULL THEN NULL
        WHEN LEN(Dst)<=4 THEN REPLICATE(N'*',LEN(Dst))
        ELSE LEFT(Dst,2)+REPLICATE(N'*',LEN(Dst)-4)+RIGHT(Dst,2) END,
    Direction=CASE
        WHEN NULLIF(Did,N'') IS NOT NULL OR Dcontext LIKE N'%from-trunk%' THEN N'inbound'
        WHEN Dcontext LIKE N'%from-internal%' OR Dcontext LIKE N'%outbound%' THEN N'outbound'
        ELSE N'unknown' END,
    Duration,
    Billsec,
    Disposition,
    HasLinkedId=CONVERT(bit,CASE WHEN NULLIF(LinkedId,N'') IS NULL THEN 0 ELSE 1 END),
    HasUniqueId=CONVERT(bit,CASE WHEN NULLIF(UniqueId,N'') IS NULL THEN 0 ELSE 1 END),
    HasRecordingFile=CONVERT(bit,CASE WHEN NULLIF(RecordingFile,N'') IS NULL THEN 0 ELSE 1 END),
    RecordingExtension=CASE
        WHEN NULLIF(LTRIM(RTRIM(RecordingFile)),N'') IS NULL THEN NULL
        WHEN CHARINDEX(N'.',REVERSE(RecordingFile))=0 THEN NULL
        ELSE LOWER(RIGHT(RecordingFile,CHARINDEX(N'.',REVERSE(RecordingFile)))) END
FROM dbo.RawCDR
ORDER BY Calldate DESC,RawCDRId DESC;
'@

    $groupingCte = @'
WITH R AS
(
    SELECT
        RawCDRId,LinkedId,UniqueId,Calldate,ReceivedAtUtc,Duration,
        CallKey=CASE
            WHEN NULLIF(LTRIM(RTRIM(LinkedId)),N'') IS NOT NULL THEN N'linked:'+LinkedId
            WHEN NULLIF(LTRIM(RTRIM(UniqueId)),N'') IS NOT NULL THEN N'unique:'+UniqueId
            ELSE N'raw:'+CONVERT(nvarchar(30),RawCDRId)
        END
    FROM dbo.RawCDR
), C AS
(
    SELECT
        CallKey,
        LegCount=COUNT_BIG(*),
        FirstCallDate=MIN(Calldate),
        LastCallDate=MAX(Calldate),
        FirstReceivedAtUtc=MIN(ReceivedAtUtc),
        LastReceivedAtUtc=MAX(ReceivedAtUtc)
    FROM R
    GROUP BY CallKey
)
'@

    $legDistributionData = Invoke-ReadOnlyQuery -Connection $connection -Sql ($groupingCte + @'
SELECT LegCount,Calls=COUNT_BIG(*)
FROM C
GROUP BY LegCount
ORDER BY LegCount;
'@)

    $multiLegData = Invoke-ReadOnlyQuery -Connection $connection -Sql ($groupingCte + @'
SELECT TOP(25)
    CallKey,
    LegCount,
    FirstCallDate,
    LastCallDate,
    ArrivalSpreadSeconds=DATEDIFF(second,FirstReceivedAtUtc,LastReceivedAtUtc)
FROM C
WHERE LegCount>1
ORDER BY LegCount DESC,ArrivalSpreadSeconds DESC;
'@)

    $candidateData = Invoke-ReadOnlyQuery -Connection $connection -Sql @'
WITH R AS
(
    SELECT
        RawCDRId,Calldate,Duration,Billsec,Disposition,Src,Dst,Did,Dcontext,
        RecordingFile,LinkedId,UniqueId,
        CallKey=CASE
            WHEN NULLIF(LTRIM(RTRIM(LinkedId)),N'') IS NOT NULL THEN N'linked:'+LinkedId
            WHEN NULLIF(LTRIM(RTRIM(UniqueId)),N'') IS NOT NULL THEN N'unique:'+UniqueId
            ELSE N'raw:'+CONVERT(nvarchar(30),RawCDRId)
        END,
        Direction=CASE
            WHEN NULLIF(Did,N'') IS NOT NULL OR Dcontext LIKE N'%from-trunk%' THEN N'inbound'
            WHEN Dcontext LIKE N'%from-internal%' OR Dcontext LIKE N'%outbound%' THEN N'outbound'
            ELSE N'unknown' END
    FROM dbo.RawCDR
), C AS
(
    SELECT
        CallKey,
        RawCDRId=MIN(RawCDRId),
        LinkedId=MAX(NULLIF(LinkedId,N'')),
        UniqueId=MAX(NULLIF(UniqueId,N'')),
        Extension=MAX(CASE
            WHEN (Disposition=N'ANSWERED' OR ISNULL(Billsec,0)>0) AND LEN(ISNULL(Dst,N'')) BETWEEN 3 AND 4 THEN Dst
            WHEN (Disposition=N'ANSWERED' OR ISNULL(Billsec,0)>0) AND LEN(ISNULL(Src,N'')) BETWEEN 3 AND 4 THEN Src END),
        Direction=CASE
            WHEN MAX(CASE WHEN Direction=N'inbound' THEN 1 ELSE 0 END)=1 THEN N'inbound'
            WHEN MAX(CASE WHEN Direction=N'outbound' THEN 1 ELSE 0 END)=1 THEN N'outbound'
            ELSE N'unknown' END,
        DurationSeconds=MAX(ISNULL(Duration,0)),
        RecordingStatus=CASE WHEN MAX(CASE WHEN NULLIF(RecordingFile,N'') IS NULL THEN 0 ELSE 1 END)=1 THEN N'REFERENCED_UNVERIFIED' ELSE N'MISSING' END,
        LegCount=COUNT_BIG(*)
    FROM R
    GROUP BY CallKey
), Ranked AS
(
    SELECT
        C.*,
        CustomerClassMasked=CONVERT(nvarchar(20),N'UNKNOWN'),
        SelectionRank=ROW_NUMBER() OVER
        (
            ORDER BY
                CASE WHEN LegCount>1 THEN 0 ELSE 1 END,
                CASE WHEN RecordingStatus=N'MISSING' THEN 0 ELSE 1 END,
                ABS(DurationSeconds-300),
                RawCDRId DESC
        )
    FROM C
)
SELECT TOP(50)
    CandidateId=CONCAT(N'C',RIGHT(N'0000'+CONVERT(nvarchar(10),SelectionRank),4)),
    RawCDRId,CallKey,LinkedId,UniqueId,
    Extension=COALESCE(Extension,N'UNKNOWN'),Direction,DurationSeconds,
    RecordingStatus,
    AudioChannels=CONVERT(nvarchar(20),N'UNKNOWN'),
    AudioCodec=CONVERT(nvarchar(20),N'UNKNOWN'),
    CustomerClassMasked,
    ReasonForSelection=CASE
        WHEN LegCount>1 THEN CONCAT(N'MULTI_LEG_',LegCount)
        WHEN RecordingStatus=N'MISSING' THEN N'MISSING_RECORDING_REFERENCE'
        WHEN DurationSeconds<30 THEN N'SHORT_CALL'
        WHEN DurationSeconds>900 THEN N'LONG_CALL'
        ELSE N'DURATION_EXTENSION_COVERAGE' END,
    PIIMaskedNotes=CONVERT(nvarchar(200),N'No phone number or audio included')
FROM Ranked
ORDER BY SelectionRank;
'@

    $runtime = [ordered]@{
        collected_at_utc = [DateTime]::UtcNow
        dotnet_available = Test-CommandAvailable "dotnet"
        python_launcher_present = Test-CommandAvailable "python"
        python_runtime_verified = $false
        ffmpeg_available = Test-CommandAvailable "ffmpeg"
        ffprobe_available = Test-CommandAvailable "ffprobe"
        docker_available = Test-CommandAvailable "docker"
        nvidia_smi_available = Test-CommandAvailable "nvidia-smi"
    }
    if ($runtime.python_launcher_present) {
        try {
            $pythonVersion = & python --version 2>&1
            $runtime.python_runtime_verified = ($LASTEXITCODE -eq 0)
            $runtime.python_version = [string]$pythonVersion
        } catch {
            $runtime.python_version = $null
        }
    }

    $report = [ordered]@{
        generated_at_utc = [DateTime]::UtcNow
        started_at_utc = $startedAt
        mode = "READ_ONLY_DISCOVERY"
        source_root = $SourceRoot
        connection = Get-SafeConnectionMetadata -Builder $builder
        raw_cdr_columns = Convert-TableRows $schemaData.Tables[0]
        raw_cdr_indexes = Convert-TableRows $schemaData.Tables[1]
        relevant_tables = Convert-TableRows $schemaData.Tables[2]
        relevant_table_columns = Convert-TableRows $schemaData.Tables[3]
        call_baseline = Convert-TableRows $baselineData.Tables[0]
        raw_row_baseline = Convert-TableRows $baselineData.Tables[1]
        daily_calls = Convert-TableRows $dailyData.Tables[0]
        hourly_calls = Convert-TableRows $hourlyData.Tables[0]
        duration_percentiles_seconds = Convert-TableRows $durationData.Tables[0]
        completed_calls_per_nonempty_5m_bucket = Convert-TableRows $bucketData.Tables[0]
        top_extensions = Convert-TableRows $extensionData.Tables[0]
        direction_distribution = Convert-TableRows $directionData.Tables[0]
        recording_path_kinds = Convert-TableRows $recordingData.Tables[0]
        recording_extensions = Convert-TableRows $recordingData.Tables[1]
        masked_recent_samples = Convert-TableRows $recordingData.Tables[2]
        leg_count_distribution = Convert-TableRows $legDistributionData.Tables[0]
        multi_leg_examples = Convert-TableRows $multiLegData.Tables[0]
        runtime_tools = $runtime
        limitations = @(
            "Recording reachability is not inferred from a Linux path stored in SQL.",
            "Audio metadata requires an approved readable recording and ffprobe.",
            "Five-minute percentiles use non-empty working-hour buckets; zero buckets are excluded and explicitly labeled.",
            "Direction and answered-extension rules mirror the existing dashboard SQL and require business validation."
        )
    }

    Write-JsonFile -Value $report -Path (Join-Path $OutputRoot "sprint0-discovery.json")

    $candidates = Convert-TableRows $candidateData.Tables[0]
    $candidates | Export-Csv -LiteralPath (Join-Path (Split-Path $OutputRoot -Parent) "golden-set-candidate-manifest.csv") -NoTypeInformation -Encoding UTF8

    Write-JsonFile -Value ([ordered]@{
        status = "SUCCESS"
        generated_at_utc = [DateTime]::UtcNow
        output = (Join-Path $OutputRoot "sprint0-discovery.json")
        candidate_count = $candidates.Count
    }) -Path (Join-Path $OutputRoot "run-status.json")

    Write-Output "Sprint 0 read-only discovery completed."
}
catch {
    Write-JsonFile -Value ([ordered]@{
        status = "FAILED"
        generated_at_utc = [DateTime]::UtcNow
        error_type = $_.Exception.GetType().FullName
        error_message = $_.Exception.Message
    }) -Path (Join-Path $OutputRoot "run-status.json")
    throw
}
finally {
    if ($connection.State -ne [System.Data.ConnectionState]::Closed) {
        $connection.Close()
    }
    $connection.Dispose()
}
