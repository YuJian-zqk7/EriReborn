# =====================================================================
#  检测.ps1  --  runtime detection library (dot-source this)
#
#  Every probe returns a *fact*, never a guess. If a probe cannot decide,
#  it says so (Detected = $false) instead of reporting "missing" -- a
#  false "missing" would silently reinstall something already present.
#
#  Public entry point:  Test-RuntimeItem -Item <row from 运行库.csv>
# =====================================================================

$script:_ArpCache      = $null
$script:_DotnetCache   = $null
$script:_AppxCache     = $null
$script:_WebView2Cache = $null
$script:_VideoCache    = $null
$script:_SoundCache    = $null
$script:_PnpDrvCache   = $null

# --- Add/Remove Programs, both registry views -------------------------
function Get-ArpTable {
    if ($script:_ArpCache) { return $script:_ArpCache }

    $roots = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    $rows = New-Object System.Collections.ArrayList
    foreach ($r in $roots) {
        Get-ItemProperty -Path $r -ErrorAction SilentlyContinue |
            ForEach-Object {
                [void]$rows.Add([pscustomobject]@{
                    Name    = [string]$_.DisplayName
                    KeyName = [string]$_.PSChildName
                    Version = [string]$_.DisplayVersion
                    Pub     = [string]$_.Publisher
                    Key     = $_.PSChildName
                })
            }
    }
    $script:_ArpCache = $rows
    return $rows
}

# --- dotnet --list-runtimes -------------------------------------------
function Get-DotnetRuntimes {
    if ($script:_DotnetCache) { return $script:_DotnetCache }

    $rows = New-Object System.Collections.ArrayList
    $exe  = $null
    $cmd  = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $exe = $cmd.Source }
    elseif (Test-Path "$env:ProgramFiles\dotnet\dotnet.exe") { $exe = "$env:ProgramFiles\dotnet\dotnet.exe" }

    if ($exe) {
        foreach ($line in (& $exe --list-runtimes 2>&1)) {
            # Microsoft.WindowsDesktop.App 8.0.17 [C:\Program Files\dotnet\shared\...]
            if ($line -match '^\s*(\S+)\s+(\d+)\.(\d+)\.(\d+\S*)\s+\[') {
                [void]$rows.Add([pscustomobject]@{
                    Framework = $Matches[1]
                    Major     = [int]$Matches[2]
                    Version   = "$($Matches[2]).$($Matches[3]).$($Matches[4])"
                })
            }
        }
    }
    $script:_DotnetCache = $rows
    return $rows
}

# --- 显卡 / 声卡 / PnP 驱动 ----------------------------------------------
function Get-VideoControllers {
    if ($script:_VideoCache) { return $script:_VideoCache }
    $script:_VideoCache = @(Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)
    return $script:_VideoCache
}
function Get-SoundDevices {
    if ($script:_SoundCache) { return $script:_SoundCache }
    $script:_SoundCache = @(Get-CimInstance Win32_SoundDevice -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)
    return $script:_SoundCache
}
function Get-PnpDrivers {
    if ($script:_PnpDrvCache) { return $script:_PnpDrvCache }
    $script:_PnpDrvCache = @(Get-CimInstance Win32_PnPSignedDriver -ErrorAction SilentlyContinue |
                              Select-Object -ExpandProperty DeviceName | Where-Object { $_ })
    return $script:_PnpDrvCache
}

# --- Microsoft Edge WebView2 runtime ----------------------------------
function Get-WebView2 {
    if ($script:_WebView2Cache) { return $script:_WebView2Cache }

    $guid = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
    $keys = @(
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\$guid",
        "HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\$guid",
        "HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\$guid"
    )
    $ver = $null
    foreach ($k in $keys) {
        if (Test-Path $k) {
            $pv = (Get-ItemProperty $k -ErrorAction SilentlyContinue).pv
            if ($pv -and $pv -ne '0.0.0.0') { $ver = [string]$pv; break }
        }
    }
    # Fall back to the installed folder (per-user or per-machine).
    if (-not $ver) {
        foreach ($base in @("${env:ProgramFiles(x86)}\Microsoft\EdgeWebView\Application",
                            "$env:ProgramFiles\Microsoft\EdgeWebView\Application",
                            "$env:LOCALAPPDATA\Microsoft\EdgeWebView\Application")) {
            if (Test-Path $base) {
                $d = Get-ChildItem $base -Directory -ErrorAction SilentlyContinue |
                     Sort-Object { [version]($_.Name -replace '[^\d.]','') } -ErrorAction SilentlyContinue |
                     Select-Object -Last 1
                if ($d) { $ver = $d.Name; break }
            }
        }
    }
    $script:_WebView2Cache = $ver
    return $ver
}

# --- Appx / MSIX packages (one enumeration for all msix probes) --------
function Get-AppxNameTable {
    if ($script:_AppxCache) { return $script:_AppxCache }
    $names = New-Object System.Collections.ArrayList
    try {
        foreach ($p in Get-AppxPackage -ErrorAction SilentlyContinue) {
            [void]$names.Add([pscustomobject]@{ Name = $p.Name; Version = $p.Version; Full = $p.PackageFullName })
        }
    } catch { }
    $script:_AppxCache = $names
    return $names
}

function Reset-DetectionCache {
    $script:_ArpCache      = $null
    $script:_DotnetCache   = $null
    $script:_AppxCache     = $null
    $script:_WebView2Cache = $null
    $script:_VideoCache    = $null
    $script:_SoundCache    = $null
    $script:_PnpDrvCache   = $null
}

# 参数当正则用: 空参数绝不命中 (否则 -match '' 对任意文本都为真, 大量误判"已安装"),
# 非法正则降级为字面匹配, 不让一个写坏的检测参数崩掉整个脚本。
function _MatchParam {
    param([string]$Text, [string]$Pattern)
    if (-not $Pattern) { return $false }
    try { return [bool]($Text -match $Pattern) }
    catch { return ($Text -like "*$Pattern*") }
}
function Compare-Ver {
    param([string]$A, [string]$B)
    try {
        $va = ($A -replace '[^\d.]','').Trim('.')
        $vb = ($B -replace '[^\d.]','').Trim('.')
        if (-not $va) { return $false }
        return ([version]$va -ge [version]$vb)
    } catch { return $false }
}

# =====================================================================
#  Main probe. Returns:
#     Installed : bool   -- confirmed present
#     Detected  : bool   -- probe produced a definite answer
#     Version   : string -- detected version, if any
#     Evidence  : string -- what the probe actually saw (for the report)
# =====================================================================
function Test-RuntimeItem {
    param(
        [Parameter(Mandatory)] $Item
    )

    $r = [ordered]@{
        Id = $Item.Id; Name = $Item.名称; Category = $Item.类别; Tier = $Item.层级
        WingetId = $Item.wingetId
        Installed = $false; Detected = $false; Version = $null; Evidence = ''
        Method = $Item.检测方式
    }
    $method = $Item.检测方式
    $param  = $Item.检测参数
    $minVer = $Item.最低版本

    switch ($method) {

        'arp' {
            if (-not $param) { $r.Evidence = '未配置检测参数, 跳过 ARP 匹配'; break }
            $hit = Get-ArpTable | Where-Object { (_MatchParam $_.Name $param) -or (_MatchParam $_.KeyName $param) } | Select-Object -First 1
            if ($hit) {
                $r.Detected  = $true
                $shown = if ($hit.Name) { $hit.Name } else { $hit.KeyName }
                $r.Evidence  = "ARP 命中: $shown $($hit.Version)"
                if (-not $minVer -or (Compare-Ver $hit.Version $minVer)) {
                    $r.Installed = $true
                    $r.Version   = $hit.Version
                }
                else {
                    $r.Evidence += "  (低于最低要求 $minVer)"
                }
            }
            else {
                $r.Detected = $true          # ARP was enumerated; absence is meaningful
                $r.Evidence = 'ARP 中无匹配项'
            }
        }

        'regRelease' {
            if (-not $param) { $r.Evidence = '未配置检测参数'; break }
            if (Test-Path $param) {
                $p = Get-ItemProperty $param -ErrorAction SilentlyContinue
                $r.Detected = $true
                if ($p.Install -eq 1) {
                    $rel = [int]$p.Release
                    $r.Version  = [string]$p.Version
                    $r.Evidence = "注册表 Version=$($p.Version) Release=$rel"
                    if (-not $minVer -or $rel -ge [int]$minVer) { $r.Installed = $true }
                    else { $r.Evidence += "  (Release $rel < 要求 $minVer)" }
                }
                else {
                    $r.Evidence = '注册表存在但 Install != 1'
                }
            }
            else {
                $r.Detected = $true
                $r.Evidence = "注册表键不存在: $param"
            }
        }

        'regSubkey' {
            if (-not $param) { $r.Evidence = '未配置检测参数'; break }
            $parts = $param -split '\|', 2
            $key   = $parts[0]
            $want  = if ($parts.Count -gt 1) { $parts[1] } else { $null }
            if (Test-Path $key) {
                $r.Detected = $true
                $subs = @(Get-ChildItem $key -ErrorAction SilentlyContinue | Select-Object -ExpandProperty PSChildName)
                if ($want) {
                    $m = $subs | Where-Object { $_ -eq $want -or $_ -like "$want.*" }
                    if ($m) { $r.Installed = $true; $r.Version = ($m | Select-Object -First 1)
                              $r.Evidence = "注册表子键: $key\$($r.Version)" }
                    else    { $r.Evidence = "子键存在但无 '$want' (现有: $($subs -join ', '))" }
                }
                elseif ($subs.Count) { $r.Installed = $true; $r.Evidence = "子键存在: $($subs -join ', ')" }
                else { $r.Evidence = '键存在但为空' }
            }
            else {
                $r.Detected = $true
                $r.Evidence = "注册表键不存在: $key"
            }
        }

        'dotnet' {
            if (-not $param) { $r.Evidence = '未配置检测参数'; break }
            $parts = $param -split '\|', 2
            $fw    = $parts[0]
            $major = [int]$parts[1]
            $rt    = Get-DotnetRuntimes
            $r.Detected = $true
            if (-not $rt.Count) { $r.Evidence = 'dotnet 不可用或未安装任何运行时'; break }
            $hit = $rt | Where-Object { $_.Framework -eq $fw -and $_.Major -eq $major } | Select-Object -First 1
            if ($hit) {
                $r.Installed = $true
                $r.Version   = $hit.Version
                $r.Evidence  = "dotnet --list-runtimes: $($hit.Framework) $($hit.Version)"
            }
            else {
                $r.Evidence = "dotnet 中无 $fw $major.*"
            }
        }

        'video' {
            if (-not $param) { $r.Evidence = '未配置检测参数'; break }
            $r.Detected = $true
            $hit = Get-VideoControllers | Where-Object { _MatchParam $_ $param } | Select-Object -First 1
            if ($hit) { $r.Installed = $true; $r.Version = $hit; $r.Evidence = "显卡: $hit" }
            else { $r.Evidence = "无显卡匹配 '$param'" }
        }

        'sound' {
            if (-not $param) { $r.Evidence = '未配置检测参数'; break }
            $r.Detected = $true
            $hit = Get-SoundDevices | Where-Object { _MatchParam $_ $param } | Select-Object -First 1
            if ($hit) { $r.Installed = $true; $r.Version = $hit; $r.Evidence = "声卡: $hit" }
            else { $r.Evidence = "无声卡匹配 '$param'" }
        }

        'pnp' {
            if (-not $param) { $r.Evidence = '未配置检测参数'; break }
            $r.Detected = $true
            $hit = Get-PnpDrivers | Where-Object { _MatchParam $_ $param } | Select-Object -First 1
            if ($hit) { $r.Installed = $true; $r.Version = $hit; $r.Evidence = "驱动: $hit" }
            else { $r.Evidence = "无驱动匹配 '$param'" }
        }

        'webview2' {
            $v = Get-WebView2
            $r.Detected = $true
            if ($v) {
                $r.Installed = $true
                $r.Version   = $v
                $r.Evidence  = "WebView2 版本 $v"
                if ($minVer -and -not (Compare-Ver $v $minVer)) {
                    $r.Installed = $false
                    $r.Evidence += "  (低于 $minVer)"
                }
            }
            else { $r.Evidence = '未找到 WebView2 注册表项或安装目录' }
        }

        'file' {
            $r.Detected = $true
            $spec    = [string]$param
            $wantAll = $false
            if ($spec -like 'all:*') { $wantAll = $true; $spec = $spec.Substring(4) }
            $paths = @($spec -split '\|' | Where-Object { $_ })
            $found = @(); $lost = @()
            foreach ($rel in $paths) {
                if (Test-Path (Join-Path $env:WINDIR $rel)) { $found += $rel } else { $lost += $rel }
            }
            $ok = if ($wantAll) { $lost.Count -eq 0 } else { $found.Count -gt 0 }
            if ($ok) {
                $r.Installed = $true
                if ($wantAll) { $r.Evidence = "全部 $($found.Count) 个文件均在位" }
                else          { $r.Evidence = "文件存在: " + ($found -join ', ') }
            }
            else {
                $r.Evidence = "缺少 $($lost.Count)/$($paths.Count) 个: " + (($lost | Select-Object -First 4) -join ', ')
            }
        }

        'msix' {
            if (-not $param) { $r.Evidence = '未配置检测参数'; break }
            $r.Detected = $true
            $hit = Get-AppxNameTable | Where-Object { $_.Name -like "$param*" } | Select-Object -First 1
            if ($hit) {
                $r.Installed = $true
                $r.Version   = [string]$hit.Version
                $r.Evidence  = "Appx 包: $($hit.Full)"
            }
            else { $r.Evidence = "无 Appx 包匹配 '$param*'" }
        }

        'none' {
            $r.Detected  = $false
            $r.Evidence  = '该组件不做检测'
        }

        default {
            $r.Detected = $false
            $r.Evidence = "未知检测方式: $method"
        }
    }

    return [pscustomobject]$r
}

# --- Run the whole manifest -------------------------------------------
function Get-RuntimeReport {
    param([Parameter(Mandatory)][string] $ManifestPath)
    $items = Import-Csv -LiteralPath $ManifestPath -Encoding UTF8
    foreach ($it in $items) { Test-RuntimeItem -Item $it }
}
