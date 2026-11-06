param([switch]$SkipNativeBuild, [switch]$RegisteredOnly)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw "Visual C++/ATL build tools are required." }
Import-Module (Join-Path $vs "Common7\Tools\Microsoft.VisualStudio.DevShell.dll")
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments "-arch=x64 -host_arch=x64" | Out-Null
$msbuild = Join-Path $vs "MSBuild\Current\Bin\MSBuild.exe"
if (-not $SkipNativeBuild) {
    foreach ($kind in @("Open", "Save")) {
        $project = Join-Path $repo "src\Files.App.${kind}Dialog\Files.App.${kind}Dialog.vcxproj"
        & $msbuild $project -t:Build -p:Configuration=Release -p:Platform=x64 -m:1 -nodeReuse:false -nologo -v:minimal
        if ($LASTEXITCODE -ne 0) { throw "$kind dialog build failed" }
    }
}
$core = Join-Path $repo "tests\NAI.Files.Core.Tests\NAI.Files.Core.Tests.csproj"
& dotnet build $core -c Debug -p:Platform=x64 -v:quiet
if ($LASTEXITCODE -ne 0) { throw "Managed test worker build failed" }
$worker = Get-ChildItem (Join-Path $repo "artifacts") -Filter NAI.Files.Core.Tests.exe -File -Recurse |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $worker) {
    $worker = Get-ChildItem (Join-Path $repo "tests\NAI.Files.Core.Tests\bin") -Filter NAI.Files.Core.Tests.exe -File -Recurse |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
}
if (-not $worker) { throw "Managed worker executable not found" }
$output = Join-Path $repo "artifacts\tests\dialog"
New-Item -ItemType Directory -Force -Path $output | Out-Null
foreach ($kind in @("Open", "Save")) {
    $defines = @("/DUNICODE", "/D_UNICODE")
    if ($kind -eq "Save") { $defines += "/DTEST_SAVE" }
    $exe = Join-Path $output "${kind}Trace.exe"
    & cl.exe /nologo /std:c++17 /EHsc /MD /utf-8 @defines "/Fe:$exe" "/Fo:$output\${kind}Trace.obj" `
        (Join-Path $PSScriptRoot "native-trace.cpp") /link ole32.lib oleaut32.lib shell32.lib uuid.lib user32.lib advapi32.lib shlwapi.lib
    if ($LASTEXITCODE -ne 0) { throw "$kind native tracer compile failed" }
    if ($RegisteredOnly) { & $exe --registered }
    else { & $exe $worker.FullName }
    if ($LASTEXITCODE -ne 0) { throw "$kind native tracer failed" }
}
