param(
    [Parameter(Mandatory = $true)][string]$Phone,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [string]$SourceRoot = ""
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = Join-Path (Split-Path $PSScriptRoot -Parent) "Source"
}
$settings = Get-Content -LiteralPath (Join-Path $SourceRoot "appsettings.json") -Raw | ConvertFrom-Json
$connection = New-Object System.Data.SqlClient.SqlConnection ([string]$settings.ConnectionStrings.DigiAhanCdr)
$connection.Open()
try {
    $command = $connection.CreateCommand()
    $command.CommandTimeout = 60
    $command.CommandText = @"
DECLARE @phone nvarchar(32)=@value;
SELECT
  RawDidarContacts=(SELECT COUNT_BIG(*) FROM dbo.DidarContacts WHERE
      CONCAT(ISNULL(MobilePhone,N''),N'|',ISNULL(LandlinePhone,N''),N'|',ISNULL(CompanyPhone,N''),N'|',
             ISNULL(Fax,N''),N'|',ISNULL(OtherPhones,N''),N'|',ISNULL(Phones2,N'')) LIKE N'%'+@phone+N'%'),
  ExtractedDidarPhones=(SELECT COUNT_BIG(*) FROM dbo.DidarContactPhones WHERE NormalizedPhone=@phone OR OriginalPhone LIKE N'%'+@phone+N'%'),
  IdentityPhones=(SELECT COUNT_BIG(*) FROM dbo.CustomerIdentityPhones WHERE NormalizedPhone=@phone),
  PhoneDirectory=(SELECT COUNT_BIG(*) FROM dbo.CustomerPhoneDirectory WHERE NormalizedPhone=@phone),
  RawLastSyncedAt=(SELECT MAX(LastSyncedAt) FROM dbo.DidarContacts);
"@
    $null = $command.Parameters.Add("@value", [System.Data.SqlDbType]::NVarChar, 32)
    $command.Parameters["@value"].Value = $Phone
    $reader = $command.ExecuteReader()
    $null = $reader.Read()
    $result = [ordered]@{
        Phone = $Phone
        RawDidarContacts = [int64]$reader["RawDidarContacts"]
        ExtractedDidarPhones = [int64]$reader["ExtractedDidarPhones"]
        IdentityPhones = [int64]$reader["IdentityPhones"]
        PhoneDirectory = [int64]$reader["PhoneDirectory"]
        RawLastSyncedAt = if ($reader.IsDBNull(4)) { $null } else { [datetime]$reader["RawLastSyncedAt"] }
        CheckedAt = Get-Date
    }
    $reader.Dispose()
    $result | ConvertTo-Json | Set-Content -LiteralPath $OutputPath -Encoding UTF8
}
finally {
    $connection.Dispose()
}
