#Requires -Version 5.1
<#
  安装软件.ps1  --  "清单上所有软件"的统一安装入口

  数据源: 清单\软件目录.csv (由 引擎\生成目录.ps1 汇总生成)
  每项按 来源 决定落地方式:

    package   本地安装包 (E:\Work 或 包目录) → 便携解压 / 安装器静默跑
    winget    winget 静默安装 (自动提权 / 自动写卸载项)
    download  官网安装器 → 用浏览器打开官网, 用户点一下即可
    dist      123 云盘分享里的文件 → 启动器打开分享页, 用户自己下载
    manual    只提示怎么做

  目标根目录 = <Root>\<英文分类目录>[\<安装子目录>]
  英文目录: Security BasicTools ChatSocial CloudDrive Download Browser
            NetAccel Modeling3D Peripheral Runtime
  —— 里面不出现中文目录名。

  用法:
    安装软件.ps1                             只看计划和落地方式 (默认)
    安装软件.ps1 -Install -Yes               装全部可自动处理的项
    安装软件.ps1 -Install -Yes -Category Security,BasicTools
    安装软件.ps1 -Install -Yes -Only bandizip,everything
    安装软件.ps1 -Install -Yes -PatchOnly    只打补丁 / 只打开需要人工的下载页
    安装软件.ps1 -Install -Yes -NoDownload   不打开任何浏览器 (纯静默)

  管理员: 有就直接用; 没有就用 UAC 提权跑安装器, 拿不到 UAC 上的确认就退回当前用户安装。
#>
[CmdletBinding()]
param(
    [string]   $Catalog   = 'E:\安装系统\清单\软件目录.csv',
    [string]   $Root      = 'E:\Apps',
    [string]   $PackageDir= 'E:\安全软件',
    [string]   $ExtraDirs = '',
    [string]   $ShareUrl  = 'https://1828566527.share.123pan.cn/123pan/2KXljv-OaNUv',
    [string]   $LogDir    = 'E:\安装系统\日志',
    [string]   $LibDir    = 'E:\安装系统\引擎\库',
    [string]   $SevenZip  = 'E:\Work\ModelInstaller\tools\7zip\7z.exe',
    [string[]] $Category,
    [string[]] $Only,
    [string[]] $Skip,
    [switch]   $Install,
    [switch]   $PatchOnly,
    [switch]   $NoDownload,
    [switch]   $NoShortcut,
    [switch]   $NoCloudDownload,
    [switch]   $Force,
    [switch]   $Yes
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
if (-not (Test-Path $LogDir)) { New-Item -ItemType Directory -Force -Path $LogDir | Out-Null }
$script:LogFile = Join-Path $LogDir ("软件安装_{0:yyyyMMdd_HHmmss}.log" -f (Get-Date))
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

. (Join-Path $LibDir '检测.ps1')
. (Join-Path $LibDir 'SoftwareLibrary.ps1')

$logSb = { param($m) Write-Log $m }

# ------------------------------------------------------------------ 载入
$items = @(Get-SoftwareCatalog -Path $Catalog)
if ($Category) { $items = @($items | Where-Object { $Category -contains $_.category -or $Category -contains $_.dir }) }
if ($Only)     { $items = @($items | Where-Object { $Only -contains $_.id -or $Only -contains $_.name }) }
if ($Skip)     { $items = @($items | Where-Object { $Skip -notcontains $_.id }) }
if (-not $items.Count) { Write-Log '没有匹配的条目。' 'WARN'; return }

$pkgDirs = @($PackageDir, 'E:\Work', 'E:\安装系统\缓存\安装包')
if ($ExtraDirs) { $pkgDirs += ($ExtraDirs -split ';' | Where-Object { $_ }) }
$pkgDirs = @($pkgDirs | Where-Object { $_ } | Select-Object -Unique)

$roots = @($Root, 'E:\Apps')
$roots = @($roots | Select-Object -Unique)

# 7z: 优先用传进来的, 其次找常见的 7-Zip 位置
if (-not (Test-Path $SevenZip)) {
    foreach ($cand in @('C:\Program Files\7-Zip\7z.exe', 'C:\Program Files (x86)\7-Zip\7z.exe',
                        'E:\安装系统\引擎\7z.exe', 'E:\安装系统\缓存\7z.exe')) {
        if (Test-Path $cand) { $SevenZip = $cand; break }
    }
}

Write-Log ("软件目录 $Catalog  ({0} 项)" -f $items.Count)
Write-Log ("目标根目录 {0}   安装包目录 {1}" -f $Root, ($pkgDirs -join ' ; '))

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Log '当前不是管理员: 需要提权的安装器会弹一次 UAC, 取消则退回当前用户安装。' 'WARN' }

# ------------------------------------------------------------------ 网盘预下载
# 用自己的 123 云盘登录态把需要的安装包拉到缓存目录 —— 一趟就能把云盘项目
# 变成"本地安装包", 后面第二趟计划和安装就按本地包走, 不用来回跳。
# 没登录态就明确提示, 并退回"打开分享页自己下载"。
$downloaded = 0
$script:FolderFiles = @{}   # 云盘目录型条目: 条目 id -> 该目录下全部文件对象
if (-not $NoCloudDownload -and $items.Count) {
    . (Join-Path $LibDir '网盘目录.ps1')
    $cred = Get-Pan123Credential
    if (-not $cred.exists) {
        Write-Log "未配置 123 云盘登录态, 云盘里的包无法自动下载。" 'WARN'
        Write-Log "  在启动器「网盘下载」页点一次「用浏览器登录」, 或写进 $($cred.path)"
    }
    else {
        # 按分享地址分组: 清单自带的分享 + 插件带来的其它人的分享
        $shares = @{}
        if (-not $shares.ContainsKey($ShareUrl)) { $shares[$ShareUrl] = New-Object System.Collections.ArrayList }
        foreach ($it in $items) {
            if ($it.shareUrl) {
                if (-not $shares.ContainsKey($it.shareUrl)) { $shares[$it.shareUrl] = New-Object System.Collections.ArrayList }
                [void]$shares[$it.shareUrl].Add($it)
            }
            else { [void]$shares[$ShareUrl].Add($it) }
        }

        foreach ($shareUrl in @($shares.Keys)) {
            $bucket = @($shares[$shareUrl])
            if (-not $bucket.Count) { continue }
            # 只需要处理"云盘来源 + 还没下到本地"的那些
            $need = @($bucket | Where-Object {
                $_.archive -and (-not (Get-LocalPackageFile -Item $_ -Dirs $pkgDirs))
            })
            $isPluginShare = ($shareUrl -ne $ShareUrl)
            if (-not $need.Count -and -not $isPluginShare) { continue }

            try {
                $sw = [Diagnostics.Stopwatch]::StartNew()
                $session = Initialize-Pan123Session -ShareUrl $shareUrl -LibDir $LibDir
                Write-Log ("云盘分享 {0}   需要 {1} 个包   登录态: token={2} cookie={3}" -f `
                    $session.ShareKey, $need.Count,
                    $(if ($cred.token) { '有' } else { '无' }), $(if ($cred.cookie) { '有' } else { '无' }))

                $map = Get-RemoteTree -ShareUrl $shareUrl

                # 目录型条目: 文件名匹配不上、但整段路径对得上的 (如游戏文件夹) → 记录整目录下载
                $needNoFile = @($need | Where-Object { -not (Match-RemoteFile -Archive $_.archive -Map $map) })
                foreach ($it in $needNoFile) {
                    if ($script:FolderFiles.ContainsKey($it.id)) { continue }
                    $ffiles = @(Match-RemoteFolder -Archive $it.archive -Files $script:LastRemoteFiles)
                    if ($ffiles.Count) {
                        $script:FolderFiles[$it.id] = $ffiles
                        $tgb = (($ffiles | Measure-Object -Property Size -Sum).Sum) / 1GB
                        Write-Log ("  目录项 {0}: {1} 个文件 / {2:N1} GB, 将整目录下载到目标位置" -f $it.name, $ffiles.Count, $tgb) 'OK'
                    }
                }

                $planDl = @(Get-CatalogDownloadPlan -Items $need -Map $map -OutDir $cloudCache)
                $todoDl = @($planDl | Where-Object { -not $_.cached })
                Write-Log ("  分享里匹配到 {0} 个包, 其中 {1} 个需要下载" -f $planDl.Count, $todoDl.Count)
                if ($todoDl.Count) {
                    $done = @(Save-CatalogDownloads -Plan $todoDl -OutDir $cloudCache -Log $logSb)
                    $downloaded += $done.Count
                }
                Reset-PackageCache
                $sw.Stop()
                Write-Log ("  处理结束: 新下载 {0} 个, 耗时 {1:N0} 秒" -f $downloaded, $sw.Elapsed.TotalSeconds) 'OK'
            }
            catch {
                Write-Log ("云盘下载失败 ({0}): {1}" -f $shareUrl, $_.Exception.Message) 'ERR'
                if ($_.Exception.Message -match '5112') {
                    Write-Log "  → 登录态无效或已过期。请在启动器「网盘下载」页重新登录一次。" 'WARN'
                }
            }
        }
    }
}

# ------------------------------------------------------------------ 计划
Write-Log '扫描本机状态 ...'
$arp = Get-ArpTable
$jobs = New-Object System.Collections.ArrayList
foreach ($it in $items) {
    $st = Test-SoftwareInstalled -Item $it -Roots $roots -Arp $arp
    $act = Resolve-SoftwareAction -Item $it -PackageDirs $pkgDirs -ShareUrl $ShareUrl
    $dest = Get-SoftwareDestDir -Item $it -Root $Root
    [void]$jobs.Add([pscustomobject]@{
        Item=$it; Installed=$st.installed; Detected=$st.detected; Evidence=$st.evidence
        Action=$act.action; File=$act.file; Url=$act.url; Why=$act.why; Dest=$dest
    })
}

Write-Log '计划' 'HEAD'
Write-Host ("  {0,-12} {1,-30} {2,-8} {3,-10} {4}" -f '分类', '名称', '层级', '状态', '落地方式') -ForegroundColor Cyan
Write-Host ('  ' + '-' * 104)
foreach ($p in $jobs) {
    $state = if ($p.Installed) { '已装' } elseif (-not $p.Detected -and $p.Item.detectMethod -eq 'none') { '未知' } else { '缺失' }
    $col = switch ($p.Action) { 'package' {'White'} 'winget' {'White'} 'download' {'Yellow'} 'dist' {'Yellow'} default {'DarkGray'} }
    Write-Host ("  {0,-12} {1,-30} {2,-8} {3,-10} {4}" -f $p.Item.dir, $p.Item.name, $p.Item.tier, $state, $p.Action) -ForegroundColor $col
}
$auto = @($jobs | Where-Object { -not $_.Installed -and $_.Action -in @('package','winget','download') })
$manual = @($jobs | Where-Object { -not $_.Installed -and $_.Action -in @('dist','manual') })
$done = @($jobs | Where-Object { $_.Installed })
Write-Log ("共 {0} 项: 已装 {1}, 可自动装 {2}, 需要人工下载 {3}" -f $jobs.Count, $done.Count, $auto.Count, $manual.Count)

if (-not $Install) {
    Write-Log '仅计划模式, 什么都没有做。要装加 -Install -Yes'
    Write-Log "日志: $script:LogFile"
    return
}

if (($auto.Count -or $manual.Count) -and -not $Yes) {
    Write-Host ''
    Write-Host ("即将: 自动安装 {0} 项, 打开下载页 {1} 项。" -f $auto.Count, $manual.Count) -ForegroundColor Yellow
    if ((Read-Host '继续吗? (y/N)') -notmatch '^[yY]') { Write-Log '用户取消'; return }
}

$results = New-Object System.Collections.ArrayList

function Install-FromFile {
    <# 一个落地文件 → 装到目标目录 + 快捷方式 + 补丁。失败抛错。 #>
    param($PlanItem, [string]$FilePath)
    $dest = Install-SoftwarePackage -Item $PlanItem.Item -File $FilePath -Root $Root -SevenZip $SevenZip -Log $logSb
    if (-not $NoShortcut -and $PlanItem.Item.mode -ne '安装') {
        $main = Get-MainExe -Dir $dest -Name $PlanItem.Item.name
        if ($main -and (New-DesktopShortcut -Name $PlanItem.Item.name -ExePath $main.FullName)) {
            Write-Log ("  桌面快捷方式: {0}.lnk → {1}" -f $PlanItem.Item.name, $main.Name) 'OK'
        }
    }
    Invoke-SoftwarePatch -Item $PlanItem.Item -Dest $dest -SevenZip $SevenZip -Log $logSb
    return $dest
}

# ------------------------------------------------------------------ 便携/安装包 + 补丁
Write-Log '开始处理本地安装包' 'HEAD'
foreach ($p in $auto) {
    if ($p.Action -ne 'package') { continue }
    if ($PatchOnly) { continue }
    $dest = $p.Dest
    $err = $null
    try {
        Write-Log ("[{0}] {1}  ←  {2}" -f $p.Item.dir, $p.Item.name, [IO.Path]::GetFileName($p.File))
        $dest = Install-FromFile -PlanItem $p -FilePath $p.File
    }
    catch { $err = $_.Exception.Message; Write-Log "  失败: $err" 'ERR' }
    [void]$results.Add([pscustomobject]@{
        Id=$p.Item.id; 名称=$p.Item.name; 分类=$p.Item.dir; 方式='安装包'
        结果=$(if ($err) { '失败' } else { '已执行' }); 落地=$dest; 错误=$err
    })
}

# ------------------------------------------------------------------ 云盘整目录项: 按原结构下载到目标位置
# 名单里"弹丸论破 1 → 16_游戏软件\弹丸1"这类是云盘目录不是单个文件,
# 这里把目录下所有文件按相对结构下载到 <目标目录>, 游戏资料片等大体积内容也走这条路。
foreach ($p in $auto) {
    if ($p.Action -ne 'dist') { continue }
    if (-not $script:FolderFiles.ContainsKey($p.Item.id)) { continue }
    if ($PatchOnly) { continue }
    $files = @($script:FolderFiles[$p.Item.id])
    $dest = $p.Dest
    $err = $null
    try {
        $prefix = Get-CommonPathPrefix -Files $files
        $total = (($files | Measure-Object -Property Size -Sum).Sum)
        Write-Log ("[{0}] {1}  ←  云盘目录 {2}  ({3} 个文件, {4:N1} GB)" -f `
            $p.Item.dir, $p.Item.name, $p.Item.archive, $files.Count, ($total / 1GB))
        if (-not $Yes) { throw '整目录下载体积可能很大, 需要 -Yes 确认' }
        New-Item -ItemType Directory -Force -Path $dest | Out-Null
        $i = 0
        foreach ($f in $files) {
            $i++
            $fullp  = [string]$f.路径
            $rel    = ''
            if ($prefix -and $fullp.Length -gt $prefix.Length) { $rel = $fullp.Substring($prefix.Length).TrimStart('\') }
            $subDir = ''
            if ($rel) { $subDir = [IO.Path]::GetDirectoryName($rel) }
            $targetDir = if ($subDir) { Join-Path $dest $subDir } else { $dest }
            try {
                [void](Save-Pan123File -File $f -OutDir $targetDir -SkipHash)
                Write-Log ("  [{0}/{1}] OK  {2}" -f $i, $files.Count, $f.FileName)
            } catch {
                Write-Log ("  [{0}/{1}] 失败  {2}: {3}" -f $i, $files.Count, $f.FileName, $_.Exception.Message) 'ERR'
            }
            Start-Sleep -Milliseconds 400    # 云盘接口限频, 文件间稍作停顿
        }
        if (-not $NoShortcut) {
            $main = Get-MainExe -Dir $dest -Name $p.Item.name
            if ($main -and (New-DesktopShortcut -Name $p.Item.name -ExePath $main.FullName)) {
                Write-Log ("  桌面快捷方式: {0}.lnk → {1}" -f $p.Item.name, $main.Name) 'OK'
            }
        }
        $p.Action = 'folder-done'
    } catch {
        $err = $_.Exception.Message
        Write-Log "  失败: $err" 'ERR'
    }
    [void]$results.Add([pscustomobject]@{
        Id=$p.Item.id; 名称=$p.Item.name; 分类=$p.Item.dir; 方式='云盘目录'
        结果=$(if ($err) { '失败' } else { '已下载' }); 落地=$dest; 错误=$err
    })
}

# ------------------------------------------------------------------ 官网直链: 自动下载再装
# 清单/插件标了 official 且给了直链, 又写了文件名或能从 URL 推出文件名的, 直接下到缓存再装。
foreach ($p in $auto) {
    if ($p.Action -ne 'download') { continue }
    if ($PatchOnly) { continue }
    $it = $p.Item
    $err = $null; $dest = $p.Dest; $file = $null
    try {
        $fileName = $it.archive
        if (-not $fileName) {
            $u = ($it.official -split '\s+')[0]
            $guess = [IO.Path]::GetFileName(($u -split '\?')[0])
            if ($guess -and $guess -match '\.(exe|msi|zip|7z|rar|tar|gz)$') { $fileName = $guess }
        }
        if ($it.official -and $fileName) {
            Write-Log ("[{0}] {1}  ←  官网直链 {2}" -f $it.dir, $it.name, $it.official)
            # 清单/插件提供了 SHA256 就校验; 没提供则至少记录落地文件哈希, 便于事后核对
            $f = Save-RemoteFile -Url $it.official -OutDir $cloudCache -FileName $fileName -Sha256 $it.sha256
            if ($f.Skipped) { Write-Log "  缓存命中: $($f.Path)" } else { Write-Log "  下载完成 $([math]::Round($f.Bytes/1MB,1)) MB" }
            if (-not $it.sha256) {
                Write-Log ("  SHA256={0} (清单未提供期望哈希, 未校验, 仅记录)" -f (Get-FileHash $f.Path -Algorithm SHA256).Hash.ToLower()) 'WARN'
            }
            $file = $f.Path
            $dest = Install-FromFile -PlanItem $p -FilePath $file
            $p.Action = 'package'
        }
        else {
            throw "官网只有页面地址, 没有可直接下载的直链"
        }
    }
    catch { $err = $_.Exception.Message; Write-Log "  失败: $err" 'ERR' }
    [void]$results.Add([pscustomobject]@{
        Id=$it.id; 名称=$it.name; 分类=$it.dir; 方式='官网直链'
        结果=$(if ($err) { '失败' } else { '已执行' }); 落地=$(if ($dest) { $dest } else { $it.official }); 错误=$err
    })
}

# ------------------------------------------------------------------ winget
$wg = @($auto | Where-Object { $_.Action -eq 'winget' })
if ($wg.Count -and -not $PatchOnly) {
    Write-Log "winget 静默安装 $($wg.Count) 项" 'HEAD'
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        Write-Log 'winget 不可用 (缺少 App Installer), 跳过全部 winget 项' 'ERR'
        foreach ($p in $wg) {
            [void]$results.Add([pscustomobject]@{
                Id=$p.Item.id; 名称=$p.Item.name; 分类=$p.Item.dir; 方式='winget'
                结果='winget不可用'; 落地=$p.Item.wingetId; 错误='winget 未安装'
            })
        }
    }
    else {
    foreach ($p in $wg) {
        Write-Log ("[{0}] {1}  winget install --id {2}" -f $p.Item.dir, $p.Item.name, $p.Item.wingetId)
        try {
            $wo = & winget install --id $p.Item.wingetId --exact --source winget --silent `
                      --accept-package-agreements --accept-source-agreements --disable-interactivity 2>&1 | Out-String
            foreach ($ln in ($wo -split "`r?`n" | Where-Object { $_.Trim() })) { Add-Content $script:LogFile $ln -Encoding UTF8 }
            $wgCode = $LASTEXITCODE
        } catch {
            $wgCode = -1
            Write-Log "  winget 调用异常: $($_.Exception.Message)" 'ERR'
        }
        Write-Log "  退出码 $wgCode"
        [void]$results.Add([pscustomobject]@{
            Id=$p.Item.id; 名称=$p.Item.name; 分类=$p.Item.dir; 方式='winget'
            结果=$(if ($wgCode -eq 0) { '已执行' } else { "码$wgCode" }); 落地=$p.Item.wingetId; 错误=$null
        })
    }
    }
}

# ------------------------------------------------------------------ 需要人工的: 打开下载页
# 网盘/官网只能人工点一下 —— 不做任何自动下载, 只把页面和要拿的文件名摆到眼前。
if (-not $NoDownload -and -not $PatchOnly) {
    # 已经整目录下载的项不再让用户手动去下
    $needDist = @($manual | Where-Object { $_.Action -eq 'dist' -and -not $script:FolderFiles.ContainsKey($_.Item.id) })
    $needDl   = @($manual | Where-Object { $_.Action -eq 'download' })
    if ($needDist.Count) {
        Write-Log "以下 $($needDist.Count) 项在 123 云盘分享里, 需要你自己下载:" 'HEAD'
        Write-Log "  分享地址: $ShareUrl"
        foreach ($p in $needDist) { Write-Log ("    {0,-10} {1,-28} 文件: {2}" -f $p.Item.dir, $p.Item.name, $p.Item.archive) }
        Write-Log "  下载后放到: $PackageDir  然后重跑本脚本即可自动导入安装" 'WARN'
        try { Start-Process $ShareUrl } catch { }
    }
    if ($needDl.Count) {
        Write-Log "以下 $($needDl.Count) 项走官网, 已用浏览器打开:" 'HEAD'
        foreach ($p in $needDl) {
            Write-Log ("    {0,-10} {1,-28} {2}" -f $p.Item.dir, $p.Item.name, $p.Item.Url)
            try { Start-Process $p.Url } catch { }
        }
    }
    $needManual = @($manual | Where-Object { $_.Action -eq 'manual' })
    foreach ($p in $needManual) {
        Write-Log ("    手动: {0,-10} {1,-28} {2}" -f $p.Item.dir, $p.Item.name, $p.Why) 'WARN'
    }
    foreach ($p in $manual) {
        if ($script:FolderFiles.ContainsKey($p.Item.id)) { continue }
        [void]$results.Add([pscustomobject]@{
            Id=$p.Item.id; 名称=$p.Item.name; 分类=$p.Item.dir; 方式=$p.Action
            结果='需人工'; 落地=$(if ($p.Url) { $p.Url } else { $p.Item.archive }); 错误=$null
        })
    }
}

# ------------------------------------------------------------------ 复检
Reset-DetectionCache
$arp = Get-ArpTable
Write-Log '复检' 'HEAD'
$verified = New-Object System.Collections.ArrayList
foreach ($p in $jobs) {
    $st = Test-SoftwareInstalled -Item $p.Item -Roots $roots -Arp $arp
    [void]$verified.Add([pscustomobject]@{
        分类=$p.Item.dir; 名称=$p.Item.name; 层级=$p.Item.tier
        方式=$p.Action; 安装前=$(if ($p.Installed) { '已装' } else { '缺失' })
        安装后=$(if ($st.installed) { '已装' } elseif ($st.detected) { '仍缺失' } else { '未验证' })
        依据=$st.evidence
    })
}
foreach ($v in $verified) {
    $col = switch ($v.安装后) { '已装' {'Green'} '仍缺失' {'Red'} default {'DarkGray'} }
    Write-Host ("  {0,-12} {1,-30} {2,-6} {3,-8} {4}" -f $v.分类, $v.名称, $v.方式, $v.安装后, $v.依据) -ForegroundColor $col
}

$reportDir = Join-Path (Split-Path $LogDir -Parent) '报告'
if (-not (Test-Path $reportDir)) { New-Item -ItemType Directory -Force -Path $reportDir | Out-Null }
$rp = Join-Path $reportDir ("软件安装_{0:yyyyMMdd_HHmmss}.csv" -f (Get-Date))
$verified | Export-Csv -LiteralPath $rp -NoTypeInformation -Encoding UTF8
Write-Log "报告: $rp"
Write-Log "日志: $script:LogFile"
