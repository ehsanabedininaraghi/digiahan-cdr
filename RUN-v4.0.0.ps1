param([string]$RepositoryRoot = "D:\DigiAhan\CDR3.1.0git")
$ErrorActionPreference = "Stop"
$source = Join-Path $RepositoryRoot "Source"
$payload = Join-Path $PSScriptRoot "payload"
$logs = Join-Path $RepositoryRoot "Logs\Runs"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$runDir = Join-Path $logs "v4.0.0-$stamp"
$backup = Join-Path $RepositoryRoot "_backups\v4.0.0-$stamp"
New-Item -ItemType Directory -Force -Path $runDir,$backup,(Join-Path $RepositoryRoot "tools") | Out-Null
Start-Transcript -Path (Join-Path $runDir "installer-transcript.txt") -Force | Out-Null
$phase = "START"
try {
  $phase="STOP"; Write-Host "[1/6] Stopping dashboard..." -ForegroundColor Cyan
  Get-NetTCPConnection -LocalPort 5088 -State Listen -ErrorAction SilentlyContinue | ForEach-Object { if ($_.OwningProcess -gt 0) { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue } }
  Start-Sleep 2

  $phase="BACKUP"; Write-Host "[2/6] Backup..." -ForegroundColor Cyan
  foreach($p in @("Program.cs","Services\CustomerIntelligenceRepository.cs","Services\AgentPanelRepository.cs","Services\VoipIncidentLogger.cs","DigiAhan.CDR.Receiver.csproj")){
    $f=Join-Path $source $p; if(Test-Path $f){ Copy-Item $f (Join-Path $backup ([IO.Path]::GetFileName($f))) -Force }
  }

  $phase="INSTALL"; Write-Host "[3/6] Installing v4 core..." -ForegroundColor Cyan
  Copy-Item (Join-Path $payload "Source\Program.cs") (Join-Path $source "Program.cs") -Force
  Copy-Item (Join-Path $payload "Source\Services\*.cs") (Join-Path $source "Services") -Force
  Copy-Item (Join-Path $payload "tools\*.ps1") (Join-Path $RepositoryRoot "tools") -Force
  $proj=Join-Path $source "DigiAhan.CDR.Receiver.csproj"
  $txt=[IO.File]::ReadAllText($proj)
  $txt=[regex]::Replace($txt,'<Version>[^<]+</Version>','<Version>4.0.0</Version>')
  $txt=[regex]::Replace($txt,'<AssemblyVersion>[^<]+</AssemblyVersion>','<AssemblyVersion>4.0.0.0</AssemblyVersion>')
  $txt=[regex]::Replace($txt,'<FileVersion>[^<]+</FileVersion>','<FileVersion>4.0.0.0</FileVersion>')
  [IO.File]::WriteAllText($proj,$txt,(New-Object Text.UTF8Encoding($false)))

  $phase="BUILD"; Write-Host "[4/6] Building..." -ForegroundColor Cyan
  Push-Location $source
  try { dotnet build --no-restore; if($LASTEXITCODE -ne 0){ dotnet build }; if($LASTEXITCODE -ne 0){ throw "Build failed" } }
  finally { Pop-Location }

  $phase="START"; Write-Host "[5/6] Starting..." -ForegroundColor Cyan
  $out=Join-Path $runDir "application-stdout.log"; $err=Join-Path $runDir "application-stderr.log"
  $p=Start-Process dotnet -ArgumentList @("run","--no-build","--no-restore") -WorkingDirectory $source -RedirectStandardOutput $out -RedirectStandardError $err -WindowStyle Hidden -PassThru
  $healthy=$false
  1..40 | ForEach-Object { Start-Sleep 1; try { $h=Invoke-RestMethod http://localhost:5088/health -TimeoutSec 3; if($h.status -eq 'healthy'){ $healthy=$true; return } } catch{} }
  if(-not $healthy){ throw "Dashboard did not become healthy" }

  $phase="TEST"; Write-Host "[6/6] Testing..." -ForegroundColor Cyan
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $RepositoryRoot "tools\test-all-connections-v4.0.0.ps1") -RepositoryRoot $RepositoryRoot -ServerUrl http://localhost:5088 -Strict 2>&1 | Tee-Object -FilePath (Join-Path $runDir "diagnostics.txt")
  if($LASTEXITCODE -ne 0){ throw "Diagnostics failed" }
  "SUCCESS`nVersion=4.0.0`nPhase=$phase`nRunDir=$runDir`nBackup=$backup" | Set-Content (Join-Path $runDir "summary.txt") -Encoding UTF8
  Write-Host "v4.0.0 installed successfully." -ForegroundColor Green
}
catch {
  $_ | Format-List * -Force | Out-String | Set-Content (Join-Path $runDir "fatal-error.txt") -Encoding UTF8
  "FAILED`nVersion=4.0.0`nPhase=$phase`nRunDir=$runDir`nBackup=$backup`nError=$($_.Exception.Message)" | Set-Content (Join-Path $runDir "summary.txt") -Encoding UTF8
  Write-Host $_.Exception.ToString() -ForegroundColor Red
}
finally {
  try { Stop-Transcript | Out-Null } catch {}
  Compress-Archive -Path (Join-Path $runDir "*") -DestinationPath "$runDir.zip" -Force
  Write-Host "Diagnostic ZIP: $runDir.zip" -ForegroundColor Yellow
}
