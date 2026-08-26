[CmdletBinding()]
param(
    [string]$InputPath = 'C:\Users\ehsan\Documents\ChatGPT\DIGIAHAN\recording-sample',
    [string]$LiveAiPath = 'D:\DigiAhan\CDR4.0\Source\wwwroot\ai',
    [switch]$SkipTranscription,
    [switch]$NoDeploy
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$outputPath = Join-Path $InputPath 'output'
$triagePath = Join-Path $outputPath 'batch-triage-v1.json'
$transcriptPath = Join-Path $outputPath 'batch-transcripts-small-v1.json'
$dashboardPath = Join-Path $outputPath 'batch-data.json'
$pythonCandidates = @(
    'C:\Users\ehsan\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe',
    (Join-Path (Split-Path $repoRoot -Parent) '.sprint05-runtime\python\python.exe')
)
$python = $pythonCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $python) { throw 'Python runtime مورد نیاز پیدا نشد.' }

$runtimeRoot = Join-Path (Split-Path $repoRoot -Parent) '.sprint05-runtime'
$packagePath = Join-Path $runtimeRoot 'python-packages'
$modelPath = Join-Path $runtimeRoot 'models'
if (-not (Test-Path -LiteralPath $packagePath)) { throw "بسته‌های پردازش صدا پیدا نشد: $packagePath" }
if (-not (Test-Path -LiteralPath $modelPath)) { throw "مدل گفتار پیدا نشد: $modelPath" }
if (-not (Test-Path -LiteralPath $InputPath)) { throw "پوشه ویس پیدا نشد: $InputPath" }

$previousPythonPath = $env:PYTHONPATH
try {
    $env:PYTHONPATH = $packagePath
    New-Item -ItemType Directory -Path $outputPath -Force | Out-Null

    & $python (Join-Path $repoRoot 'tools\ai\triage_recording_batch.py') `
        $InputPath --output $triagePath
    if ($LASTEXITCODE -ne 0) { throw 'غربال اولیه ویس‌ها ناموفق بود.' }

    if (-not $SkipTranscription) {
        & $python (Join-Path $repoRoot 'tools\ai\transcribe_recording_batch.py') `
            $InputPath --triage $triagePath --output $transcriptPath `
            --model-cache $modelPath --model small --threads 4 --batch-size 8
        if ($LASTEXITCODE -ne 0) { throw 'تبدیل صدا به متن ناموفق بود.' }
    }

    & $python (Join-Path $repoRoot 'tools\ai\generate_safe_dashboard_batch.py') `
        --baseline (Join-Path $repoRoot 'Source\wwwroot\ai\sample-data.json') `
        --triage $triagePath --transcripts $transcriptPath --output $dashboardPath
    if ($LASTEXITCODE -ne 0) { throw 'ساخت خروجی مربی‌گری ناموفق بود.' }

    & $python (Join-Path $repoRoot 'Tests\ai-batch-coaching-test.py') $dashboardPath
    if ($LASTEXITCODE -ne 0) { throw 'کنترل حریم خصوصی یا قواعد مربی‌گری ناموفق بود.' }

    if (-not $NoDeploy) {
        if (-not (Test-Path -LiteralPath $LiveAiPath)) { throw "مسیر داشبورد زنده پیدا نشد: $LiveAiPath" }
        Copy-Item -LiteralPath $dashboardPath -Destination (Join-Path $LiveAiPath 'batch-data.json') -Force
    }

    $payload = Get-Content -LiteralPath $dashboardPath -Raw -Encoding utf8 | ConvertFrom-Json
    [pscustomobject]@{
        AudioFiles = $payload.metrics.audioFileCount
        Transcribed = $payload.metrics.transcribedNewCount
        CoachingReady = $payload.metrics.coachingReadyCount
        TranscriptReview = $payload.coaching.transcriptReviewCount
        NonPurchaseFindings = $payload.coaching.nonPurchaseFindingCount
        SensitiveReviews = $payload.coaching.sensitiveReviewCount
        Output = $dashboardPath
        Deployed = -not $NoDeploy
    } | Format-List
}
finally {
    $env:PYTHONPATH = $previousPythonPath
}
