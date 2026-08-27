param(
    [Parameter(Mandatory = $true)]
    [string]$ExchangeDirectory
)

$ErrorActionPreference = "Stop"
$requestPath = Join-Path $ExchangeDirectory "request.json"
$resultPath = Join-Path $ExchangeDirectory "result.json"
$temporaryResultPath = Join-Path $ExchangeDirectory "result.tmp.json"

try {
    if (-not (Test-Path -LiteralPath $requestPath)) {
        throw "Accounting bridge request was not found: $requestPath"
    }

    $request = Get-Content -LiteralPath $requestPath -Raw | ConvertFrom-Json
    $days = [Math]::Max(1, [Math]::Min(365, [int]$request.Days))
    $arguments = @(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", [string]$request.ScriptPath,
        "-RepositoryRoot", [string]$request.RepositoryRoot,
        "-Days", $days,
        "-SkipIdentityRebuild"
    )

    $lines = & powershell.exe @arguments 2>&1
    $exitCode = $LASTEXITCODE
    $output = ($lines | Out-String).TrimEnd()
    $status = if ($exitCode -eq 0) { "SUCCESS" } else { "FAILED" }
    $errorText = if ($exitCode -eq 0) { "" } else { $output }

    [ordered]@{
        RequestId = [string]$request.RequestId
        Status = $status
        ExitCode = $exitCode
        Output = $output
        Error = $errorText
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $temporaryResultPath -Encoding UTF8

    Move-Item -LiteralPath $temporaryResultPath -Destination $resultPath -Force
    Remove-Item -LiteralPath $requestPath -Force -ErrorAction SilentlyContinue
    exit $exitCode
}
catch {
    $requestId = ""
    try {
        if (Test-Path -LiteralPath $requestPath) {
            $requestId = [string]((Get-Content -LiteralPath $requestPath -Raw | ConvertFrom-Json).RequestId)
        }
    } catch { }

    [ordered]@{
        RequestId = $requestId
        Status = "FAILED"
        ExitCode = 1
        Output = ""
        Error = $_.Exception.ToString()
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $temporaryResultPath -Encoding UTF8
    Move-Item -LiteralPath $temporaryResultPath -Destination $resultPath -Force
    exit 1
}
