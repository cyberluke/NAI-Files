#!/usr/bin/env pwsh
# Verifies the Files semantic path end-to-end:
#   LUSI /v1/embeddings  ->  NAI Lattice semantic.index_batch  ->  query.vector  ->  semantic.delete
#
# Run after the NAI OS runtime (LUSI) and the NAI Lattice host are up:
#   pwsh -File scripts/verify-nai-semantic.ps1
#
# Exit codes: 0 = PASS, 1 = FAIL (prints the exact failing stage).

$ErrorActionPreference = 'Stop'
$lattice = 'http://127.0.0.1:8789'
$lusiDefault = 'http://127.0.0.1:27130'
$expectedDim = 1024
$testId = 'files-verify-' + [guid]::NewGuid().ToString('N').Substring(0, 12)
$failures = @()

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

function Invoke-Lattice([string]$method, [hashtable]$args) {
    $payload = @{ method = $method; producer = 'files-verify'; args = $args } | ConvertTo-Json -Depth 8 -Compress
    $result = Invoke-RestMethod -Method Post -Uri "$lattice/dispatch" -Headers $headers `
        -ContentType 'application/json' -Body $payload -TimeoutSec 60
    if ($result.status -ne 0) { throw "$method failed: $($result.error)" }
    return $result.body
}

# --- 2. LUSI discovery + models ---------------------------------------------
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
try {
    $models = Invoke-RestMethod -Method Get -Uri "$lusiBase/v1/models" -Headers $lusiHeaders -TimeoutSec 5
    $hasEmbeddings = $false
    if ($models.capabilities -and $models.capabilities.PSObject.Properties.Name -contains 'embeddings') {
        $hasEmbeddings = $true
    }
    Write-Stage 'LUSI /v1/models' 'PASS' $(if ($hasEmbeddings) { 'embeddings lane present' } else { 'reachable (embeddings lane not advertised)' })
} catch {
    Write-Stage 'LUSI /v1/models' 'FAIL' "$lusiBase — $($_.Exception.Message)"
    Write-Host 'Start the NAI OS runtime (LUSI) and retry.' -ForegroundColor Red
    exit 1
}

# --- 3. Embedding ------------------------------------------------------------
try {
    $embedBody = @{ operation = 'index'; input = @('NAI Files semantic verification sentence') } |
        ConvertTo-Json -Depth 5 -Compress
    $embedded = Invoke-RestMethod -Method Post -Uri "$lusiBase/v1/embeddings" -Headers $lusiHeaders `
        -ContentType 'application/json' -Body $embedBody -TimeoutSec 120
    $vector = @($embedded.data[0].embedding)
    Write-Stage 'LUSI /v1/embeddings' 'PASS' "dim=$($vector.Count)"
    if ($vector.Count -ne $expectedDim) {
        Write-Stage 'Embedding dimension' 'FAIL' "expected $expectedDim, got $($vector.Count) (Lattice store is $expectedDim)"
        exit 1
    }
} catch {
    Write-Stage 'LUSI /v1/embeddings' 'FAIL' $_.Exception.Message
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
Write-Host 'PASS: Files semantic path works end-to-end (LUSI embeddings + NAI Lattice vectors).' -ForegroundColor Green
exit 0
