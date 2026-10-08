<#
  Builds the Android target. The Android SDK location comes from ANDROID_HOME,
  falling back to the per-user default install location, so no machine-specific
  path is committed to the repository.
#>
param(
    [string]$Configuration = 'Debug',
    [ValidateSet('apk','aab')][string]$PackageFormat = 'apk'
)

$ErrorActionPreference = 'Stop'

if (-not $env:ANDROID_HOME) {
    $candidate = Join-Path $env:LOCALAPPDATA 'Android\Sdk'
    if (Test-Path $candidate) {
        $env:ANDROID_HOME = $candidate
    }
    else {
        throw 'Android SDK not found. Set ANDROID_HOME or install the Android SDK.'
    }
}

Write-Host "ANDROID_HOME = $env:ANDROID_HOME"

$project = Join-Path $PSScriptRoot 'src/EriReborn.Mobile/EriReborn.Mobile.csproj'
dotnet build $project -c $Configuration -f net8.0-android -p:AndroidPackageFormat=$PackageFormat

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$out = Join-Path $PSScriptRoot 'src/EriReborn.Mobile/bin'
Get-ChildItem -Path $out -Recurse -Include *.apk,*.aab | ForEach-Object { Write-Host $_.FullName }
