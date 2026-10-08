<#
Runs the test suites without vstest.

On this machine vstest's testhost tries to watch its parent process and dies
with an access denial (CLR exception 0xe0434352) before a single test runs, so
the tests are loaded in-process by EriReborn.Tools.Runner instead.

Two things here are consequences of the sandbox and are not optional:

  * TEMP/TMP point inside the workspace, because a process started from this
    shell cannot write to the user temp directory;
  * the runner is copied next to the tests and started from there, because the
    tests resolve their fixtures relative to AppContext.BaseDirectory.

Windows PowerShell blocks unsigned scripts by default; run this with
"-ExecutionPolicy Bypass".
#>

param(
    [string] $Filter = "",
    [string] $Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = Split-Path -Parent $PSScriptRoot
$env:TEMP = Join-Path $root ".test-temp"
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Path $env:TEMP -Force | Out-Null

$runnerProject = Join-Path $root "tools\EriReborn.Tools.Runner\EriReborn.Tools.Runner.csproj"
$runnerOut = Join-Path $root "tools\EriReborn.Tools.Runner\bin\$Configuration\net8.0"

Write-Host "构建运行器…"
& dotnet build $runnerProject -c $Configuration --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "运行器构建失败" }

# Project, target framework and log are spelled out because the two suites do
# not share a framework: Core is net8.0, Windows is net8.0-windows.
$suites = @(
    [pscustomobject]@{
        Name = "Core"
        Project = "tests\EriReborn.Core.Tests\EriReborn.Core.Tests.csproj"
        Output = "tests\EriReborn.Core.Tests\bin\$Configuration\net8.0"
        Assembly = "EriReborn.Core.Tests.dll"
        Log = "docs\Environment_Smoke_Log.txt"
    },
    [pscustomobject]@{
        Name = "Windows"
        Project = "tests\EriReborn.Windows.Tests\EriReborn.Windows.Tests.csproj"
        Output = "tests\EriReborn.Windows.Tests\bin\$Configuration\net8.0-windows"
        Assembly = "EriReborn.Windows.Tests.dll"
        Log = "docs\Environment_Smoke_Log-Windows.txt"
    }
)

$failed = 0

foreach ($suite in $suites) {
    Write-Host ""
    Write-Host "===== $($suite.Name) ====="

    $project = Join-Path $root $suite.Project
    $outDir = Join-Path $root $suite.Output

    & dotnet build $project -c $Configuration --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "$($suite.Name) 构建失败" }

    Copy-Item (Join-Path $runnerOut "EriReborn.Tools.Runner.dll") $outDir -Force
    Copy-Item (Join-Path $runnerOut "EriReborn.Tools.Runner.runtimeconfig.json") $outDir -Force

    $arguments = @((Join-Path $outDir $suite.Assembly), "--log", (Join-Path $root $suite.Log))
    if ($Filter -ne "") { $arguments += @("--filter", $Filter) }

    & dotnet (Join-Path $outDir "EriReborn.Tools.Runner.dll") @arguments
    if ($LASTEXITCODE -ne 0) { $failed++ }
}

Write-Host ""
if ($failed -gt 0) {
    Write-Host "有 $failed 个套件存在失败。"
    exit 1
}

Write-Host "全部套件通过。"
exit 0
