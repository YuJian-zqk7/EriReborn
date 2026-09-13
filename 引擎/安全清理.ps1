#Requires -Version 5.1
<#
  安全清理.ps1  --  安全与清理软件安装系统

  来源: 123云盘分享 (主) + 官网免费源 (备)
  流程: 检测 → 下载 → 解压/安装 → 复检

  用法:
    安全清理.ps1 -Report                     只列清单和检测状态 (默认)
    安全清理.ps1 -Download                   只从123云盘下载到缓存
    安全清理.ps1 -Install -Yes               下载并装到目标目录
    安全清理.ps1 -Only huorong,autoruns -Install -Yes
    安全清理.ps1 -Pan123Cookie '<cookie串>'  直接传 cookie
    (或把 cookie 写进 E:\安装系统\123云盘cookie.txt)

  便携类解压到 E:\安全清理\<名称>\ , 安装类跑安装器。
#>
[CmdletBinding()]
param(
    [string]   $Manifest   = 'E:\安装系统\清单\安全清理.csv',
    [string]   $ShareUrl   = 'https://1828566527.share.123pan.cn/123pan/2KXljv-YXcuv',
    [string]   $CacheDir   = 'E:\安装系统\缓存\安全清理',
    [string]   $Root       = 'E:\Apps',
    [string]   $CategoryDir= 'security_clear',
    [string]   $LogDir     = 'E:\安装系统\日志',
    [string]   $LibDir     = 'E:\安装系统\引擎\库',
    [string]   $SevenZip   = 'E:\Work\ModelInstaller\tools\7zip\7z.exe',
    [string]   $ImportDir,
    [string]   $Pan123Cookie,
    [string]   $Pan123Token,
    [string[]] $Only,
    [string[]] $Skip,
    [switch]   $Report,
    [switch]   $Download,
    [switch]   $Install,
    [switch]   $OpenShare,
    [switch]   $Force,
    [switch]   $Yes
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $LogDir)) { New-Item -ItemType Directory -Force -Path $LogDir | Out-Null }
$script:LogFile = Join-Path $LogDir ("安全清理_{0:yyyyMMdd_HHmmss}.log" -f (Get-Date))
. (Join-Path $LibDir '日志.ps1')
Limit-LogRetention -Dir $LogDir

# 目标根 = <用户选的根>\<英文类别>   例如 E:\Apps\security_clear
$TargetDir = if ($CategoryDir) { Join-Path $Root $CategoryDir } else { $Root }

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

# ------------------------------------------------ 桌面快捷方式与主程序判定
function New-DesktopShortcut {
    param([string]$Name, [string]$ExePath)
    $ws  = New-Object -ComObject WScript.Shell
    $desktop = [Environment]::GetFolderPath('Desktop')
    $lnk = $ws.CreateShortcut((Join-Path $desktop ($Name + '.lnk')))
    $lnk.TargetPath = $ExePath
    $lnk.WorkingDirectory = Split-Path $ExePath -Parent
    $lnk.Save()
}
function Get-MainExe {
    param([string]$Dir, [string]$Name)
    $exes = @(Get-ChildItem $Dir -Recurse -Filter *.exe -File -ErrorAction SilentlyContinue)
    if (-not $exes.Count) { return $null }
    $key = ($Name -replace '[^\p{L}]','').ToLower()
    $hit = $exes | Where-Object { (($_.BaseName -replace '[^\p{L}]','').ToLower()) -match $key } |
           Sort-Object Length -Descending | Select-Object -First 1
    if ($hit) { return $hit }
    return $exes | Sort-Object Length -Descending | Select-Object -First 1
}

. (Join-Path $LibDir '123云盘.ps1')

# ------------------------------------------------------------------ 载入
$items = Import-Csv -LiteralPath $Manifest -Encoding UTF8
if ($Only) { $items = $items | Where-Object { $_.Id -in $Only } }
if ($Skip) { $items = $items | Where-Object { $_.Id -notin $Skip } }

$cookie = if ($Pan123Cookie) { $Pan123Cookie } else { Read-Pan123Cookie }
$token  = if ($Pan123Token) { $Pan123Token } else { Read-Pan123Token }
$share  = Set-Pan123Share -ShareUrl $ShareUrl -Cookie $cookie -Token $token
Write-Log "123云盘分享 $($share.ShareKey)   登录态: cookie=$(if($share.HasCookie){'有'}else{'无'}) token=$(if($share.HasToken){'有'}else{'无'})"

# ------------------------------------------------------------------ 检测
# 安全匹配: 检测参数当正则用, 写坏时降级为字面匹配, 不让单个坏 regex 崩掉整个脚本
function Test-SafeMatch {
    param([string]$Text, [string]$Pattern)
    if (-not $Pattern) { return $false }
    try { return ($Text -match $Pattern) }
    catch { return ($Text -like "*$Pattern*") }
}
function Get-ArpNames {
    $roots = @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
               'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
               'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*')
    foreach ($r in $roots) {
        Get-ItemProperty -Path $r -ErrorAction SilentlyContinue |
            ForEach-Object { [pscustomobject]@{ Name = [string]$_.DisplayName; Key = [string]$_.PSChildName } }
    }
}
$arp = Get-ArpNames

Write-Log '扫描本机已装情况 ...'
$state = @{}
foreach ($it in $items) {
    $installed = $false; $evidence = ''
    $folder = Join-Path $TargetDir $it.名称
    if (Test-Path $folder) {
        $exes = @(Get-ChildItem $folder -Recurse -Filter *.exe -File -ErrorAction SilentlyContinue)
        if ($exes.Count) {
            $installed = $true; $evidence = "目标目录已有可执行文件: $($exes[0].Name)"
        }
        else { $evidence = "目录存在但无可执行文件, 视为未装 (可能解压不完整)" }
    }
    elseif ($it.检测方式 -eq 'arp' -and $it.检测参数) {
        $hit = $arp | Where-Object { (Test-SafeMatch $_.Name $it.检测参数) -or (Test-SafeMatch $_.Key $it.检测参数) } | Select-Object -First 1
        if ($hit) { $installed = $true; $evidence = "ARP 命中: $($hit.Name)$($hit.Key)" }
        else { $evidence = 'ARP 中无匹配' }
    }
    else { $evidence = '不做检测' }
    $state[$it.Id] = [pscustomobject]@{ Installed=$installed; Evidence=$evidence }
}

# ------------------------------------------------------------------ 展示
Write-Log '清单与当前状态' 'HEAD'
Write-Host ("  {0,-20} {1,-28} {2,-8} {3,-6} {4}" -f 'Id','名称','类别','层级','状态') -ForegroundColor Cyan
Write-Host ('  ' + '-' * 100)
foreach ($it in $items) {
    $s = $state[$it.Id]
    $st = if ($s.Installed) { '已装' } else { '缺失' }
    $col = if ($s.Installed) { 'DarkGray' } else { 'White' }
    Write-Host ("  {0,-20} {1,-28} {2,-8} {3,-6} {4}" -f $it.Id, $it.名称, $it.类别, $it.层级, $st) -ForegroundColor $col
}
$miss = @($items | Where-Object { -not $state[$_.Id].Installed })
Write-Log "共 $($items.Count) 项, 缺失 $(($miss).Count) 项"

if ($Report -or (-not $Download -and -not $Install -and -not $ImportDir -and -not $OpenShare)) {
    Write-Log '仅检测模式。要下载加 -Download, 要装加 -Install'
    Write-Log "日志: $script:LogFile"
    return
}

# ------------------------------------------------------------------ 下载
# 本地导入时完全不碰云盘接口 —— 新分享有 700+ 文件要 80+ 次调用, 会触发 429 限频,
# 而且离线路径本来就不该依赖网络。
$tree = @(); $treeBy = @{}
$wingetList = @()   # 提前初始化: 任何模式下无安装包但有 wingetId 的项都收进来 (BUG-8)
if (-not $ImportDir) {
    Write-Log '读 123云盘 文件列表 ...' 'HEAD'
    $tree = @(Get-Pan123Tree)
    Write-Log "  分享内 $($tree.Count) 个文件"
    $treeBy = @{}; foreach ($f in $tree) { $treeBy[$f.FileName] = $f }
}

# ---- 导入分支: 从本地文件夹收集已下载的文件 (123云盘手动下载后丢这里) ----
# ---- 导入分支: 从本地文件夹收集已下载的文件 ----
# 匹配顺序: 文件名完全相同 → 归一化后相同 (去掉版本号/空格/符号, 只留字母与汉字)
# 这样即使你把文件重命名或换了版本号也能认出来。
function Get-NameKey { param([string]$s) return ($s -replace '[^\p{L}]','').ToLower() }

if ($ImportDir) {
    if (-not (Test-Path $ImportDir)) { throw "导入目录不存在: $ImportDir" }
    Write-Log "从本地目录导入: $ImportDir" 'HEAD'
    $found = @(Get-ChildItem $ImportDir -File -Recurse -ErrorAction SilentlyContinue)
    Write-Log "  目录内 $($found.Count) 个文件"
    $dl = @{}; $used = @(); $wingetList = @()
    foreach ($it in $items) {
        # 清单里没写分享文件名的, 说明这项只能从官网直下, 绝不参与本地目录匹配 ——
        # 否则空 key 会 -like "*" 匹配到任意文件, 把别的包当成本项安装。
        if (-not $it.分享文件名) {
            Write-Log ("  跳过 (无分享文件名, 需官网下载): {0}" -f $it.名称) 'WARN'
            continue
        }
        $key = Get-NameKey ([IO.Path]::GetFileNameWithoutExtension($it.分享文件名))
        $hit = $found | Where-Object { $_.Name -eq $it.分享文件名 } | Select-Object -First 1
        if (-not $hit -and $key.Length -ge 2) {
            $hit = $found | Where-Object { (Get-NameKey $_.BaseName) -eq $key } | Select-Object -First 1
        }
        if (-not $hit -and $key.Length -ge 4) {
            $hit = $found | Where-Object {
                $k2 = Get-NameKey $_.BaseName
                $k2.Length -ge 2 -and ($k2 -like "*$key*" -or $key -like "*$k2*")
            } | Select-Object -First 1
        }
        if ($hit) {
            $dl[$it.Id] = $hit.FullName; $used += $hit.FullName
            Write-Log ("  OK   {0,-30} <- {1}" -f $it.名称, $hit.Name) 'OK'
        }
        elseif ($it.wingetId) {
            $wingetList += $it
            Write-Log ("  winget {0,-28} ({1})" -f $it.名称, $it.wingetId) 'OK'
        }
        else { Write-Log ("  缺   {0,-30} ({1})" -f $it.名称, $it.分享文件名) 'WARN' }
    }
    $un = @($found | Where-Object { $_.FullName -notin $used })
    if ($un.Count -and -not $Only) {
        Write-Log "  目录里有 $($un.Count) 个文件没被清单认领:" 'WARN'
        $un | ForEach-Object { Write-Log "      $($_.Name)" }
    }
    Write-Log "收集到 $($dl.Count)/$($items.Count) 个文件"
}
# ---- 自动打开分享页 + 监测目录等文件到齐 ----
if ($OpenShare) {
    $watchDir = if ($ImportDir) { $ImportDir } else { 'E:\安全清理\安装包' }
    if (-not (Test-Path $watchDir)) { New-Item -ItemType Directory -Force -Path $watchDir | Out-Null }
    Write-Log "分享页: $ShareUrl" 'HEAD'
    Write-Log "把下载的文件放进: $watchDir"
    Start-Process $ShareUrl
    Write-Log '等待文件到齐 (Ctrl+C 中断) ...'
    $names = @($items | Where-Object { $_.分享文件名 } | Select-Object -ExpandProperty 分享文件名)
    while ($true) {
        $have = @(Get-ChildItem $watchDir -File -Recurse -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)
        $need = @($names | Where-Object { $_ -notin $have })
        Write-Host ("`r  已到 {0}/{1}   还缺: {2}          " -f ($names.Count-$need.Count), $names.Count,
                    (($need | Select-Object -First 3) -join ', ')) -NoNewline
        if (-not $need.Count) { Write-Host ''; Write-Log '文件已到齐' 'OK'; break }
        Start-Sleep -Seconds 5
    }
    Write-Log '接下来会自动导入安装 ...'
    $ImportDir = $watchDir
}
if (-not (Test-Path $CacheDir)) { New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null }

if (-not $ImportDir) {
$dl = @{}
foreach ($it in $items) {
    $f = $treeBy[$it.分享文件名]
    if (-not $f) {
        Write-Log "  分享内没有 '$($it.分享文件名)' ($($it.名称))" 'WARN'
        if ($it.官网直链) { Write-Log "      可改用官网源: $($it.官网直链)" }
        if (-not $it.分享文件名 -and $it.wingetId) { $wingetList += $it }
        continue
    }
    Write-Log "  下载 $($it.名称)  ($([math]::Round($f.Size/1MB,1)) MB)"
    try {
        $r = Save-Pan123File -File $f -OutDir $CacheDir -SkipHash
        $dl[$it.Id] = $r.Path
        if ($r.Skipped) { Write-Log "      缓存命中: $($r.Path)" }
        elseif ($r.Sha256) { Write-Log "      完成 SHA256=$($r.Sha256.Substring(0,16))..." 'OK' }
        else            { Write-Log "      完成: $($r.Path)" 'OK' }
    }
    catch { Write-Log "      失败: $($_.Exception.Message)" 'ERR' }
}
}

if (-not $Install) {
    Write-Log '仅下载模式 (-Download)'
    Write-Log "日志: $script:LogFile"
    return
}

# ------------------------------------------------------------------ 安装
if (-not $Yes) {
    Write-Host ''
    Write-Host "即将安装 $($dl.Count) 个组件到 $TargetDir" -ForegroundColor Yellow
    if ((Read-Host '继续吗? (y/N)') -notmatch '^[yY]') { Write-Log '用户取消'; return }
}

$has7z = Test-Path $SevenZip
if (-not $has7z) { Write-Log "找不到 7z: $SevenZip (便携类无法解压)" 'WARN' }

Write-Log '开始安装' 'HEAD'
$results = New-Object System.Collections.ArrayList
foreach ($it in $items) {
    if (-not $dl.ContainsKey($it.Id)) { continue }
    if ($state[$it.Id].Installed -and -not $Force) {
        Write-Log "  跳过 (已装): $($it.名称)" 'WARN'
        continue
    }
    $file = $dl[$it.Id]
    $dest = Join-Path $TargetDir $it.名称
    # 清单里标了「补丁目标」的, 解压直接覆盖进那个目录 —— 这就是打补丁
    $patchTarget = [string]$it.补丁目标
    if ($patchTarget) { $dest = $patchTarget }
    $err  = $null
    try {
        if ($it.安装方式 -eq '便携') {
            # 有些包的 zip 头部损坏, 7z 会中途放弃但退出码仍是 0/1/2 不定。
            # 所以不能只看退出码 —— 按"解出来的体积对不对得上压缩包"来判断, 不对就重试。
            # 实测 SmartDefrag_11.1.0.466_Single.zip 第一次只解出一个 PNG。
            $arcSize = (Get-Item $file).Length
            $gotBytes = 0; $got = @()
            for ($try = 1; $try -le 3; $try++) {
                if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
                New-Item -ItemType Directory -Force -Path $dest | Out-Null
                Write-Log "  解压 $([IO.Path]::GetFileName($file)) → $dest$(if($try -gt 1){" (第 $try 次)"})"
                # 7z 会把 "Headers Error" 之类写到 stderr。脚本全局 ErrorActionPreference=Stop 时,
                # PowerShell 会把第一行 stderr 当成终止性错误抛出 —— 这才是"失败:(空消息)"的真正原因,
                # 而且会让重试逻辑一次都跑不到。这里临时放开。
                $eap = $ErrorActionPreference
                $ErrorActionPreference = 'Continue'
                $out = & $SevenZip x $file "-o$dest" -y -bso0 -bsp0 2>&1 | Out-String
                $rc  = $LASTEXITCODE
                $ErrorActionPreference = $eap
                Add-Content $script:LogFile $out -Encoding UTF8
                $got = @(Get-ChildItem $dest -Recurse -File -ErrorAction SilentlyContinue)
                $gotBytes = ($got | Measure-Object Length -Sum).Sum
                if ($gotBytes -ge $arcSize * 0.8) { break }
                Write-Log ("      只得到 {0:N0} 字节 (包 {1:N0}), 重试" -f $gotBytes, $arcSize) 'WARN'
            }
            if ($gotBytes -lt $arcSize * 0.5) {
                throw ("解压结果异常: 得到 {0:N0} 字节 / 包 {1:N0} 字节, 7z 退出码 {2}" -f $gotBytes, $arcSize, $rc)
            }
            if ($rc -ne 0) { Write-Log "      7z 退出码 $rc 但体积正常, 按成功处理" 'WARN' }
            # 有些 exe 根本不是压缩包(是自带资源的普通程序), 7z 会解出一堆资源却没有主程序。
            # 这种情况直接把原文件作为绿色版放进去。
            $exes = @(Get-ChildItem $dest -Recurse -Filter *.exe -File -ErrorAction SilentlyContinue)
            if (-not $exes.Count) {
                Copy-Item -LiteralPath $file -Destination $dest -Force
                Write-Log "      解压产物里没有可执行文件, 已直接复制原文件" 'WARN'
            }
        }
        elseif ($it.安装方式 -eq '安装') {
            # 只能跑真正的安装器。压缩包/镜像跑 Start-Process -Wait 会打开默认程序并卡死。
            $ext = [IO.Path]::GetExtension($file).ToLower()
            if ($ext -notin @('.exe','.msi','.bat','.cmd','.ps1')) {
                throw "不是可执行的安装器（$ext），拒绝运行：$file"
            }
            Write-Log "  运行安装器 $([IO.Path]::GetFileName($file))"
            $p = Start-Process -FilePath $file -Wait -PassThru
            Write-Log "  退出码 $($p.ExitCode)"
        }
        else { Write-Log "  手动项, 只下载不安装" 'WARN' }
    }
    catch { $err = $_.Exception.Message; Write-Log "  失败: $err" 'ERR' }

    # 桌面快捷方式: 解压成功且有主程序 (补丁项不需要)
    if (-not $err -and -not $patchTarget -and (Test-Path $dest)) {
        $mainExe = Get-MainExe -Dir $dest -Name $it.名称
        if ($mainExe) {
            try {
                New-DesktopShortcut -Name $it.名称 -ExePath $mainExe.FullName
                Write-Log ("  桌面快捷方式: {0}.lnk -> {1}" -f $it.名称, $mainExe.Name) 'OK'
            } catch { Write-Log "  快捷方式失败: $($_.Exception.Message)" 'WARN' }
        }
    }

    # 补丁程序: 清单里标了就运行 —— 用于"解压后打授权补丁"这类
    # (例如 Bandizip 专业版: 解出安装器与 PRO 补丁, 跑完补丁才算完)
    if (-not $err -and $it.补丁程序) {
        $patchExe = Join-Path $dest $it.补丁程序
        if (Test-Path $patchExe) {
            Write-Log "  运行补丁: $([IO.Path]::GetFileName($patchExe))"
            try {
                $pp = Start-Process -FilePath $patchExe -Wait -PassThru
                Write-Log "  补丁退出码 $($pp.ExitCode)" 'OK'
            } catch { Write-Log "  补丁执行失败: $($_.Exception.Message)" 'WARN' }
        }
        else { Write-Log "  补丁程序不存在: $patchExe" 'WARN' }
    }

    # 复检
    $ok = $false
    if ($it.安装方式 -eq '便携') { $ok = Test-Path $dest }
    if (-not $ok) { $ok = -not $err }
    [void]$results.Add([pscustomobject]@{
        Id=$it.Id; 名称=$it.名称; 方式=$it.安装方式
        结果 = $(if ($err) { '失败' } elseif ($ok) { '成功' } else { '未验证' })
        落地 = $(if (Test-Path $dest) { $dest } else { $file })
        错误 = $err
    })
}

# ---------------------------------------------------------------- winget 安装
# 清单里写了 wingetId 但本地没有安装包的走这里 (自动提权/静默/自动写卸载项)
if ($wingetList.Count) {
    Write-Log "winget 安装 $($wingetList.Count) 项" 'HEAD'
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        Write-Log 'winget 不可用 (缺少 App Installer), 跳过 winget 安装' 'ERR'
    }
    else {
    foreach ($w in $wingetList) {
        Write-Log "  winget install --id $($w.wingetId)"
        try {
            $wo = & winget install --id $w.wingetId --exact --source winget --silent `
                      --accept-package-agreements --accept-source-agreements --disable-interactivity 2>&1 | Out-String
            foreach ($ln in ($wo -split "`r?`n" | Where-Object { $_.Trim() })) { Add-Content $script:LogFile $ln -Encoding UTF8 }
            $wcode = $LASTEXITCODE
        } catch {
            $wcode = -1
            Write-Log "  winget 调用异常: $($_.Exception.Message)" 'ERR'
        }
        [void]$results.Add([pscustomobject]@{
            Id=$w.Id; 名称=$w.名称; 方式='winget'
            结果=$(if ($wcode -eq 0) { '成功' } else { "码$wcode" })
            落地=$w.wingetId; 错误=$null
        })
        Write-Log "  退出码 $wcode"
    }
    }
}
Write-Log '结果' 'HEAD'
foreach ($r in $results) {
    $col = if ($r.结果 -eq '成功') { 'Green' } else { 'Red' }
    Write-Host ("  {0,-28} {1,-6} {2,-8} {3}" -f $r.名称, $r.方式, $r.结果, $r.落地) -ForegroundColor $col
}
$rp = Join-Path (Split-Path $LogDir -Parent) ("报告\安全清理_{0:yyyyMMdd_HHmmss}.csv" -f (Get-Date))
$results | Export-Csv -LiteralPath $rp -NoTypeInformation -Encoding UTF8
Write-Log "报告: $rp"
Write-Log "日志: $script:LogFile"
