#Requires -Version 5.1
<#
  _fix-bom.ps1  --  ASCII-only helper, no BOM required to run.

  Windows PowerShell 5.1 reads a BOM-less .ps1 as ANSI/GBK. Any script that
  contains non-ASCII text (Chinese comments, regex literals, string tables)
  then fails to parse -- and a parse error kills the WHOLE file, including
  statements that would otherwise have run.

  Usage:
      _fix-bom.ps1 -Path <file-or-dir> [-Recurse]
      _fix-bom.ps1 -Path 'E:\project\engine' -Recurse -Check

  Adds a UTF-8 BOM to every .ps1/.psm1/.psd1/.txt/.csv/.json that contains
  non-ASCII bytes and currently has no BOM. -Check only reports.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Path,
    [switch] $Recurse,
    [switch] $Check
)

$ErrorActionPreference = 'Stop'

$exts = @('.ps1', '.psm1', '.psd1', '.txt', '.csv', '.json')
$bom  = [byte[]](0xEF, 0xBB, 0xBF)

function Test-HasBom {
    param([string] $File)
    $fs = [IO.File]::OpenRead($File)
    try {
        if ($fs.Length -lt 3) { return $false }
        $b = New-Object byte[] 3
        [void]$fs.Read($b, 0, 3)
        return ($b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF)
    }
    finally { $fs.Dispose() }
}

function Test-HasNonAscii {
    param([string] $File)
    $fs = [IO.File]::OpenRead($File)
    try {
        $b = New-Object byte[] 8192
        while (($n = $fs.Read($b, 0, $b.Length)) -gt 0) {
            for ($i = 0; $i -lt $n; $i++) { if ($b[$i] -gt 0x7F) { return $true } }
        }
        return $false
    }
    finally { $fs.Dispose() }
}

$files = @()
if (Test-Path -LiteralPath $Path -PathType Container) {
    $files = Get-ChildItem -LiteralPath $Path -File -Recurse:$Recurse |
             Where-Object { $exts -contains $_.Extension.ToLower() }
}
else {
    $files = @(Get-Item -LiteralPath $Path)
}

$fixed = 0; $ok = 0; $skipped = 0
foreach ($f in $files) {
    if (-not (Test-HasNonAscii $f.FullName)) { $skipped++; continue }
    if (Test-HasBom $f.FullName) { $ok++; continue }

    if ($Check) {
        Write-Host ("  NEEDS-BOM  {0}" -f $f.FullName) -ForegroundColor Yellow
        $fixed++
        continue
    }

    $bytes = [IO.File]::ReadAllBytes($f.FullName)
    $out   = New-Object byte[] ($bytes.Length + 3)
    [Array]::Copy($bom, 0, $out, 0, 3)
    [Array]::Copy($bytes, 0, $out, 3, $bytes.Length)
    [IO.File]::WriteAllBytes($f.FullName, $out)
    Write-Host ("  + BOM      {0}" -f $f.FullName) -ForegroundColor Green
    $fixed++
}

Write-Host ''
if ($Check) { Write-Host "no-BOM files needing fix : $fixed" }
else        { Write-Host "added BOM                : $fixed" }
Write-Host "already had BOM          : $ok"
Write-Host "pure ASCII, untouched    : $skipped"
