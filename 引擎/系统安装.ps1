#Requires -Version 5.1
<#
  系统安装.ps1  --  cross-device runtime installer

  数据驱动: 清单\运行库.csv  ─┐
           清单\winget_meta.json ─┴─> 检测 ─> 计划 ─> 安装 ─> 复检 ─> 报告

  用法:
    -Report                     只检测并列出当前状态 (默认动作, 不改动系统)
    -List                       列出全部条目
    -Tier 核心,推荐             选择要安装的层级 (默认 核心,推荐)
    -Only id1,id2               只处理指定条目
    -Skip id1,id2               排除指定条目
    -Cache                      只下载到缓存目录, 不安装
    -Offline                    只用缓存, 不联网
    -Yes                        不再确认直接执行
    -Force                      连已安装的也重装

  设计要点:
    * 成败以"安装后复检"为准, 不以安装器退出码为准 —— 退出码在不同
      winget 版本和不同组件上含义不一致, 复检才是事实。
    * winget 为主源 (自动取最新版 / 自动选架构 / 自动写卸载项),
      清单里带直链的条目走直链 + SHA256 校验 + 静默参数。
    * 幂等: 重复运行只会补装缺失项。
#>
[CmdletBinding()]
param(
    [string]   $Manifest = 'E:\安装系统\清单\运行库.csv',
    [string]   $MetaFile = 'E:\安装系统\清单\winget_meta.json',
    [string]   $CacheDir = 'E:\安装系统\缓存',
    [string]   $LogDir   = 'E:\安装系统\日志',
    [string]   $LibDir   = 'E:\安装系统\引擎\库',
    [string[]] $Tier     = @('核心','推荐'),
    [string[]] $Only,
    [string[]] $Skip,
    [switch]   $Report,
    [switch]   $List,
    [switch]   $Cache,
    [switch]   $Offline,
    [switch]   $Yes,
    [switch]   $Force
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- 日志
if (-not (Test-Path $LogDir)) { New-Item -ItemType Directory -Force -Path $LogDir | Out-Null }
$script:LogFile = Join-Path $LogDir ("运行库_{0:yyyyMMdd_HHmmss}.log" -f (Get-Date))
. (Join-Path $LibDir '日志.ps1')
Limit-LogRetention -Dir $LogDir

function Write-Log {
    param([string]$Message, [string]$Level = 'INFO')
    $stamp = '{0:HH:mm:ss}' -f (Get-Date)
    $line  = "[$stamp][$Level] $Message"
    switch ($Level) {
        'OK'    { Write-Host $line -ForegroundColor Green }
        'WARN'  { Write-Host $line -ForegroundColor Yellow }
        'ERR'   { Write-Host $line -ForegroundColor Red }
        'HEAD'  { Write-Host ''; Write-Host $line -ForegroundColor Cyan }
        default { Write-Host $line }
    }
    Add-Content -LiteralPath $script:LogFile -Value $line -Encoding UTF8
}

# ---------------------------------------------------------------- 载入
. (Join-Path $LibDir '检测.ps1')

foreach ($p in @($Manifest, $MetaFile)) {
    if (-not (Test-Path $p)) { throw "缺少必要文件: $p" }
}
$items = Import-Csv -LiteralPath $Manifest -Encoding UTF8
$meta  = Get-Content -LiteralPath $MetaFile -Raw -Encoding UTF8 | ConvertFrom-Json

$metaById = @{}
foreach ($m in $meta) { $metaById[$m.Id] = $m }

# 把 winget 元数据关联到清单行; 清单里显式写了直链/SHA 的优先
foreach ($it in $items) {
    $m = if ($it.wingetId) { $metaById[$it.wingetId] } else { $null }
    $it | Add-Member -NotePropertyName 可用版本 -NotePropertyValue $(if ($m) { $m.Version } else { '' }) -Force
    $it | Add-Member -NotePropertyName 直链来源 -NotePropertyValue $(if ($it.直链) { '清单' } elseif ($m) { $m.InstallerUrl } else { '' }) -Force
    $it | Add-Member -NotePropertyName 有效SHA -NotePropertyValue $(if ($it.SHA256) { $it.SHA256 } elseif ($m) { $m.Sha256 } else { '' }) -Force
    $it | Add-Member -NotePropertyName 主源 -NotePropertyValue $(if ($it.直链) { '直链' } elseif ($it.wingetId) { 'winget' } else { '无' }) -Force
}

# ---------------------------------------------------------------- -List
if ($List) {
    Write-Host ''
    Write-Host ("{0,-20} {1,-30} {2,-6} {3,-8} {4,-9} {5}" -f 'Id','名称','层级','来源','可用版本','wingetId') -ForegroundColor Cyan
    Write-Host ('-' * 118)
    foreach ($it in $items) {
        Write-Host ("{0,-20} {1,-30} {2,-6} {3,-8} {4,-9} {5}" -f `
            $it.Id, $it.名称, $it.层级, $it.主源, $it.可用版本, $it.wingetId)
    }
    Write-Host ''
    Write-Host "共 $($items.Count) 项"
    return
}

# ---------------------------------------------------------------- 检测
Write-Log "载入清单 $($items.Count) 项" 
Write-Log "开始检测本机状态 ..."
$sw = [Diagnostics.Stopwatch]::StartNew()
$state = @{}
foreach ($it in $items) { $state[$it.Id] = Test-RuntimeItem -Item $it }
$sw.Stop()
Write-Log ("检测完成, 耗时 {0} ms" -f $sw.ElapsedMilliseconds) 

# ---------------------------------------------------------------- 计划
$selected = $items | Where-Object {
    if ($Only) { $_.Id -in $Only -or $_.名称 -in $Only }
    else       { $Tier -contains $_.层级 }
}
if ($Skip) { $selected = $selected | Where-Object { $_.Id -notin $Skip } }

$plan = @(foreach ($it in $selected) {
    $s = $state[$it.Id]
    $action = if ($Force)                            { '重装' }
              elseif (-not $s.Detected)              { '待装' }
              elseif ($s.Installed)                  { '已装' }
              else                                   { '待装' }
    [pscustomobject]@{
        Id = $it.Id; 名称 = $it.名称; 层级 = $it.层级; 主源 = $it.主源
        wingetId = $it.wingetId; 直链 = $it.直链来源; SHA = $it.有效SHA
        静默参数 = $it.静默参数; Action = $action; 现版 = $s.Version; 依据 = $s.Evidence
        Row = $it
    }
})

Write-Log '当前状态' 'HEAD'
Write-Host ("{0,-6} {1,-30} {2,-6} {3,-9} {4}" -f '层级','名称','来源','状态','依据/版本') -ForegroundColor Cyan
Write-Host ('-' * 112)
foreach ($p in $plan | Sort-Object @{E={@{'核心'=0;'推荐'=1;'按需'=2}[$_.层级]}}, 名称) {
    $col = switch ($p.Action) { '已装' {'DarkGray'} '重装' {'Yellow'} default {'White'} }
    Write-Host ("{0,-6} {1,-30} {2,-6} {3,-9} {4}" -f `
        $p.层级, $p.名称, $p.主源, $p.Action, `
        $(if ($p.Action -eq '已装') { $p.依据 } else { $p.Id })) -ForegroundColor $col
}

$todo = @($plan | Where-Object { $_.Action -ne '已装' })
Write-Host ''
Write-Log ("选定 $($plan.Count) 项, 其中需要安装 $($todo.Count) 项, 已装跳过 $(($plan.Count - $todo.Count)) 项")

if ($Report) {
    Write-Host ''
    Write-Log '仅检测模式 (-Report), 不做任何改动'
    Write-Log "日志: $script:LogFile"
    return
}
if (-not $todo.Count) {
    Write-Log '所有选定组件均已就位, 无需安装' 'OK'
    return
}

# ---------------------------------------------------------------- 提权
$isAdmin = ([Security.Principal.WindowsPrincipal] `
            [Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Log '当前未以管理员身份运行 —— winget 会自行请求提权, 直链安装器可能弹一次 UAC' 'WARN'
}
# ---------------------------------------------------------------- 下载
function Get-InstallerFile {
    param([string]$Url, [string]$Sha, [string]$Name)
    if (-not (Test-Path $CacheDir)) { New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null }
    $ext  = [IO.Path]::GetExtension(([uri]$Url).AbsolutePath)
    if (-not $ext) { $ext = '.exe' }
    $dest = Join-Path $CacheDir ($Name + $ext)

    if (Test-Path $dest) {
        if (-not $Sha) { Write-Log "  缓存命中 (无哈希可校验): $dest"; return $dest }
        if ((Get-FileHash $dest -Algorithm SHA256).Hash.ToLower() -eq $Sha.ToLower()) {
            Write-Log "  缓存命中且哈希一致: $dest"; return $dest
        }
        Write-Log "  缓存文件哈希不符, 重新下载" 'WARN'
        Remove-Item $dest -Force
    }
    if ($Offline) { throw "离线模式但缓存中没有 $Name ($dest)" }

    Write-Log "  下载 $Url"
    $tmp = "$dest.part"
    $ok = $false
    $prevProgress = $ProgressPreference
    for ($try = 1; $try -le 3 -and -not $ok; $try++) {
        try {
            $ProgressPreference = 'SilentlyContinue'
            Invoke-WebRequest -Uri $Url -OutFile $tmp -UseBasicParsing -TimeoutSec 900
            $ok = $true
        } catch {
            Write-Log "  第 $try 次下载失败: $($_.Exception.Message)" 'WARN'
            Start-Sleep -Seconds (3 * $try)
        } finally {
            $ProgressPreference = $prevProgress
        }
    }
    if (-not $ok) {
        Remove-Item $tmp -Force -ErrorAction SilentlyContinue
        throw "下载失败: $Url"
    }
    Move-Item $tmp $dest -Force

    if ($Sha) {
        $got = (Get-FileHash $dest -Algorithm SHA256).Hash.ToLower()
        if ($got -ne $Sha.ToLower()) {
            Remove-Item $dest -Force
            throw "SHA256 校验失败`n  期望 $($Sha.ToLower())`n  实际 $got"
        }
        Write-Log "  下载完成并通过 SHA256 校验 ($((Get-Item $dest).Length) 字节)"
    }
    else { Write-Log "  下载完成 (清单未提供哈希, 跳过校验)" 'WARN' }
    return $dest
}

# ---------------------------------------------------------------- 安装
function Invoke-WingetInstall {
    param([string]$WingetId)
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        Write-Log '  winget 不可用 (缺少 App Installer 或未在 PATH)' 'ERR'
        return -1
    }
    $wgArgs = @('install','--id',$WingetId,'--exact','--source','winget','--silent',
                '--accept-package-agreements','--accept-source-agreements','--disable-interactivity')
    Write-Log "  winget install --id $WingetId"
    $out = & winget @wgArgs 2>&1 | Out-String
    $code = $LASTEXITCODE
    foreach ($ln in ($out -split "`r?`n" | Where-Object { $_.Trim() })) { Add-Content $script:LogFile $ln -Encoding UTF8 }
    Write-Log "  winget 退出码 $code"
    return $code
}

function Invoke-DirectInstall {
    param($PlanItem)
    $file = Get-InstallerFile -Url $PlanItem.直链 -Sha $PlanItem.SHA -Name $PlanItem.Id
    $tmp  = Join-Path $env:TEMP ("rt_" + $PlanItem.Id + "_" + [guid]::NewGuid().ToString('N').Substring(0,8))
    New-Item -ItemType Directory -Force -Path $tmp | Out-Null
    try {
        $silent = [string]$PlanItem.静默参数
        if (-not $silent) { throw "条目 $($PlanItem.Id) 有直链但没写静默参数, 拒绝弹交互窗口" }
        # {DIR} 替换后的 TEMP 路径可能含空格, 主动加引号
        $silent = $silent.Replace('{DIR}', "`"$tmp`"")

        # 第一段: 自解压包
        Write-Log "  执行 $([IO.Path]::GetFileName($file)) $silent"
        $p = Start-Process -FilePath $file -ArgumentList $silent -Wait -PassThru -NoNewWindow
        Write-Log "  自解压退出码 $($p.ExitCode)"

        # 第二段: 解出来的安装器 (DirectX 这类需要 DXSETUP.exe)
        $innerMsi = Get-ChildItem $tmp -Filter '*.msi' -File -ErrorAction SilentlyContinue | Select-Object -First 1
        $innerExe = Get-ChildItem $tmp -Filter '*.exe' -File -ErrorAction SilentlyContinue |
                    Where-Object { $_.Name -ne [IO.Path]::GetFileName($file) } | Select-Object -First 1
        if ($innerMsi) {
            Write-Log "  执行内层 MSI $($innerMsi.Name) /qn /norestart"
            $p2 = Start-Process msiexec.exe -ArgumentList @('/i', "`"$($innerMsi.FullName)`"", '/qn', '/norestart') -Wait -PassThru -NoNewWindow
            Write-Log "  内层 MSI 退出码 $($p2.ExitCode)"
        }
        elseif ($innerExe) {
            Write-Log "  执行内层安装器 $($innerExe.Name) /silent"
            $p2 = Start-Process -FilePath $innerExe.FullName -ArgumentList '/silent' -Wait -PassThru -NoNewWindow
            Write-Log "  内层退出码 $($p2.ExitCode)"
        }
        else { Write-Log '  解压目录内没有内层安装器' 'WARN' }
    }
    finally {
        Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($Cache) {
    Write-Log '仅下载模式 (-Cache)' 'HEAD'
    foreach ($p in $todo | Where-Object { $_.直链 }) {
        Write-Log "缓存 $($p.名称)"
        try { [void](Get-InstallerFile -Url $p.直链 -Sha $p.SHA -Name $p.Id) }
        catch { Write-Log "  失败: $($_.Exception.Message)" 'ERR' }
    }
    Write-Log 'winget 来源的组件无需预下载 (winget 自行处理)' 
    Write-Log "日志: $script:LogFile"
    return
}

if (-not $Yes) {
    Write-Host ''
    Write-Host "即将安装 $($todo.Count) 个组件。" -ForegroundColor Yellow
    $ans = Read-Host '继续吗? (y/N)'
    if ($ans -notmatch '^[yY]') { Write-Log '用户取消'; return }
}

Write-Log '开始安装' 'HEAD'
$n = 0
$results = New-Object System.Collections.ArrayList
foreach ($p in $todo) {
    $n++
    Write-Log "[$n/$($todo.Count)] $($p.名称)  ($($p.主源))"
    $err = $null
    try {
        if ($p.主源 -eq 'winget') {
            [void](Invoke-WingetInstall -WingetId $p.wingetId)
        }
        elseif ($p.主源 -eq '直链') {
            Invoke-DirectInstall -PlanItem $p
        }
        else { throw '既没有 wingetId 也没有直链, 无法安装' }
    }
    catch {
        $err = $_.Exception.Message
        Write-Log "  失败: $err" 'ERR'
    }
    [void]$results.Add([pscustomobject]@{ Id = $p.Id; 名称 = $p.名称; 错误 = $err })
}

# ---------------------------------------------------------------- 复检
Reset-DetectionCache   # 必须清空, 否则复检读到的是安装前的 ARP 快照
Write-Log '安装完毕, 开始复检' 'HEAD'
$verify = @(foreach ($p in $todo) {
    $after  = Test-RuntimeItem -Item $p.Row
    $before = $state[$p.Id]
    [pscustomobject]@{
        名称 = $p.名称; 层级 = $p.层级
        安装前 = $(if ($before.Installed) { '已装' } else { '缺失' })
        安装后 = $(if ($after.Installed) { '已装' } else { '仍然缺失' })
        版本   = $after.Version
        依据   = $after.Evidence
        成功   = $after.Installed
    }
})

Write-Host ''
Write-Host ("{0,-30} {1,-8} {2,-10} {3}" -f '名称','安装前','安装后','依据/版本') -ForegroundColor Cyan
Write-Host ('-' * 110)
foreach ($v in $verify) {
    $col = if ($v.成功) { 'Green' } else { 'Red' }
    Write-Host ("{0,-30} {1,-8} {2,-10} {3}" -f $v.名称, $v.安装前, $v.安装后, $v.依据) -ForegroundColor $col
}

$okCnt   = @($verify | Where-Object 成功).Count
$failCnt = @($verify | Where-Object { -not $_.成功 }).Count
Write-Host ''
if ($failCnt -eq 0) { Write-Log "全部 $okCnt 项安装成功并通过复检" 'OK' }
else {
    Write-Log "$okCnt 项成功, $failCnt 项未通过复检" 'WARN'
    foreach ($v in ($verify | Where-Object { -not $_.成功 })) { Write-Log "  未通过: $($v.名称) — $($v.依据)" 'ERR' }
}

# 报告
$reportPath = Join-Path (Split-Path $LogDir -Parent) ("报告\运行库安装_{0:yyyyMMdd_HHmmss}.csv" -f (Get-Date))
$verify | Export-Csv -LiteralPath $reportPath -NoTypeInformation -Encoding UTF8
Write-Log "报告: $reportPath"
Write-Log "日志: $script:LogFile"
