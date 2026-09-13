#Requires -Version 5.1
<#
  取元数据.ps1  --  build-time tool, ASCII-only on purpose.

  Reads a list of winget package IDs, runs `winget show` for each,
  parses the installer metadata (URL / SHA256 / type / arch / scope)
  and writes:
      <OutDir>\winget_meta.json
      <OutDir>\winget_meta.csv

  The CSV is the raw material for the hand-curated runtime manifest.
#>
[CmdletBinding()]
param(
    [string] $IdFile,
    [string] $OutDir = 'E:\安装系统\清单',
    [string[]] $Ids
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

if (-not $Ids -and $IdFile) {
    $Ids = Get-Content -LiteralPath $IdFile -Encoding UTF8 |
           ForEach-Object { $_.Trim() } |
           Where-Object { $_ -and -not $_.StartsWith('#') }
}
if (-not $Ids) { throw 'No package IDs supplied (use -Ids or -IdFile).' }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function Get-Match {
    param([string] $Text, [string[]] $Patterns)
    foreach ($p in $Patterns) {
        if ($Text -match $p) { return $Matches[1].Trim() }
    }
    return $null
}

# Parse one `winget show` blob into a flat object.
function ConvertFrom-WingetShow {
    param([string] $Text, [string] $Id)

    $lines = $Text -split "`r?`n"

    $o = [ordered]@{
        Id            = $Id
        Found         = $false
        Name          = $null
        Version       = $null
        Publisher     = $null
        PublisherUrl  = $null
        Homepage      = $null
        License       = $null
        Description   = $null
        Tags          = ''
        InstallerType = $null
        InstallerUrl  = $null
        Sha256        = $null
        Arch          = $null
        Scope         = $null
        Locale        = $null
        ReleaseDate   = $null
        OfflineOk     = $null
        UrlCount      = 0
        UrlsJson      = ''
    }

    # "Found OpenAL [CreativeTechnology.OpenAL]"  (localized: "已找到 ...")
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match "\[$([regex]::Escape($Id))\]") {
            $o.Found = $true
            $nm = $lines[$i] -replace "\[$([regex]::Escape($Id))\]", ''
            $nm = $nm -replace '^\s*(?:\u5df2\u627e\u5230|Found|\u627e\u5230)\s*', ''
            $o.Name = $nm.Trim()
            break
        }
    }
    if (-not $o.Found) { return [pscustomobject]$o }

    $o.Version      = Get-Match $Text @('(?m)^\s*(?:版本|Version)\s*[:：]\s*(.+?)\s*$')
    $o.Publisher    = Get-Match $Text @('(?m)^\s*(?:发布者|Publisher)\s*[:：]\s*(.+?)\s*$')
    $o.PublisherUrl = Get-Match $Text @('(?m)^\s*(?:发布服务器 URL|Publisher Url|Publisher URL)\s*[:：]\s*(\S+)')
    $o.Homepage     = Get-Match $Text @('(?m)^\s*(?:主页|Homepage)\s*[:：]\s*(\S+)')
    $o.License      = Get-Match $Text @('(?m)^\s*(?:许可证|License)\s*[:：]\s*(.+?)\s*$')
    $o.Description  = Get-Match $Text @('(?m)^\s*(?:描述|Description)\s*[:：]\s*(.+?)\s*$')
    $o.ReleaseDate  = Get-Match $Text @('(?m)^\s*(?:发布日期|Release Date)\s*[:：]\s*(\S+)')
    $o.OfflineOk    = Get-Match $Text @('(?m)^\s*(?:支持脱机分发|Supports offline distribution)\s*[:：]\s*(\S+)')

    # Tags block: "标记：" or "Tags:" followed by indented lines
    $tagBlock = [regex]::Match($Text, '(?ms)^\s*(?:标记|Tags)\s*[:：]\s*\r?\n((?:\s{2,}\S.*\r?\n?)+)')
    if ($tagBlock.Success) {
        $o.Tags = (($tagBlock.Groups[1].Value -split "`r?`n" |
                    ForEach-Object { $_.Trim() } |
                    Where-Object { $_ }) -join '|')
    }

    # Installer blocks. Each block starts at an "installer type" line.
    $blocks = New-Object System.Collections.ArrayList
    $cur = $null
    $typeRe = '(?:安装程序类型|Installer Type)\s*[:：]\s*(.+?)\s*$'
    foreach ($ln in $lines) {
        if ($ln -match $typeRe) {
            if ($cur) { [void]$blocks.Add($cur) }
            $cur = [ordered]@{ Type = $Matches[1]; Url = $null; Sha256 = $null; Arch = $null; Scope = $null; Locale = $null }
            continue
        }
        if (-not $cur) { continue }
        if ($ln -match '(?:安装程序 URL|Installer Url|Installer URL)\s*[:：]\s*(\S+)')            { $cur.Url    = $Matches[1] }
        elseif ($ln -match '(?:安装程序 SHA256|Installer SHA256)\s*[:：]\s*([0-9a-fA-F]{64})')    { $cur.Sha256 = $Matches[1].ToLower() }
        elseif ($ln -match '^\s*(?:架构|Architecture)\s*[:：]\s*(\S+)')                            { $cur.Arch   = $Matches[1] }
        elseif ($ln -match '^\s*(?:范围|Scope)\s*[:：]\s*(\S+)')                                   { $cur.Scope  = $Matches[1] }
        elseif ($ln -match '^\s*(?:安装程序区域设置|Installer Locale)\s*[:：]\s*(\S+)')            { $cur.Locale = $Matches[1] }
    }
    if ($cur) { [void]$blocks.Add($cur) }

    if ($blocks.Count -gt 0) {
        $o.UrlCount = $blocks.Count
        # Primary = first block that actually carries a URL.
        $primary = $blocks | Where-Object { $_.Url } | Select-Object -First 1
        if ($primary) {
            $o.InstallerType = $primary.Type
            $o.InstallerUrl  = $primary.Url
            $o.Sha256        = $primary.Sha256
            $o.Arch          = $primary.Arch
            $o.Scope         = $primary.Scope
            $o.Locale        = $primary.Locale
        }
        else {
            $o.InstallerType = $blocks[0].Type
        }
        $o.UrlsJson = ($blocks | ForEach-Object { [pscustomobject]$_ } | ConvertTo-Json -Compress -Depth 4)
    }

    return [pscustomobject]$o
}

$results = New-Object System.Collections.ArrayList
if (-not (Get-Command winget -ErrorAction SilentlyContinue)) { throw 'winget 不可用 (缺少 App Installer 或未在 PATH).' }
$n = 0
foreach ($id in $Ids) {
    $n++
    Write-Progress -Activity 'winget show' -Status "$n/$($Ids.Count)  $id" `
                   -PercentComplete ([int](100 * $n / $Ids.Count))
    try {
        $raw = & winget show --id $id --exact --accept-source-agreements 2>&1 | Out-String
    }
    catch {
        Write-Host ("  ERR  {0,-42} ({1})" -f $id, $_.Exception.Message) -ForegroundColor Red
        [void]$results.Add([pscustomobject]@{ Id = $id; Found = $false })
        continue
    }
    $obj = ConvertFrom-WingetShow -Text $raw -Id $id
    [void]$results.Add($obj)
    if ($obj.Found) {
        Write-Host ("  OK   {0,-42} {1,-18} {2}" -f $obj.Id, $obj.Version, $obj.InstallerType)
    }
    else {
        Write-Host ("  MISS {0,-42} (winget has no such package id)" -f $id) -ForegroundColor Yellow
    }
}
Write-Progress -Activity 'winget show' -Completed

$jsonPath = Join-Path $OutDir 'winget_meta.json'
$csvPath  = Join-Path $OutDir 'winget_meta.csv'

$results | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $jsonPath -Encoding UTF8
$results | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding UTF8

Write-Host ''
Write-Host ("Found {0}/{1}" -f ($results | Where-Object Found).Count, $results.Count)
Write-Host "  $jsonPath"
Write-Host "  $csvPath"
