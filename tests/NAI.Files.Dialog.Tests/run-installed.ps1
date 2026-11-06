param([string]$ExpectedVersion, [ValidateSet("open", "save", "cancel")][string]$Mode = "open", [string]$InitialDirectory, [switch]$AllowDesktopUI)
$ErrorActionPreference = "Stop"
if (-not $AllowDesktopUI) { throw "This tracer opens desktop windows. Run only in an isolated desktop or with explicit approval and -AllowDesktopUI." }
$package = Get-AppxPackage -Name FilesDev
if (-not $package) { throw "FilesDev is not registered." }
if ($ExpectedVersion -and [string]$package.Version -ne $ExpectedVersion) {
    throw "Expected Files $ExpectedVersion, registered package is $($package.Version)."
}

$request = Join-Path ([IO.Path]::GetTempPath()) ("nai-dialog-" + [guid]::NewGuid().ToString("N") + ".txt")
$eventName = "Local\NAI.Files.Dialog." + [IO.Path]::GetFileName($request)
$signal = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::AutoReset, $eventName)
$process = $null
try {
    $arguments = "files-dev.exe -outputpath `"$request`" -mode $Mode -dialogtrace true"
    if ($InitialDirectory) { $arguments += " -directory `"$InitialDirectory`"" }
    $uri = "files-dev:?cmd=" + [Convert]::ToHexString([Text.Encoding]::UTF8.GetBytes($arguments))
    $activation = [Diagnostics.ProcessStartInfo]::new($uri)
    $activation.UseShellExecute = $true
    $activated = [Diagnostics.Process]::Start($activation)
    if ($activated) { $activated.Dispose() }
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while (-not [IO.File]::Exists($request + ".pid")) {
        if ([DateTime]::UtcNow -gt $deadline) { throw "Diagnostic process did not acknowledge startup: $request" }
        Start-Sleep -Milliseconds 20
    }
    $tracePid = [int][IO.File]::ReadAllText($request + ".pid")
    $process = [Diagnostics.Process]::GetProcessById($tracePid)
    # Pin the handle before completion. Querying by PID after exit can no longer recover its exit code.
    $null = $process.SafeHandle
    if (-not $signal.WaitOne(180000)) { throw "Backend diagnostic never completed. Trace: $request.trace" }
    if (-not $process.WaitForExit(30000)) { throw "Diagnostic process did not shut down after completion: $tracePid" }
    if ($null -eq $process.ExitCode) { throw "Diagnostic exit status could not be retrieved: $tracePid" }
    if ($process.ExitCode -ne 0) { throw "Files diagnostic exited abnormally: $($process.ExitCode)" }
    $trace = [IO.File]::ReadAllLines($request + ".trace")
    $trace | ForEach-Object { Write-Host $_ }
    if ($trace.Where({ $_.StartsWith("FAIL") }).Count -gt 0) { throw "Backend diagnostic assertion failed. Trace: $request.trace" }
    $results = [IO.File]::ReadAllLines($request)
    if ($Mode -eq "cancel") {
        if ($results.Count -ne 0) { throw "Cancellation returned a file selection." }
    } elseif ($results.Count -ne 1) { throw "Backend diagnostic did not return exactly one path." }
    elseif ($Mode -eq "save") {
        if ([IO.File]::Exists($results[0]) -or -not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($results[0]))) {
            throw "Save diagnostic did not return a new destination in an existing directory."
        }
    } elseif (-not [IO.File]::Exists($results[0])) { throw "Backend diagnostic returned a nonexistent file." }
    if ($trace.Where({ $_.StartsWith("PASS") }).Count -lt 12) { throw "Backend diagnostic did not run all action assertions." }
    [pscustomobject]@{
        Version = [string]$package.Version
        Mode = $Mode
        InstallLocation = $package.InstallLocation
        BackendTrace = $request + ".trace"
        Result = $results[0]
        Assertions = $trace.Where({ $_.StartsWith("PASS") }).Count
        ExitCode = $process.ExitCode
    } | ConvertTo-Json -Depth 3
} finally {
    if ($process) { $process.Dispose() }
    $signal.Dispose()
}
