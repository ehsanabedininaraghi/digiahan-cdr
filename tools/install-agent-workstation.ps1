param(
    [Parameter(Mandatory=$true)]
    [ValidatePattern('^\d{3}$')]
    [string]$Extension,
    [string]$ServerUrl = "http://192.168.8.143:5088"
)

$ErrorActionPreference = "Stop"
$names = @{
"201"="مجید پورمهدی";"202"="مجید پورمهدی";"203"="مینا شافوری";"204"="مینا شافوری";
"205"="ایلیا حاجی";"206"="ایلیا حاجی";"207"="مهدی تقی‌زاده";"208"="مهدی تقی‌زاده";
"211"="مهدی حسنی";"212"="مهدی حسنی";"213"="مهدی فراهانی";"214"="مهدی فراهانی";
"215"="حسنا مظاهری";"216"="حسنا مظاهری";"217"="فتحی";"218"="فتحی";
"219"="عباس زمانی";"220"="عباس زمانی";"223"="پویا";"224"="پویا";
"225"="احسان عابدینی";"226"="احسان عابدینی"
}
$name = if ($names.ContainsKey($Extension)) { $names[$Extension] } else { "داخلی $Extension" }
$url = "$ServerUrl/agent/$Extension"
$browser = @(
"$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
"${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
"$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
"${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $browser) { throw "Chrome or Edge was not found." }
$shell = New-Object -ComObject WScript.Shell
$desktop = [Environment]::GetFolderPath("Desktop")
$startup = [Environment]::GetFolderPath("Startup")
$title = "پنل فروش دیجی‌آهن - $name"
foreach ($folder in @($desktop,$startup)) {
    $shortcut = $shell.CreateShortcut((Join-Path $folder "$title.lnk"))
    $shortcut.TargetPath = $browser
    $shortcut.Arguments = "--app=$url --start-maximized"
    $shortcut.WorkingDirectory = Split-Path $browser
    $shortcut.Description = "DigiAhan Sales Agent Panel - Extension $Extension"
    $shortcut.Save()
}
Write-Host "Installed for $name - Extension $Extension" -ForegroundColor Green
Write-Host "Desktop shortcut and automatic startup were created." -ForegroundColor Cyan
Start-Process $browser "--app=$url --start-maximized"
