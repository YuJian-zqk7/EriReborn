#Requires -Version 5.1
<#
  驱动安装.ps1  --  跨设备驱动与外设

  四档来源:
    1. Windows Update 驱动   自动安装   按本机设备 ID 匹配, 不含任何厂商硬编码
    2. 厂商官方驱动页         检测+直达  按 PCI 厂商号匹配
    3. OEM 整机厂支持         检测+直达  按 厂商+型号 匹配
    4. 外设配套软件           winget    按硬件厂商名匹配

  安全设计: 固件(BIOS/EC)更新默认排除。刷固件中途断电会变砖, 不能由
  脚本替用户决定。要装必须显式加 -IncludeFirmware。

  用法:
    驱动安装.ps1 -Report              只检测并列出计划 (默认)
    驱动安装.ps1 -Yes                 安装 WU 驱动更新 (不含固件)
    驱动安装.ps1 -Yes -IncludeFirmware 连固件一起 (有风险)
    驱动安装.ps1 -Peripherals         安装匹配到的外设配套软件
    驱动安装.ps1 -SkipWu              跳过耗时的 WU 搜索
#>
[CmdletBinding()]
param(
    [string] $SourceCsv  = 'E:\安装系统\清单\驱动源.csv',
    [string] $PeriphCsv  = 'E:\安装系统\清单\外设软件.csv',
    [string] $LogDir     = 'E:\安装系统\日志',
    [string] $LibDir     = 'E:\安装系统\引擎\库',
    [string[]] $Only,
    [string[]] $Skip,
    [switch] $Report,
    [switch] $Yes,
    [switch] $IncludeFirmware,
    [switch] $Peripherals,
    [switch] $SkipWu
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $LogDir)) { New-Item -ItemType Directory -Force -Path $LogDir | Out-Null }
$script:LogFile = Join-Path $LogDir ("驱动_{0:yyyyMMdd_HHmmss}.log" -f (Get-Date))
. (Join-Path $LibDir '日志.ps1')
Limit-LogRetention -Dir $LogDir

function Write-Log {
    param([string]$Message, [string]$Level = 'INFO')
    $line = '[{0:HH:mm:ss}][{1}] {2}' -f (Get-Date), $Level, $Message
    switch ($Level) {
        'OK'   { Write-Host $line -ForegroundColor Green }
        'WARN' { Write-Host $line -ForegroundColor Yellow }
        'ERR'  { Write-Host $line -ForegroundColor Red }
        'HEAD' { Write-Host ''; Write-Host $line -ForegroundColor Cyan }
        default { Write-Host $line }
    }
    Add-Content -LiteralPath $script:LogFile -Value $line -Encoding UTF8
}

. (Join-Path $LibDir '硬件.ps1')

# =====================================================================
#  1. 硬件盘点
# =====================================================================
Write-Log '扫描本机硬件 ...' 'HEAD'
$hw = Get-HardwareInventory
$oem = Get-OemSupportUrl
Write-Log "整机: $($oem.Manufacturer) $($oem.Model)   共 $($hw.Count) 个设备"

Write-Host ''
Write-Host '  厂商分布' -ForegroundColor Cyan
Get-HardwareSummary | ForEach-Object {
    Write-Host ("    {0,-20} {1,3} 个   {2}" -f $_.VendorName, $_.Count, $_.Classes)
}

# =====================================================================
#  2. 缺驱动的设备
# =====================================================================
$problem = @(Get-ProblemDevice)
$realProblem = @($problem | Where-Object { -not $_.IsPhantom })
$phantom     = @($problem | Where-Object { $_.IsPhantom })

Write-Log '缺驱动设备' 'HEAD'
if (-not $problem.Count) { Write-Log '  无' 'OK' }
else {
    if ($realProblem.Count) {
        foreach ($d in $realProblem) {
            Write-Log "  [真实硬件] $($d.状态说明): $(if($d.Name){$d.Name}else{$d.InstanceId})" 'WARN'
            Write-Log "      厂商=$($d.VendorName)  VID=$($d.VendorId)  DEV=$($d.DevId)"
        }
    }
    else { Write-Log '  真实硬件无缺驱动' 'OK' }
    if ($phantom.Count) {
        Write-Log "  另有 $($phantom.Count) 个 ROOT\ 虚拟/残留节点缺驱动 (无害, 通常是卸载残留)" 'WARN'
        foreach ($d in $phantom) { Write-Log "      $($d.InstanceId)" }
    }
}

# =====================================================================
#  3. 厂商驱动源匹配
# =====================================================================
$src = Import-Csv -LiteralPath $SourceCsv -Encoding UTF8
$vendorsPresent = @($hw | Where-Object { $_.VendorName } |
                    Select-Object -ExpandProperty VendorName -Unique)

$matched = New-Object System.Collections.ArrayList
foreach ($s in $src) {
    if ($Only -and $s.Id -notin $Only) { continue }
    if ($Skip -and $s.Id -in $Skip)    { continue }
    $hit = switch ($s.匹配类型) {
        '厂商'   { $vendorsPresent -contains $s.匹配值 }
        '整机厂' { "$($oem.Manufacturer)" -like "*$($s.匹配值)*" }
        '内置'   { $true }
        default  { $false }
    }
    if ($hit) {
        [void]$matched.Add([pscustomobject]@{
            Id=$s.Id; 名称=$s.名称; 类别=$s.类别; 来源类型=$s.来源类型
            地址=$s.地址; 自动=($s.自动 -eq '是'); 说明=$s.说明; 匹配值=$s.匹配值
        })
    }
}

Write-Log '匹配到的驱动来源' 'HEAD'
Write-Host ("  {0,-34} {1,-14} {2,-8} {3}" -f '名称','类别','自动化','地址') -ForegroundColor Cyan
Write-Host ('  ' + '-' * 108)
foreach ($m in $matched) {
    Write-Host ("  {0,-34} {1,-14} {2,-8} {3}" -f $m.名称, $m.类别,
                $(if ($m.自动) { '可自动' } else { '手动' }), $m.地址)
}

# =====================================================================
#  4. Windows Update 驱动
# =====================================================================
$wuAll = @(); $wuDriver = @(); $wuFirmware = @()
if (-not $SkipWu) {
    Write-Log '查询 Windows Update 驱动更新 (约 1~2 分钟) ...' 'HEAD'
    try {
        $session = New-Object -ComObject Microsoft.Update.Session
        $searcher = $session.CreateUpdateSearcher()
        $searcher.ServerSelection = 0
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $result = $searcher.Search("IsInstalled=0 and Type='Driver'")
        $sw.Stop()
        Write-Log ("  耗时 {0} 秒, 找到 {1} 项" -f [int]$sw.Elapsed.TotalSeconds, $result.Updates.Count)

        foreach ($u in $result.Updates) {
            $isFw = ($u.Title -match 'Firmware|BIOS|UEFI|EC Firmware') -or ("$($u.DriverClass)" -match 'Firmware')
            $row = [pscustomobject]@{
                Title   = $u.Title
                KB      = $(if ($u.KBArticleIDs.Count) { "KB$($u.KBArticleIDs.Item(0))" } else { '' })
                Class   = "$($u.DriverClass)"
                Mfr     = "$($u.DriverManufacturer)"
                Date    = $u.DriverVerDate
                IsFw    = $isFw
                Identity = $u.Identity.UpdateID
                Com     = $u
            }
            $wuAll += $row
            if ($isFw) { $wuFirmware += $row } else { $wuDriver += $row }
        }

        Write-Host ''
        Write-Host ("  {0,-10} {1,-58} {2}" -f '类型','标题','厂商') -ForegroundColor Cyan
        Write-Host ('  ' + '-' * 110)
        foreach ($r in $wuFirmware) {
            Write-Host ("  {0,-10} {1,-58} {2}" -f '固件*', $r.Title, $r.Mfr) -ForegroundColor Yellow
        }
        foreach ($r in $wuDriver) {
            Write-Host ("  {0,-10} {1,-58} {2}" -f '驱动', $r.Title, $r.Mfr)
        }
        if ($wuFirmware.Count) {
            Write-Log "  * 固件更新 $($wuFirmware.Count) 项默认不装 —— 刷固件断电会变砖。要装请加 -IncludeFirmware" 'WARN'
        }
    }
    catch {
        Write-Log "  WU 查询失败: $($_.Exception.Message)" 'ERR'
        Write-Log '  可加 -SkipWu 跳过, 或检查 Windows Update 服务是否正常' 'WARN'
    }
}
else { Write-Log '已跳过 Windows Update 查询 (-SkipWu)' 'WARN' }

# =====================================================================
#  5. 外设配套软件匹配
# =====================================================================
$periph = Import-Csv -LiteralPath $PeriphCsv -Encoding UTF8
$periphHit = @($periph | Where-Object {
    ($_.匹配类型 -eq '厂商' -and $vendorsPresent -contains $_.匹配值)
})
if ($Peripherals) {
    # 手动类只有显式指定才算
    $manual = @($periph | Where-Object { $_.匹配类型 -eq '手动' -and ($Only -and $_.Id -in $Only) })
    $periphHit = @($periphHit) + $manual | Sort-Object Id -Unique
}

Write-Log '外设配套软件' 'HEAD'
if (-not $periphHit.Count) { Write-Log '  本机硬件厂商没有匹配到配套软件' }
else {
    Write-Host ("  {0,-32} {1,-38} {2}" -f '名称','wingetId','说明') -ForegroundColor Cyan
    Write-Host ('  ' + '-' * 108)
    foreach ($p in $periphHit) {
        Write-Host ("  {0,-32} {1,-38} {2}" -f $p.名称, $p.wingetId, $p.说明)
    }
}

# =====================================================================
#  计划
# =====================================================================
$todoWu = @($wuDriver)
if ($IncludeFirmware) { $todoWu = @($wuDriver) + @($wuFirmware) }
if ($Skip)  { $todoWu = $todoWu | Where-Object { $_.Title -notmatch ($Skip -join '|') } }
if ($Only)  { $todoWu = $todoWu | Where-Object { $_.Title -match ($Only -join '|') } }

Write-Host ''
Write-Log ("待装: WU 驱动 $($todoWu.Count) 项; 外设软件 $(if($Peripherals){$periphHit.Count}else{0}) 项")

if ($Report -or (-not $Yes -and -not $Peripherals)) {
    if (-not $Yes -and -not $Peripherals) {
        Write-Log '仅检测模式, 未做改动。要安装加 -Yes (驱动) 或 -Peripherals (外设软件)'
    }
    Write-Log "日志: $script:LogFile"
    return
}

# =====================================================================
#  6. 安装 WU 驱动
# =====================================================================
$verifyRows = New-Object System.Collections.ArrayList
if ($Yes -and $todoWu.Count) {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
               ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
    Write-Log '安装 WU 驱动需要管理员权限, 正在提权 ...' 'WARN'
    $ra = @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`"",'-Yes')
    if ($IncludeFirmware) { $ra += '-IncludeFirmware' }
    if ($Peripherals)     { $ra += '-Peripherals' }
    if ($SkipWu)          { $ra += '-SkipWu' }
    if ($Only) { $ra += @('-Only', "`"$($Only -join ',')`"") }
    if ($Skip) { $ra += @('-Skip', "`"$($Skip -join ',')`"") }
    Start-Process -FilePath (Get-Process -Id $PID).Path -Verb RunAs -Wait -ArgumentList $ra
    # 子进程写自己的时间戳日志, 回显最新一份
    $childLog = Get-ChildItem $LogDir -Filter '驱动_*.log' -File -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($childLog) {
        Get-Content $childLog.FullName -Encoding UTF8 | ForEach-Object { Write-Host $_ }
        Write-Log "提权执行完毕, 子进程日志: $($childLog.FullName)"
    }
    else { Write-Log '提权执行完毕 (未找到子进程日志)' 'WARN' }
    return
    }
    else {
        Write-Log '开始安装 Windows Update 驱动' 'HEAD'
        try {
            $session = New-Object -ComObject Microsoft.Update.Session
            $coll = New-Object -ComObject Microsoft.Update.UpdateColl
            foreach ($r in $todoWu) {
                if (-not $r.Com.EulaAccepted) { $r.Com.AcceptEula() }
                [void]$coll.Add($r.Com)
                Write-Log "  加入: $($r.Title)"
            }
            Write-Log '  下载中 (可能需要几分钟) ...'
            $dl = $session.CreateUpdateDownloader()
            $dl.Updates = $coll
            $dlr = $dl.Download()
            Write-Log "  下载结果码 $($dlr.ResultCode)  (2=成功 3=部分成功 4=失败)"

            Write-Log '  安装中 ...'
            $inst = $session.CreateUpdateInstaller()
            $inst.Updates = $coll
            $ir = $inst.Install()
            Write-Log "  安装结果码 $($ir.ResultCode)  需重启=$($ir.RebootRequired)"

            for ($i = 0; $i -lt $coll.Count; $i++) {
                $one = $ir.GetUpdateResult($i)
                $title = $todoWu[$i].Title
                $ok = ($one.ResultCode -eq 2)
                [void]$verifyRows.Add([pscustomobject]@{
                    名称 = $title; 来源 = 'Windows Update'
                    结果 = $(switch ($one.ResultCode) { 2 {'成功'} 3 {'部分成功'} 4 {'失败'} 5 {'已放弃'} default {"码$($one.ResultCode)"} })
                    成功 = $ok; 说明 = "$($todoWu[$i].Mfr) $($todoWu[$i].Kb)"
                })
                if ($ok) { Write-Log "  OK   $title" 'OK' } else { Write-Log "  失败 $title (码 $($one.ResultCode))" 'ERR' }
            }
        }
        catch {
            Write-Log "  WU 安装异常: $($_.Exception.Message)" 'ERR'
        }
    }
}

# =====================================================================
#  7. 安装外设软件
# =====================================================================
if ($Peripherals -and $periphHit.Count) {
    Write-Log '安装外设配套软件' 'HEAD'
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        Write-Log 'winget 不可用 (缺少 App Installer), 跳过外设软件安装' 'ERR'
    }
    else {
    $n = 0
    foreach ($p in $periphHit) {
        $n++
        Write-Log "[$n/$($periphHit.Count)] $($p.名称)  ($($p.wingetId))"
        try {
        $out = & winget install --id $p.wingetId --exact --source winget --silent `
                   --accept-package-agreements --accept-source-agreements --disable-interactivity 2>&1 | Out-String
        $code = $LASTEXITCODE
        foreach ($ln in ($out -split "`r?`n" | Where-Object { $_.Trim() })) { Add-Content $script:LogFile $ln -Encoding UTF8 }

        # 复检: 让 winget 自己回答装没装上
        $chk = & winget list --id $p.wingetId --exact --accept-source-agreements 2>&1 | Out-String
        $ok = ($chk -match [regex]::Escape($p.wingetId))
        } catch {
            $code = -1; $ok = $false
            Write-Log "  winget 调用异常: $($_.Exception.Message)" 'ERR'
        }
        [void]$verifyRows.Add([pscustomobject]@{
            名称 = $p.名称; 来源 = 'winget'
            结果 = $(if ($ok) { '成功' } else { "未通过复检(码 $code)" })
            成功 = $ok; 说明 = $p.wingetId
        })
        if ($ok) { Write-Log "  OK   $($p.名称) 已装上" 'OK' }
        else     { Write-Log "  未通过复检: $($p.名称)" 'ERR' }
    }
    }
}

# =====================================================================
#  8. 复检与报告
# =====================================================================
Write-Log '复检' 'HEAD'
if (-not $SkipWu) {
    try {
        $s2 = New-Object -ComObject Microsoft.Update.Session
        $q2 = $s2.CreateUpdateSearcher()
        $q2.ServerSelection = 0
        $r2 = $q2.Search("IsInstalled=0 and Type='Driver'")
        Write-Log "  复检: 仍有 $($r2.Updates.Count) 项待装驱动更新 (装前 $($wuAll.Count) 项)"
    }
    catch { Write-Log "  复检查询失败: $($_.Exception.Message)" 'WARN' }
}

if ($verifyRows.Count) {
    Write-Host ''
    Write-Host ("  {0,-56} {1,-10} {2}" -f '名称','结果','说明') -ForegroundColor Cyan
    Write-Host ('  ' + '-' * 100)
    foreach ($v in $verifyRows) {
        $col = if ($v.成功) { 'Green' } else { 'Red' }
        Write-Host ("  {0,-56} {1,-10} {2}" -f $v.名称, $v.结果, $v.说明) -ForegroundColor $col
    }
    $okc = @($verifyRows | Where-Object 成功).Count
    $fail = @($verifyRows | Where-Object { -not $_.成功 }).Count
    Write-Log "$okc 项成功, $fail 项未通过" $(if ($fail) { 'WARN' } else { 'OK' })

    $rp = Join-Path (Split-Path $LogDir -Parent) ("报告\驱动安装_{0:yyyyMMdd_HHmmss}.csv" -f (Get-Date))
    $verifyRows | Export-Csv -LiteralPath $rp -NoTypeInformation -Encoding UTF8
    Write-Log "报告: $rp"
}
Write-Log "日志: $script:LogFile"
