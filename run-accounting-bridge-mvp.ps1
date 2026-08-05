param([int]$Days = 30)
$ErrorActionPreference = 'Stop'

$repo = 'D:\DigiAhan\CDR3.1.0git'
$appsettings = Join-Path $repo 'Source\appsettings.json'
$logDir = Join-Path $repo 'logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$log = Join-Path $logDir ("accounting-bridge-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')

function Log($m) { $x = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') | $m"; $x | Tee-Object -FilePath $log -Append }
function PDate([datetime]$d) { $p = New-Object System.Globalization.PersianCalendar; '{0:0000}/{1:00}/{2:00}' -f $p.GetYear($d),$p.GetMonth($d),$p.GetDayOfMonth($d) }
function V($x) { if ($null -eq $x -or $x -is [DBNull]) { [DBNull]::Value } else { $x } }

if (!(Test-Path $appsettings)) { throw "appsettings.json not found: $appsettings" }
$config = Get-Content $appsettings -Raw | ConvertFrom-Json
$destCs = $config.ConnectionStrings.DigiAhanCdr
if ([string]::IsNullOrWhiteSpace($destCs)) { throw 'ConnectionStrings.DigiAhanCdr not found.' }

$cutoff = PDate ((Get-Date).Date.AddDays(-($Days-1)))
$sourceCs = 'Provider=SQLOLEDB;Data Source=COREI5;Initial Catalog=daftar1405;User ID=sa;Password=;'
$src = $null; $dst = $null; $tx = $null

try {
  Log "START cutoff=$cutoff"
  $src = New-Object -ComObject ADODB.Connection
  $src.ConnectionTimeout = 15; $src.CommandTimeout = 180
  $src.Open($sourceCs)
  Log 'Legacy SQL connection OK'

  Add-Type -AssemblyName System.Data
  $dst = New-Object System.Data.SqlClient.SqlConnection($destCs)
  $dst.Open()
  Log 'Destination SQL connection OK'

  $schema = @"
IF OBJECT_ID(N'dbo.AccountingVisitors',N'U') IS NULL CREATE TABLE dbo.AccountingVisitors(SourceDatabase nvarchar(128) NOT NULL,FiscalYear int NOT NULL,VisitorId int NOT NULL,VisitorName nvarchar(200) NULL,RoleType nvarchar(30) NULL,IsActive bit NOT NULL,ImportedAtUtc datetime2(0) NOT NULL,CONSTRAINT PK_AccountingVisitors PRIMARY KEY(SourceDatabase,FiscalYear,VisitorId));
IF OBJECT_ID(N'dbo.AccountingCustomers',N'U') IS NULL CREATE TABLE dbo.AccountingCustomers(SourceDatabase nvarchar(128) NOT NULL,FiscalYear int NOT NULL,DetailCode nvarchar(18) NOT NULL,ShortCode nvarchar(12) NULL,CustomerName nvarchar(400) NULL,ManagerName nvarchar(200) NULL,EconomicCode nvarchar(100) NULL,CustomerTel nvarchar(200) NULL,CustomerAddress nvarchar(400) NULL,ImportedAtUtc datetime2(0) NOT NULL,CONSTRAINT PK_AccountingCustomers PRIMARY KEY(SourceDatabase,FiscalYear,DetailCode));
IF OBJECT_ID(N'dbo.AccountingInvoices',N'U') IS NULL CREATE TABLE dbo.AccountingInvoices(SourceDatabase nvarchar(128) NOT NULL,FiscalYear int NOT NULL,FactorCode int NOT NULL,DocumentNumber decimal(18,0) NULL,FactorNumber decimal(18,0) NULL,FactorDate nvarchar(10) NULL,TypeIndex int NULL,TypeDescription nvarchar(500) NULL,CustomerShortCode nvarchar(12) NULL,CustomerDetailCode nvarchar(18) NULL,CustomerName nvarchar(400) NULL,Amount decimal(19,4) NULL,VisitorId int NULL,VisitorName nvarchar(400) NULL,ImportedAtUtc datetime2(0) NOT NULL,CONSTRAINT PK_AccountingInvoices PRIMARY KEY(SourceDatabase,FiscalYear,FactorCode));
"@
  $c=$dst.CreateCommand(); $c.CommandText=$schema; $c.CommandTimeout=120; [void]$c.ExecuteNonQuery()

  $tx=$dst.BeginTransaction()
  $c=$dst.CreateCommand(); $c.Transaction=$tx; $c.CommandText="DELETE FROM dbo.AccountingInvoices WHERE SourceDatabase='daftar1405' AND FiscalYear=1405 AND FactorDate>=@d; DELETE FROM dbo.AccountingVisitors WHERE SourceDatabase='daftar1405' AND FiscalYear=1405;"; [void]$c.Parameters.Add('@d',[Data.SqlDbType]::NVarChar,10); $c.Parameters['@d'].Value=$cutoff; [void]$c.ExecuteNonQuery()

  $vis=0
  $rs=$src.Execute('SELECT visitorid,visitorname FROM visitor ORDER BY visitorid')
  while(!$rs.EOF){
    $id=[int]$rs.Fields.Item('visitorid').Value; $name=[string]$rs.Fields.Item('visitorname').Value
    $role=if($id -eq 6){'SHARED'}elseif($id -eq 7){'COLLECTIONS'}else{'SALES'}; $active=if($id -eq 8){0}else{1}
    $c=$dst.CreateCommand(); $c.Transaction=$tx; $c.CommandText='INSERT INTO dbo.AccountingVisitors VALUES(@db,@fy,@id,@n,@r,@a,SYSUTCDATETIME())'
    [void]$c.Parameters.Add('@db',[Data.SqlDbType]::NVarChar,128);$c.Parameters['@db'].Value='daftar1405'
    [void]$c.Parameters.Add('@fy',[Data.SqlDbType]::Int);$c.Parameters['@fy'].Value=1405
    [void]$c.Parameters.Add('@id',[Data.SqlDbType]::Int);$c.Parameters['@id'].Value=$id
    [void]$c.Parameters.Add('@n',[Data.SqlDbType]::NVarChar,200);$c.Parameters['@n'].Value=V $name
    [void]$c.Parameters.Add('@r',[Data.SqlDbType]::NVarChar,30);$c.Parameters['@r'].Value=$role
    [void]$c.Parameters.Add('@a',[Data.SqlDbType]::Bit);$c.Parameters['@a'].Value=$active
    [void]$c.ExecuteNonQuery();$vis++;$rs.MoveNext()
  };$rs.Close();Log "Visitors=$vis"

  $sql="SELECT f.Code AS FactorCode,f.dnumber,f.fnumber,f.fdate,f.typeindex,f.type,f.customercode,f.customername,f.amount,f.visitorid,v.visitorname,c.detailcode,c.managername,c.economiccode,c.customertel,c.customeraddress FROM factor f LEFT JOIN visitor v ON v.visitorid=f.visitorid LEFT JOIN customer c ON c.shortcode=f.customercode WHERE f.typeindex=1 AND f.fdate>='$cutoff' ORDER BY f.Code"
  $rs=$src.Execute($sql);$inv=0;$cust=0
  while(!$rs.EOF){
    $detail=[string]$rs.Fields.Item('detailcode').Value; $short=[string]$rs.Fields.Item('customercode').Value; $custName=[string]$rs.Fields.Item('customername').Value
    if(![string]::IsNullOrWhiteSpace($detail)){
      $c=$dst.CreateCommand();$c.Transaction=$tx;$c.CommandText=@"
UPDATE dbo.AccountingCustomers SET ShortCode=@s,CustomerName=@n,ManagerName=@m,EconomicCode=@e,CustomerTel=@t,CustomerAddress=@a,ImportedAtUtc=SYSUTCDATETIME() WHERE SourceDatabase='daftar1405' AND FiscalYear=1405 AND DetailCode=@d;
IF @@ROWCOUNT=0 INSERT INTO dbo.AccountingCustomers VALUES('daftar1405',1405,@d,@s,@n,@m,@e,@t,@a,SYSUTCDATETIME());
"@
      foreach($p in @(@('@d',[Data.SqlDbType]::NVarChar,18,$detail),@('@s',[Data.SqlDbType]::NVarChar,12,$short),@('@n',[Data.SqlDbType]::NVarChar,400,$custName),@('@m',[Data.SqlDbType]::NVarChar,200,$rs.Fields.Item('managername').Value),@('@e',[Data.SqlDbType]::NVarChar,100,$rs.Fields.Item('economiccode').Value),@('@t',[Data.SqlDbType]::NVarChar,200,$rs.Fields.Item('customertel').Value),@('@a',[Data.SqlDbType]::NVarChar,400,$rs.Fields.Item('customeraddress').Value))){[void]$c.Parameters.Add($p[0],$p[1],$p[2]);$c.Parameters[$p[0]].Value=V $p[3]}
      [void]$c.ExecuteNonQuery();$cust++
    }
    $c=$dst.CreateCommand();$c.Transaction=$tx;$c.CommandText='INSERT INTO dbo.AccountingInvoices VALUES(''daftar1405'',1405,@fc,@dn,@fn,@fd,@ti,@td,@cs,@cd,@cn,@am,@vi,@vn,SYSUTCDATETIME())'
    foreach($p in @(@('@fc',[Data.SqlDbType]::Int,0,$rs.Fields.Item('FactorCode').Value),@('@dn',[Data.SqlDbType]::Decimal,0,$rs.Fields.Item('dnumber').Value),@('@fn',[Data.SqlDbType]::Decimal,0,$rs.Fields.Item('fnumber').Value),@('@fd',[Data.SqlDbType]::NVarChar,10,$rs.Fields.Item('fdate').Value),@('@ti',[Data.SqlDbType]::Int,0,$rs.Fields.Item('typeindex').Value),@('@td',[Data.SqlDbType]::NVarChar,500,$rs.Fields.Item('type').Value),@('@cs',[Data.SqlDbType]::NVarChar,12,$short),@('@cd',[Data.SqlDbType]::NVarChar,18,$detail),@('@cn',[Data.SqlDbType]::NVarChar,400,$custName),@('@am',[Data.SqlDbType]::Money,0,$rs.Fields.Item('amount').Value),@('@vi',[Data.SqlDbType]::Int,0,$rs.Fields.Item('visitorid').Value),@('@vn',[Data.SqlDbType]::NVarChar,400,$rs.Fields.Item('visitorname').Value))){if($p[2]-gt 0){[void]$c.Parameters.Add($p[0],$p[1],$p[2])}else{[void]$c.Parameters.Add($p[0],$p[1])};$c.Parameters[$p[0]].Value=V $p[3]}
    [void]$c.ExecuteNonQuery();$inv++;$rs.MoveNext()
  };$rs.Close()

  $tx.Commit();$tx=$null
  Log "SUCCESS invoices=$inv customerRowsProcessed=$cust visitors=$vis"
  Write-Host "`nAccounting Bridge completed successfully." -ForegroundColor Green
  Write-Host "Invoices: $inv | Visitors: $vis" -ForegroundColor Cyan
  Write-Host "Log: $log" -ForegroundColor DarkGray
}
catch{if($tx){try{$tx.Rollback()}catch{}};Log $_.Exception.ToString();throw}
finally{if($src){try{$src.Close()}catch{}};if($dst){try{$dst.Close()}catch{}}}
