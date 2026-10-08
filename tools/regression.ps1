#!/usr/bin/env pwsh
<#
.SYNOPSIS
    EriReborn regression: build, verify the test count, then run everything.

.DESCRIPTION
    The count cross-check is not ceremony. A round once reported a total that did
    not match its own source tree, because the runner was pointed at a test DLL
    nobody had rebuilt in that command. Counting the tests from the source and
    comparing it with what the runner discovered makes that impossible to miss.

.PARAMETER SkipSlow
    Leave out Android, publish and launch.

.PARAMETER SkipSmoke
    Leave out the smoke suite as well.
#>
param(
    [switch]$SkipSlow,
    [switch]$SkipSmoke
)

$ErrorActionPreference = "Continue"
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$script:report = Join-Path $root "docs/regression-last.txt"
$env:TEMP = Join-Path $root ".test-temp"
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Force -Path $env:TEMP | Out-Null
Remove-Item $script:report -ErrorAction SilentlyContinue

# The host stream, not the success stream: a function that returns a value and
# also writes with Write-Output returns its own log lines along with it, which
# silently turned a test count into a paragraph of text.
function Write-Line([string]$text) {
    Write-Host $text
    Add-Content -Path $script:report -Value $text -Encoding UTF8
}

$failures = New-Object System.Collections.Generic.List[string]

function Lines([string]$text) { return ($text -split "\r?\n") }

function Step([string]$name, [scriptblock]$body) {
    Write-Line ""
    Write-Line "===== $name ====="
    try { & $body }
    catch {
        $failures.Add("$name : $($_.Exception.Message)")
        Write-Line "  !! threw: $($_.Exception.Message)"
    }
}

function Build([string]$project, [string]$configuration) {
    $out = dotnet build $project -c $configuration --nologo 2>&1 | Out-String
    $errors = @(Lines $out | Select-String -Pattern ': error' | ForEach-Object { $_.Line.Trim() })
    $warnings = @(Lines $out | Select-String -Pattern ': warning' | ForEach-Object { $_.Line.Trim() })

    if ($errors.Count -gt 0) {
        Write-Line "  errors: $($errors.Count)"
        $errors | Select-Object -Unique -First 5 | ForEach-Object { Write-Line "    $_" }
        $failures.Add("$project ($configuration): $($errors.Count) errors")
    }

    if ($warnings.Count -gt 0) {
        Write-Line "  warnings: $($warnings.Count)"
        $warnings | Select-Object -Unique -First 5 | ForEach-Object { Write-Line "    $_" }
        $failures.Add("$project ($configuration): $($warnings.Count) warnings")
    }

    if ($errors.Count -eq 0 -and $warnings.Count -eq 0) { Write-Line "  0 warnings, 0 errors" }
}

function SourceTestCount([string]$directory) {
    $total = 0
    $files = Get-ChildItem $directory -Recurse -File -Filter "*.cs" |
        Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' }
    foreach ($file in $files) {
        $content = [System.IO.File]::ReadAllLines($file.FullName, [System.Text.Encoding]::UTF8)
        $total += @($content | Select-String -Pattern '^\s*\[Fact\]').Count
        $total += @($content | Select-String -Pattern '^\s*\[InlineData').Count
    }
    return $total
}

function RunSuite([string]$assembly, [string]$runnerDir, [string]$log) {
    $dir = Split-Path $assembly -Parent
    $runnerDll = Join-Path $dir "EriReborn.Tools.Runner.dll"
    Copy-Item (Join-Path $runnerDir "EriReborn.Tools.Runner.dll") $dir -Force -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $runnerDir "EriReborn.Tools.Runner.runtimeconfig.json") $dir -Force -ErrorAction SilentlyContinue

    $out = & dotnet $runnerDll $assembly --log $log 2>&1 | Out-String
    $lines = Lines $out

    # Parsed from the runner's ASCII TOTAL line, never from its localised summary:
    # matching human text means a script breaks whenever the two are decoded
    # differently, and it did.
    if ($out -match 'TOTAL passed=([0-9]+) failed=([0-9]+) skipped=([0-9]+) total=([0-9]+)') {
        $passed = [int]$Matches[1]
        $failed = [int]$Matches[2]
        $skipped = [int]$Matches[3]
        $total = [int]$Matches[4]

        Write-Line "  passed $passed / failed $failed / skipped $skipped / total $total"

        if ($failed -gt 0) {
            $failures.Add("$([System.IO.Path]::GetFileName($assembly)): $failed failing case(s)")
            @($lines | Select-String -Pattern '^FAIL' | ForEach-Object { $_.Line.Trim() }) |
                Select-Object -First 5 | ForEach-Object { Write-Line "    $_" }
        }

        return $total
    }

    Write-Line "  !! the runner produced no TOTAL line (exit $LASTEXITCODE, $($out.Length) chars)"
    @($lines | Where-Object { $_.Trim() -ne "" } | Select-Object -Last 4) |
        ForEach-Object { Write-Line "     $_" }
    $failures.Add("$([System.IO.Path]::GetFileName($assembly)): runner produced no TOTAL line")
    return -1
}

Get-Process EriReborn -ErrorAction SilentlyContinue | Stop-Process -Force

Step "Release build" { Build (Join-Path $root "EriReborn.sln") "Release" }

Step "Debug build (tests + runner, in this same command)" {
    Build (Join-Path $root "tests/EriReborn.Core.Tests/EriReborn.Core.Tests.csproj") "Debug"
    Build (Join-Path $root "tests/EriReborn.Windows.Tests/EriReborn.Windows.Tests.csproj") "Debug"
    Build (Join-Path $root "tools/EriReborn.Tools.Runner/EriReborn.Tools.Runner.csproj") "Debug"
}

$runnerDir = Join-Path $root "tools/EriReborn.Tools.Runner/bin/Debug/net8.0"
$coreOut = Join-Path $root "tests/EriReborn.Core.Tests/bin/Debug/net8.0"
$winOut = Join-Path $root "tests/EriReborn.Windows.Tests/bin/Debug/net8.0-windows"

$script:coreTotal = 0
Step "Core tests" {
    $script:coreTotal = RunSuite (Join-Path $coreOut "EriReborn.Core.Tests.dll") $runnerDir (Join-Path $root "docs/Environment_Smoke_Log.txt")
}

Step "Core count cross-check (source vs runner)" {
    $fromSource = SourceTestCount (Join-Path $root "tests/EriReborn.Core.Tests")
    Write-Line "  source $fromSource / runner $script:coreTotal"
    if ($fromSource -ne $script:coreTotal) {
        Write-Line "  !! mismatch: something ran a DLL that was not rebuilt, or a case was not discovered"
        $failures.Add("Core count mismatch: source $fromSource, runner $script:coreTotal")
    }
}

Step "Windows tests" {
    RunSuite (Join-Path $winOut "EriReborn.Windows.Tests.dll") $runnerDir (Join-Path $root "docs/Environment_Smoke_Log-Windows.txt") | Out-Null
}

if (-not $SkipSmoke) {
    Step "Smoke suite" {
        # Never commit or embed credentials in the regression harness. The smoke suite
        # reports SKIP when this optional key is unavailable.
        if (-not $env:ERIREBORN_AI_KEY) { Remove-Item Env:ERIREBORN_AI_KEY -ErrorAction SilentlyContinue }
        $out = dotnet run --project (Join-Path $root "tools/EriReborn.Tools.Smoke") -c Debug -- --user-data (Join-Path $root ".smoke-data") --suggest --msix --authenticode --catalog --ai --environment --pnp --realinstall --install --signature --extensions --marketplace --aria2 2>&1 | Out-String
        if ($out -match 'SMOKE OK') { Write-Line "  SMOKE OK" }
        else {
            Write-Line "  no SMOKE OK in the output"
            $failures.Add("smoke suite did not pass")
        }
    }
}

if (-not $SkipSlow) {
    Step "Android build" {
        $env:ANDROID_HOME = Join-Path $env:LOCALAPPDATA "Android/Sdk"
        $out = dotnet build (Join-Path $root "src/EriReborn.Mobile/EriReborn.Mobile.csproj") -c Debug -f net8.0-android --nologo 2>&1 | Out-String
        $errors = @(Lines $out | Select-String -Pattern ': error')
        $warnings = @(Lines $out | Select-String -Pattern ': warning')
        if ($errors.Count -gt 0 -or $warnings.Count -gt 0) {
            Write-Line "  errors $($errors.Count), warnings $($warnings.Count)"
            $failures.Add("Android build: $($errors.Count) errors, $($warnings.Count) warnings")
        }
        else { Write-Line "  0 warnings, 0 errors" }
    }

    Step "Publish and launch" {
        $publishOut = Join-Path $root "publish/win-x64"
        dotnet publish (Join-Path $root "src/EriReborn.Desktop/EriReborn.Desktop.csproj") -c Release -r win-x64 --self-contained false -o $publishOut --nologo | Out-Null

        Start-Process -FilePath (Join-Path $publishOut "EriReborn.exe")
        Start-Sleep -Seconds 14
        $proc = Get-Process EriReborn -ErrorAction SilentlyContinue
        if ($proc) {
            Write-Line "  APP RUNNING pid=$($proc.Id)"
            $proc | Stop-Process -Force
        }
        else {
            Write-Line "  APP NOT RUNNING"
            $failures.Add("the published app did not start")
        }
    }
}

Write-Line ""
if ($failures.Count -eq 0) {
    Write-Line "REGRESSION OK"
    exit 0
}

Write-Line "REGRESSION FAILED"
$failures | ForEach-Object { Write-Line "  - $_" }
exit 1
