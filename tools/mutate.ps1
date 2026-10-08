#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Breaks one property at a time and reports whether the tests notice.

.DESCRIPTION
    A test that passes says nothing about whether it would fail. Each mutation in
    tools/mutations.json disables exactly one property the suite claims to protect;
    a mutation nobody catches is an unguarded property, and this reports it as a
    failure rather than a curiosity.

    An entry marked "defensive" is expected to survive: its property is held by
    another guard, or is unreachable through the public API. Those are reported
    separately, so a designed-for outcome is not confused with a gap.

    Every mutation is reverted in a finally block, including when the build fails.
#>
param(
    [string]$Only
)

$ErrorActionPreference = "Continue"
[Console]::OutputEncoding = [Text.Encoding]::UTF8

# Written to a file as well as the host. Host output is not reliably captured in every
# runner, and a mutation report nobody can read is a report nobody can act on.
$script:report = Join-Path (Resolve-Path (Join-Path $PSScriptRoot "..")).Path "docs/mutation-last.txt"
Remove-Item $script:report -ErrorAction SilentlyContinue

function Say([string]$text) {
    Write-Host $text
    Add-Content -Path $script:report -Value $text -Encoding UTF8
}

# A script that dies silently is worse than one that fails loudly: this one is run with
# its host output often not captured, so an unhandled error would leave nothing behind.
trap {
    Say "UNHANDLED ERROR: $($_.Exception.GetType().Name): $($_.Exception.Message)"
    Say "  at line $($_.InvocationInfo.ScriptLineNumber)"
    exit 2
}

Say "Mutation run starting (only: '$Only')."

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$env:TEMP = Join-Path $root ".test-temp"
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Force -Path $env:TEMP | Out-Null

$smokeProject = Join-Path $root "tools/EriReborn.Tools.Smoke/EriReborn.Tools.Smoke.csproj"
$runnerDir = Join-Path $root "tools/EriReborn.Tools.Runner/bin/Debug/net8.0"
$runner = Join-Path $runnerDir "EriReborn.Tools.Runner.dll"

# Every suite a mutation could be guarded by. A platform mutation is guarded by the
# platform's tests, and only running the Core suite hid that.
$suites = @(
    [pscustomobject]@{
        Name     = "Core"
        Project  = Join-Path $root "tests/EriReborn.Core.Tests/EriReborn.Core.Tests.csproj"
        Source   = Join-Path $root "tests/EriReborn.Core.Tests"
        Assembly = Join-Path $root "tests/EriReborn.Core.Tests/bin/Debug/net8.0/EriReborn.Core.Tests.dll"
        Dir      = Join-Path $root "tests/EriReborn.Core.Tests/bin/Debug/net8.0"
    },
    [pscustomobject]@{
        Name     = "Windows"
        Project  = Join-Path $root "tests/EriReborn.Windows.Tests/EriReborn.Windows.Tests.csproj"
        Source   = Join-Path $root "tests/EriReborn.Windows.Tests"
        Assembly = Join-Path $root "tests/EriReborn.Windows.Tests/bin/Debug/net8.0-windows/EriReborn.Windows.Tests.dll"
        Dir      = Join-Path $root "tests/EriReborn.Windows.Tests/bin/Debug/net8.0-windows"
    }
)

# Which suites could even contain the filter's tests. Asking both when the answer is
# unknown is correct; asking both always is what made a run take tens of minutes.
function Select-Suites([string]$filter) {
    if ([string]::IsNullOrWhiteSpace($filter) -or $filter -eq "*") { return $suites }

    $matched = @()
    foreach ($suite in $suites) {
        $hit = Get-ChildItem $suite.Source -Recurse -File -Filter "*.cs" |
            Select-String -Pattern $filter -SimpleMatch -List
        if ($hit) { $matched += $suite }
    }

    if ($matched.Count -eq 0) { return $suites }
    return $matched
}

foreach ($suite in $suites) {
    Copy-Item "$runnerDir/EriReborn.Tools.Runner.dll" $suite.Dir -Force -ErrorAction SilentlyContinue
    Copy-Item "$runnerDir/EriReborn.Tools.Runner.runtimeconfig.json" $suite.Dir -Force -ErrorAction SilentlyContinue
}

function Lines([string]$text) { return ($text -split "\r?\n") }

$definitions = (Get-Content (Join-Path $PSScriptRoot "mutations.json") -Raw -Encoding UTF8 | ConvertFrom-Json).mutations
$survivors = New-Object System.Collections.Generic.List[string]
$defensive = New-Object System.Collections.Generic.List[string]

foreach ($mutation in $definitions) {
    if ($Only -and $mutation.name -notlike "*$Only*") { continue }

    $path = Join-Path $root $mutation.file
    $original = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)

    # Line endings must not decide whether a mutation applies. The anchors in mutations.json are
    # authored with LF, and a file can be saved with CRLF; that difference is an editing artefact,
    # not "the code moved". Both sides are therefore compared with LF endings, and the file is put
    # back verbatim in the finally block either way.
    $originalLf = $original.Replace("`r`n", "`n")
    $anchorLf = $mutation.old.Replace("`r`n", "`n")
    $hits = ([regex]::Matches($originalLf, [regex]::Escape($anchorLf))).Count

    if ($hits -ne 1) {
        # A mutation that no longer applies is also a failure: it means the code it
        # described has moved, and the property is not being probed at all.
        Say "$($mutation.name) | NOT APPLIED ($hits matches) - the code moved"
        $survivors.Add($mutation.name)
        continue
    }

    try {
        $mutated = $originalLf.Replace($anchorLf, $mutation.new.Replace("`r`n", "`n"))

        if ($mutation.alsoOld) {
            $alsoOldLf = $mutation.alsoOld.Replace("`r`n", "`n")
            $secondHits = ([regex]::Matches($mutated, [regex]::Escape($alsoOldLf))).Count
            if ($secondHits -ne 1) {
                Say "$($mutation.name) | SECOND EDIT NOT APPLIED ($secondHits matches)"
                $survivors.Add($mutation.name)
                continue
            }

            $mutated = $mutated.Replace($alsoOldLf, $mutation.alsoNew.Replace("`r`n", "`n"))
        }

        [System.IO.File]::WriteAllText($path, $mutated, (New-Object System.Text.UTF8Encoding($false)))

        # A mutant that does not compile must never be reported as "not caught": the
        # runner would then exercise the previous, unmutated DLL and call the
        # property unguarded. That is how a harness lies quietly.
        # Only the suites the filter can match: a mutation in a platform assembly is
        # guarded by the platform's tests, and an earlier version of this script only
        # ever built and ran the Core suite. It reported those mutations as unguarded,
        # which was the harness being wrong rather than the tests being weak.
        #
        # Building the whole solution instead was the other extreme: with one build per
        # mutation that is tens of minutes, which is past any single run's budget, and a
        # harness that cannot finish reports nothing at all.
        # A mutation can be guarded by the smoke suite rather than a unit test. Until this
        # branch existed that whole layer had no mutation coverage: its assertions could
        # fail, but nothing showed that they would.
        $isSmoke = ($mutation.suite -eq "smoke")

        if ($isSmoke) {
            $buildOut = dotnet build $smokeProject -c Debug --nologo 2>&1 | Out-String
        }
        else {
            $wanted = @(Select-Suites $mutation.filter)

            $buildOut = ""
            foreach ($suite in $wanted) {
                $buildOut += (dotnet build $suite.Project -c Debug --nologo 2>&1 | Out-String)
            }
        }

        $buildErrors = @(Lines $buildOut | Select-String -Pattern ': error' | ForEach-Object { $_.Line.Trim() })

        if ($buildErrors.Count -gt 0) {
            Say "$($mutation.name) | MUTANT DID NOT BUILD ($($buildErrors.Count) errors) - not a verdict on the tests"
            $buildErrors | Select-Object -First 2 | ForEach-Object { Say "    $_" }
            $survivors.Add("$($mutation.name) (mutant did not build)")
            continue
        }

        if ($isSmoke) {
            $smokeArgs = @()
            if ($mutation.smokeArgs) { $smokeArgs = @($mutation.smokeArgs) }

            $smokeOut = dotnet run --project $smokeProject -c Debug -- --user-data (Join-Path $root ".smoke-data") @smokeArgs 2>&1 | Out-String

            if ($smokeOut -notmatch 'SMOKE OK') {
                Say "$($mutation.name) | caught by the smoke suite"
            }
            elseif ($mutation.defensive) {
                Say "$($mutation.name) | defensive - held by another guard, as recorded"
                $defensive.Add($mutation.name)
            }
            else {
                Say "$($mutation.name) | NOT CAUGHT by the smoke suite - the property is unguarded"
                $survivors.Add($mutation.name)
            }

            continue
        }

        $failed = 0
        $total = 0
        $sawSummary = $false

        foreach ($suite in $wanted) {
            # "*" means the whole suite: the honest filter when the question is whether
            # any test at all covers the code, not whether a particular class does.
            $out = if ($mutation.filter -eq "*") {
                & dotnet $runner $suite.Assembly 2>&1 | Out-String
            }
            else {
                & dotnet $runner $suite.Assembly --filter $mutation.filter 2>&1 | Out-String
            }

            if ($out -match 'TOTAL passed=([0-9]+) failed=([0-9]+) skipped=[0-9]+ total=([0-9]+)') {
                $sawSummary = $true
                $failed += [int]$Matches[2]
                $total += [int]$Matches[3]
            }
        }

        if ($sawSummary -and $total -gt 0) {
            if ($failed -gt 0) {
                Say "$($mutation.name) | caught by $failed of $total"
            }
            elseif ($mutation.defensive) {
                Say "$($mutation.name) | defensive - held by another guard, as recorded"
                $defensive.Add($mutation.name)
            }
            else {
                Say "$($mutation.name) | NOT CAUGHT - the property is unguarded"
                $survivors.Add($mutation.name)
            }
        }
        else {
            Say "$($mutation.name) | the filter matched no tests in any suite"
            $survivors.Add("$($mutation.name) (no tests ran)")
        }
    }
    finally {
        [System.IO.File]::WriteAllText($path, $original, (New-Object System.Text.UTF8Encoding($false)))
    }
}

Say ""
if ($defensive.Count -gt 0) {
    Say "defensive (expected to survive): $($defensive.Count)"
}

if ($survivors.Count -eq 0) {
    Say "MUTATION RUN OK - every unguarded property is accounted for"
    exit 0
}

Say "MUTATION RUN FAILED - unguarded properties:"
$survivors | ForEach-Object { Say "  - $_" }
exit 1
