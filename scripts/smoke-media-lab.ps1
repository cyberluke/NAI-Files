#!/usr/bin/env pwsh
# Smoke matrix for NAI Media Lab operations.
# Generates deterministic samples, runs each operation's exact FFmpeg args and reports PASS/FAIL.
# Mirrors MediaOperationRegistry; run it whenever the bundled FFmpeg or an operation changes.
#
#   pwsh -File scripts/smoke-media-lab.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$ff = Join-Path $root 'src/Files.App/ffmpeg/ffmpeg.exe'
if (-not (Test-Path -LiteralPath $ff)) { Write-Host "ffmpeg not found: $ff" -ForegroundColor Red; exit 1 }

$work = Join-Path $env:TEMP "media-lab-smoke"
if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
New-Item -ItemType Directory -Path $work | Out-Null

$video = Join-Path $work 'sample.mp4'
$audio = Join-Path $work 'sample.mp3'
$image = Join-Path $work 'sample.png'

Write-Host 'Generating samples...' -ForegroundColor Cyan
& $ff -y -hide_banner -loglevel error -f lavfi -i 'testsrc=duration=4:size=640x360:rate=30' `
    -f lavfi -i 'sine=frequency=440:duration=4' -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest $video
& $ff -y -hide_banner -loglevel error -f lavfi -i 'sine=frequency=440:duration=4' -c:a libmp3lame $audio
& $ff -y -hide_banner -loglevel error -f lavfi -i 'testsrc=duration=1:size=640x360' -frames:v 1 $image

$failures = 0

function Invoke-Produce([string]$id, [string]$inputPath, [string]$extension, [string[]]$ffargs) {
    $output = Join-Path $work ($id + $extension)
    $resolved = $ffargs | ForEach-Object { $_.Replace('{in}', $inputPath) }
    $log = & $ff @resolved $output 2>&1 | Out-String
    if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $output) -and (Get-Item -LiteralPath $output).Length -gt 0) {
        Write-Host ("[PASS] {0} ({1:N0} KB)" -f $id, ((Get-Item -LiteralPath $output).Length / 1KB)) -ForegroundColor Green
        return $true
    }
    Write-Host ("[FAIL] {0}" -f $id) -ForegroundColor Red
    Write-Host ($log.Substring([Math]::Max(0, $log.Length - 600))) -ForegroundColor DarkGray
    return $false
}

function Invoke-Analyze([string]$id, [string]$inputPath, [string[]]$ffargs, [string]$keyword) {
    $resolved = $ffargs | ForEach-Object { $_.Replace('{in}', $inputPath) }
    $log = & $ff @resolved 2>&1 | Out-String
    $errored = $log -match 'Error while filtering|No such filter|Invalid argument|Conversion failed'
    if ($LASTEXITCODE -eq 0 -and -not $errored) {
        $found = if ($keyword -and $log -match $keyword) { " (matched '$keyword')" } else { '' }
        Write-Host ("[PASS] {0}{1}" -f $id, $found) -ForegroundColor Green
        return $true
    }
    Write-Host ("[FAIL] {0} (no '{1}')" -f $id, $keyword) -ForegroundColor Red
    Write-Host ($log.Substring([Math]::Max(0, $log.Length - 600))) -ForegroundColor DarkGray
    return $false
}

$head = @('-y', '-hide_banner', '-nostdin', '-i', '{in}')

# ---- Video / Enhance ----
if (-not (Invoke-Produce 'video.60fps' $video '.mp4' (@($head + @('-t','2','-vf','minterpolate=fps=60:mi_mode=mci','-c:v','libx264','-crf','18','-preset','slow','-c:a','copy'))))) { $failures++ }
if (-not (Invoke-Produce 'video.denoise' $video '.mp4' (@($head + @('-vf','hqdn3d=4:3:6:4.5','-c:v','libx264','-crf','20','-c:a','copy'))))) { $failures++ }
if (-not (Invoke-Produce 'video.sharpen' $video '.mp4' (@($head + @('-vf','unsharp=5:5:1.0:5:5:0.0','-c:v','libx264','-crf','20','-c:a','copy'))))) { $failures++ }
if (-not (Invoke-Produce 'video.deinterlace' $video '.mp4' (@($head + @('-vf','bwdif','-c:v','libx264','-crf','20','-c:a','copy'))))) { $failures++ }
if (-not (Invoke-Produce 'video.hdr' $video '.mp4' (@($head + @('-vf','format=gbrpf32le,tonemap=hable:desat=0,format=yuv420p','-c:v','libx264','-crf','20','-c:a','copy'))))) { $failures++ }

# ---- Video / Create ----
if (-not (Invoke-Produce 'video.reverse' $video '.mp4' (@($head + @('-vf','reverse','-af','areverse','-c:v','libx264','-crf','20','-c:a','aac'))))) { $failures++ }
if (-not (Invoke-Produce 'video.speed2x' $video '.mp4' (@($head + @('-vf','setpts=0.5*PTS','-af','atempo=2.0','-c:v','libx264','-crf','20','-c:a','aac'))))) { $failures++ }
if (-not (Invoke-Produce 'video.slowmo' $video '.mp4' (@($head + @('-vf','setpts=2.0*PTS','-af','atempo=0.5','-c:v','libx264','-crf','20','-c:a','aac'))))) { $failures++ }
if (-not (Invoke-Produce 'video.contact' $video '.png' (@($head + @('-vf','fps=1/5,scale=320:-1,tile=5x4','-frames:v','1'))))) { $failures++ }

# ---- Video / Analyze ----
if (-not (Invoke-Analyze 'video.freezedetect' $video (@($head + @('-vf','freezedetect=n=-50dB:d=2','-an','-f','null','-'))) 'freezedetect')) { $failures++ }
if (-not (Invoke-Analyze 'video.scenedetect' $video (@($head + @('-vf',"select='gt(scene,0.4)',showinfo",'-an','-f','null','-'))) 'showinfo')) { $failures++ }
if (-not (Invoke-Analyze 'video.blackdetect' $video (@($head + @('-vf','blackdetect=d=0.5:pix_th=0.10','-an','-f','null','-'))) 'blackdetect')) { $failures++ }
if (-not (Invoke-Analyze 'video.silencedetect' $video (@($head + @('-af','silencedetect=n=-50dB:d=1','-vn','-f','null','-'))) 'silencedetect')) { $failures++ }

# ---- Audio / Enhance ----
if (-not (Invoke-Produce 'audio.loudnorm' $audio '.mp3' (@($head + @('-af','loudnorm=I=-16:TP=-1.5:LRA=11','-c:a','libmp3lame','-b:a','192k'))))) { $failures++ }
if (-not (Invoke-Produce 'audio.denoise' $audio '.mp3' (@($head + @('-af','afftdn=nf=-25','-c:a','libmp3lame','-b:a','192k'))))) { $failures++ }
if (-not (Invoke-Produce 'audio.compress' $audio '.mp3' (@($head + @('-af','acompressor=threshold=-18dB:ratio=3:attack=20:release=250','-c:a','libmp3lame','-b:a','192k'))))) { $failures++ }
if (-not (Invoke-Produce 'audio.limiter' $audio '.mp3' (@($head + @('-af','alimiter=limit=0.95','-c:a','libmp3lame','-b:a','192k'))))) { $failures++ }

# ---- Audio / Analyze ----
if (-not (Invoke-Analyze 'audio.silence' $audio (@($head + @('-af','silencedetect=n=-50dB:d=1','-vn','-f','null','-'))) 'silencedetect')) { $failures++ }
if (-not (Invoke-Analyze 'audio.loudness' $audio (@($head + @('-af','loudnorm=I=-16:TP=-1.5:LRA=11:print_format=json','-vn','-f','null','-'))) '"input_i"')) { $failures++ }

# ---- Image / Enhance ----
if (-not (Invoke-Produce 'image.upscale2x' $image '.png' (@($head + @('-vf','scale=iw*2:ih*2:flags=lanczos'))))) { $failures++ }
if (-not (Invoke-Produce 'image.sharpen' $image '.png' (@($head + @('-vf','unsharp=5:5:1.0:5:5:0.0'))))) { $failures++ }
if (-not (Invoke-Produce 'image.denoise' $image '.png' (@($head + @('-vf','hqdn3d=1.5:1.5:6:6'))))) { $failures++ }
if (-not (Invoke-Produce 'image.grayscale' $image '.png' (@($head + @('-vf','hue=s=0'))))) { $failures++ }

Write-Host ''
if ($failures -eq 0) {
    Write-Host 'PASS: every Media Lab operation produced a valid result.' -ForegroundColor Green
    exit 0
}

Write-Host ("FAIL: {0} operation(s) failed. Artifacts: {1}" -f $failures, $work) -ForegroundColor Red
exit 1
