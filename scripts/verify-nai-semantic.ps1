#!/usr/bin/env pwsh
# Verifies the Files semantic path end-to-end:
#   embeddings (LUSI, else Ollama)  ->  NAI Lattice semantic.index  ->  query.vector  ->  semantic.delete
#
# Run after the local runtime and the NAI Lattice host are up:
#   pwsh -File scripts/verify-nai-semantic.ps1
#
# Exit codes: 0 = PASS, 1 = FAIL (prints the exact failing stage).

$ErrorActionPreference = 'Stop'
$lattice = 'http://127.0.0.1:8789'
$lusiDefault = 'http://127.0.0.1:27130'
$ollamaBase = 'http://127.0.0.1:11434/v1'
$storeDim = 1024
$testId = 'files-verify-' + [guid]::NewGuid().ToString('N').Substring(0, 12)

function Write-Stage([string]$name, [string]$status, [string]$detail = '') {
    $color = if ($status -eq 'PASS') { 'Green' } elseif ($status -eq 'SKIP') { 'Yellow' } else { 'Red' }
    Write-Host ("[{0}] {1}{2}" -f $status, $name, $(if ($detail) { " — $detail" } else { '' })) -ForegroundColor $color
}

# --- 1. Lattice bridge -------------------------------------------------------
try {
    $health = Invoke-RestMethod -Method Get -Uri "$lattice/health" -TimeoutSec 3
    Write-Stage 'Lattice bridge /health' 'PASS' "$($health.bridge) v$($health.version)"
} catch {
    Write-Stage 'Lattice bridge /health' 'FAIL' $_.Exception.Message
    Write-Host 'Start the NAI Lattice host (gastown-release.exe launch) and retry.' -ForegroundColor Red
    exit 1
}

$tokenPath = Join-Path $env:LOCALAPPDATA 'NAI\Lattice\v1\json-bridge.token'
if (-not (Test-Path -LiteralPath $tokenPath)) {
    Write-Stage 'Lattice bridge token' 'FAIL' $tokenPath
    exit 1
}
$token = (Get-Content -LiteralPath $tokenPath -Raw).Trim()
$headers = @{ Authorization = "Bearer $token" }

function Invoke-Lattice([string]$method, [hashtable]$payloadArgs) {
    $payload = @{ method = $method; producer = 'files-verify'; args = $payloadArgs } | ConvertTo-Json -Depth 8 -Compress
    $result = Invoke-RestMethod -Method Post -Uri "$lattice/dispatch" -Headers $headers `
        -ContentType 'application/json' -Body $payload -TimeoutSec 60
    if ($result.status -ne 0) { throw "$method failed: $($result.error)" }
    return $result.body
}

# --- 2. Resolve an embedding backend (LUSI first, Ollama fallback) -----------
$lusiBase = $lusiDefault
$lusiToken = $null
$discovery = Join-Path $env:LOCALAPPDATA 'VIVERRA\Toastovac\models.json'
if (Test-Path -LiteralPath $discovery) {
    try {
        $info = Get-Content -LiteralPath $discovery -Raw | ConvertFrom-Json
        if ($info.port) { $lusiBase = "http://127.0.0.1:$($info.port)" }
        if ($info.token) { $lusiToken = $info.token }
    } catch { }
}

$lusiHeaders = @{}
if ($lusiToken) { $lusiHeaders.Authorization = "Bearer $lusiToken" }

$backend = $null
try {
    $null = Invoke-RestMethod -Method Get -Uri "$lusiBase/v1/models" -Headers $lusiHeaders -TimeoutSec 5
    $backend = @{ Kind = 'lusi'; Base = $lusiBase; Token = $lusiToken; Model = $null }
    Write-Stage 'LUSI /v1/models' 'PASS' $lusiBase
} catch {
    Write-Stage 'LUSI /v1/models' 'SKIP' "$lusiBase — $($_.Exception.Message)"
}

if (-not $backend) {
    try {
        $models = Invoke-RestMethod -Method Get -Uri "$ollamaBase/models" -TimeoutSec 5
        $ids = @($models.data | ForEach-Object { $_.id })
        $model = $ids | Where-Object { $_ -match 'embed' } | Select-Object -First 1
        if (-not $model) { $model = $ids | Select-Object -First 1 }
        if (-not $model) { throw 'no models available' }
        $backend = @{ Kind = 'openai'; Base = $ollamaBase; Token = $null; Model = $model }
        Write-Stage 'Ollama /v1/models' 'PASS' "$ollamaBase (model=$model)"
    } catch {
        Write-Stage 'Ollama /v1/models' 'FAIL' $_.Exception.Message
        Write-Host 'Start the NAI OS runtime (LUSI) or Ollama and retry.' -ForegroundColor Red
        exit 1
    }
}

# --- 3. Embedding + dimension normalization ---------------------------------
try {
    if ($backend.Kind -eq 'lusi') {
        $body = @{ operation = 'index'; input = @('NAI Files semantic verification sentence') } | ConvertTo-Json -Depth 5 -Compress
        $embedded = Invoke-RestMethod -Method Post -Uri "$($backend.Base)/v1/embeddings" -Headers $lusiHeaders `
            -ContentType 'application/json' -Body $body -TimeoutSec 120
    } else {
        $body = @{ model = $backend.Model; input = @('NAI Files semantic verification sentence') } | ConvertTo-Json -Depth 5 -Compress
        $embedded = Invoke-RestMethod -Method Post -Uri "$($backend.Base)/embeddings" `
            -ContentType 'application/json' -Body $body -TimeoutSec 120
    }

    $raw = [float[]]@($embedded.data[0].embedding)
    Write-Stage 'Embeddings' 'PASS' "$($backend.Kind) dim=$($raw.Length)"

    $vector = New-Object 'float[]' $storeDim
    [Array]::Copy($raw, $vector, [Math]::Min($raw.Length, $storeDim))
    if ($raw.Length -lt $storeDim) {
        Write-Stage 'Dimension padding' 'PASS' "$($raw.Length) -> $storeDim (zero-pad, cosine-exact)"
    } elseif ($raw.Length -gt $storeDim) {
        Write-Stage 'Dimension truncation' 'PASS' "$($raw.Length) -> $storeDim"
    }
} catch {
    Write-Stage 'Embeddings' 'FAIL' $_.Exception.Message
    exit 1
}

# --- 4. Index -> query -> delete ---------------------------------------------
try {
    $null = Invoke-Lattice 'semantic.index' @{ id = $testId; vector = $vector; title = 'verify.txt'; body = 'verification'; namespace = 'files-verify' }
    Write-Stage 'Lattice semantic.index' 'PASS' $testId
} catch {
    Write-Stage 'Lattice semantic.index' 'FAIL' $_.Exception.Message
    exit 1
}

try {
    $query = Invoke-Lattice 'query.vector' @{ vector = $vector; limit = 3 }
    $hit = @($query.hits) | Where-Object { $_.id -eq $testId } | Select-Object -First 1
    if (-not $hit) { throw "expected hit $testId, got: $($query.hits | ConvertTo-Json -Compress)" }
    Write-Stage 'Lattice query.vector' 'PASS' "score=$([math]::Round([double]$hit.score, 4))"
} catch {
    Write-Stage 'Lattice query.vector' 'FAIL' $_.Exception.Message
    exit 1
}

try {
    $deleted = Invoke-Lattice 'semantic.delete' @{ id = $testId }
    $after = Invoke-Lattice 'query.vector' @{ vector = $vector; limit = 3 }
    $stillThere = @($after.hits) | Where-Object { $_.id -eq $testId }
    if ($stillThere) { throw "vector $testId still present after delete" }
    Write-Stage 'Lattice semantic.delete' 'PASS' "removed=$($deleted.removed)"
} catch {
    Write-Stage 'Lattice semantic.delete' 'FAIL' $_.Exception.Message
    exit 1
}

Write-Host ''
Write-Host ("PASS: Files semantic path works end-to-end via {0} + NAI Lattice." -f $backend.Kind) -ForegroundColor Green
exit 0
