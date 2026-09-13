# =====================================================================
#  SoftwareLibrary.ps1  --  统一软件目录的读取、检测、落地定位
#
#  这个文件是"清单上所有软件"的唯一解释器:
#    软件目录.csv (生成产物) ──> Get-SoftwareCatalog ──> 各项带目录/来源/状态
#
#  引擎(安装软件.ps1 等)与 导出状态.ps1 都用这里的结果,
#  保证命令行与图形界面看到的结论一致, 不会两套判断分叉。
#
#  依赖: 检测.ps1 里的 Get-ArpTable / Reset-DetectionCache / Test-RuntimeItem
# =====================================================================

function Get-SoftwareCatalog {
    <#
      读取统一软件目录。返回项字段(英文键):
        id name category dir tier source wingetId mode detectMethod detectParam
        official archive localPackage installSub patch note
        plugin pluginId shareUrl silent      (插件应用才有值, meta 表补进来)
    #>
    param([Parameter(Mandatory)][string] $Path)

    if (-not (Test-Path $Path)) { throw "缺软件目录: $Path (先跑 引擎\生成目录.ps1)" }

    # 插件应用额外字段存在 清单\插件目录.csv, 按 Id 对回来
    $meta = @{}
    $indexFile = Join-Path (Split-Path $Path -Parent) '插件目录.csv'
    if (Test-Path $indexFile) {
        foreach ($m in (Import-Csv -LiteralPath $indexFile -Encoding UTF8)) { if ($m.Id) { $meta[$m.Id] = $m } }
    }

    foreach ($r in (Import-Csv -LiteralPath $Path -Encoding UTF8)) {
        if (-not $r.Id) { continue }
        $m = $null
        if ($meta.ContainsKey($r.Id)) { $m = $meta[$r.Id] }
        [pscustomobject]@{
            id             = $r.Id
            name           = $r.名称
            category       = $r.类别
            dir            = $(if ($r.英文目录) { $r.英文目录 } else { 'MyApps' })
            tier           = $(if ($r.层级) { $r.层级 } else { '推荐' })
            source         = $r.来源
            wingetId       = $r.wingetId
            mode           = $(if ($r.安装方式) { $r.安装方式 } else { '便携' })
            detectMethod   = $r.检测方式
            detectParam    = $r.检测参数
            official       = $r.官网直链
            sha256         = $(if ($r.PSObject.Properties['SHA256']) { $r.SHA256 } else { $null })
            archive        = $r.包文件名
            localPackage   = ($r.本地包 -eq 'yes')
            installSub     = $r.安装子目录
            patch          = $r.补丁程序
            note           = $r.说明
            plugin         = $(if ($m) { $m.插件 } else { $r.插件 })
            pluginId       = $(if ($m) { $m.插件Id } else { $r.插件Id })
            shareUrl       = $(if ($m) { $m.分享地址 } else { $r.分享地址 })
            silent         = $(if ($m) { $m.静默参数 } else { $r.静默参数 })
            isPlugin       = [bool]($m -or $r.插件)
        }
    }
}

function New-DesktopShortcut {
    param([string]$Name, [string]$ExePath)
    try {
        $ws  = New-Object -ComObject WScript.Shell
        $desktop = [Environment]::GetFolderPath('Desktop')
        $lnk = $ws.CreateShortcut((Join-Path $desktop ($Name + '.lnk')))
        $lnk.TargetPath = $ExePath
        $lnk.WorkingDirectory = Split-Path $ExePath -Parent
        $lnk.Save()
        return $true
    } catch { return $false }
}

function Get-MainExe {
    param([string]$Dir, [string]$Name)
    $exes = @(Get-ChildItem $Dir -Recurse -Filter *.exe -File -ErrorAction SilentlyContinue)
    if (-not $exes.Count) { return $null }
    $key = ($Name -replace '[^\p{L}]','').ToLower()
    if ($key.Length -ge 2) {
        $hit = $exes | Where-Object { (($_.BaseName -replace '[^\p{L}]','').ToLower()) -match $key } |
               Sort-Object Length -Descending | Select-Object -First 1
        if ($hit) { return $hit }
    }
    return $exes | Sort-Object Length -Descending | Select-Object -First 1
}

function Test-SoftwareInstalled {
    <#
      判断一项是否已装。事实优先, 不敢下结论时返回 detected=$false, 绝不当成"缺失"。
      便携解压到过 <Root>\<英文目录>\<名称> 的, 目录里有 exe 就算装好。
    #>
    param($Item, [string[]]$Roots, $Arp)

    if (-not $Arp) { $Arp = Get-ArpTable }
    $ev = '不做检测'; $installed = $false; $detected = $false

    foreach ($root in $Roots) {
        if (-not $root -or -not (Test-Path $root)) { continue }
        $folder = Join-Path (Join-Path $root $Item.dir) $Item.name
        if (-not (Test-Path $folder)) { continue }
        $exes = @(Get-ChildItem $folder -Recurse -Filter *.exe -File -ErrorAction SilentlyContinue)
        if ($exes.Count) { return [pscustomobject]@{ installed=$true; detected=$true; evidence="便携目录: $($exes[0].Name)" } }
        $ev = '目录存在但没有可执行文件'
    }

    if ($Item.detectMethod -eq 'arp' -and $Item.detectParam) {
        $hit = $null
        foreach ($a in $Arp) {
            try {
                if ($a.Name -match $Item.detectParam -or $a.KeyName -match $Item.detectParam) { $hit = $a; break }
            } catch {
                # 检测参数不是合法正则时按字面匹配
                if ($a.Name -like "*$($Item.detectParam)*" -or $a.KeyName -like "*$($Item.detectParam)*") { $hit = $a; break }
            }
        }
        $detected = $true
        if ($hit) { $installed = $true; $ev = "ARP 命中: $($hit.Name) $($hit.Version)" }
        else      { $ev = 'ARP 中无匹配' }
    }
    elseif ($Item.detectMethod -and $Item.detectMethod -ne 'none') {
        $s = Test-RuntimeItem -Item ([pscustomobject]@{
            Id=$Item.id; 名称=$Item.name; 类别=$Item.category; 层级=$Item.tier
            wingetId=$Item.wingetId; 检测方式=$Item.detectMethod; 检测参数=$Item.detectParam
        })
        $installed = [bool]$s.Installed
        $detected  = [bool]$s.Detected
        $ev        = $s.Evidence
    }

    if (-not $detected -and $installed) { $detected = $true }
    return [pscustomobject]@{ installed=$installed; detected=$detected; evidence=$ev }
}

$script:_PkgFileCache = $null
$script:_PkgDirKey    = ''

function Reset-PackageCache {
    $script:_PkgFileCache = $null
    $script:_PkgDirKey    = ''
}

function Get-PackageFileTable {
    <#
      把候选目录下的文件列一遍并缓存 —— 142 项逐个递归枚举会被拖到两分钟,
      列一次共用才行。目录组合变了才重新列。
    #>
    param([string[]]$Dirs)
    $key = (@($Dirs | Where-Object { $_ } | Select-Object -Unique) -join '|')
    if ($script:_PkgFileCache -and $script:_PkgDirKey -eq $key) { return $script:_PkgFileCache }

    $files = New-Object System.Collections.ArrayList
    foreach ($d in @($Dirs | Where-Object { $_ } | Select-Object -Unique)) {
        if (-not (Test-Path $d)) { continue }
        foreach ($f in (Get-ChildItem $d -File -Recurse -ErrorAction SilentlyContinue)) { [void]$files.Add($f) }
    }
    $script:_PkgFileCache = $files
    $script:_PkgDirKey    = $key
    return $files
}

function Get-LocalPackageFile {
    <#
      在候选目录里找这一项的安装包。
      匹配顺序: 文件名完全相同 → 归一化后相同(去掉版本号/空格/符号) → 互含。
    #>
    param($Item, [string[]]$Dirs)

    if (-not $Item.archive -or -not $Item.localPackage) { return $null }
    $files = Get-PackageFileTable -Dirs $Dirs
    if (-not $files.Count) { return $null }

    $key = ($Item.archive -replace '[^\p{L}\p{N}]','').ToLower()
    foreach ($f in $files) {
        if ($f.Name -eq $Item.archive) { return $f }
    }
    if ($key.Length -lt 2) { return $null }
    foreach ($f in $files) {
        if ((($f.BaseName -replace '[^\p{L}\p{N}]','').ToLower()) -eq $key) { return $f }
    }
    if ($key.Length -ge 4) {
        foreach ($f in $files) {
            $k2 = ($f.BaseName -replace '[^\p{L}\p{N}]','').ToLower()
            if ($k2.Length -ge 4 -and ($k2 -like "*$key*" -or $key -like "*$k2*")) { return $f }
        }
    }
    return $null
}

function Resolve-SoftwareAction {
    <#
      决定一项该怎么落地:
        action = package | winget | download | dist | manual
        file   = package 时的本地安装包路径
        url    = download/dist/manual 时的地址
        why    = 给用户看的一句话
    #>
    param($Item, [string[]]$PackageDirs, [string]$ShareUrl)

    # 1) 本地已经有包 —— 直接装
    $file = Get-LocalPackageFile -Item $Item -Dirs $PackageDirs
    if ($file) {
        return [pscustomobject]@{ action='package'; file=$file.FullName; url=''; why="本地安装包: $($file.Name)" }
    }

    # 2) 手动项: 不动系统, 只提示
    if ($Item.mode -eq '手动') {
        return [pscustomobject]@{ action='manual'; file=''; url=$Item.official; why=('手动处理: ' + $Item.note) }
    }

    # 3) 官方直链能拼出文件名 → 自动下载再装 (插件里的 official 项走这条)
    if ($Item.official) {
        $guess = [IO.Path]::GetFileName((($Item.official -split '\s+')[0] -split '\?')[0])
        $name = if ($Item.archive) { $Item.archive }
                elseif ($guess -match '\.(exe|msi|zip|7z|rar|tar|gz)$') { $guess }
                else { '' }
        if ($name) {
            return [pscustomobject]@{ action='download'; file=''; url=$Item.official; why="官网直链: $($Item.official)" }
        }
    }

    # 4) winget
    if ($Item.wingetId) {
        return [pscustomobject]@{ action='winget'; file=''; url=$Item.wingetId; why="winget: $($Item.wingetId)" }
    }

    # 5) 只有官网页面 → 打开让用户自己点
    if ($Item.official) {
        return [pscustomobject]@{ action='download'; file=''; url=$Item.official; why="官网页面: $($Item.official)" }
    }

    # 6) 只有云盘文件 → 打开分享页
    if ($Item.archive) {
        $u = $(if ($Item.shareUrl) { $Item.shareUrl } else { $ShareUrl })
        return [pscustomobject]@{ action='dist'; file=''; url=$u; why="云盘自取: $($Item.archive)" }
    }

    return [pscustomobject]@{ action='manual'; file=''; url=''; why='清单未给出安装来源' }
}

function Get-SoftwareDestDir {
    <# 目标目录: <Root>\<英文分类目录>[\<安装子目录>] , 全部英文, 不出现中文 #>
    param($Item, [string]$Root)
    if ($Item.installSub) { return Join-Path (Join-Path $Root $Item.dir) $Item.installSub }
    if ($Item.name -match '^[\x20-\x7E]+$') { return Join-Path (Join-Path $Root $Item.dir) $Item.name }
    return Join-Path (Join-Path $Root $Item.dir) $Item.id
}

function Get-SoftwareHelp {
    <# 给用户看的一句话: 这项从哪来、要怎么做 #>
    param($Item)
    switch ($Item.source) {
        'pan123'   { return "123云盘自取: $($Item.archive)" }
        'dist'     { return "123云盘自取: $($Item.archive)" }
        'official' { return "官网下载: $($Item.official)" }
        'winget'   { return "winget: $($Item.wingetId)" }
        'local'    { return "本地安装包: $($Item.archive)" }
        default    { return '手动处理' }
    }
}

function Save-RemoteFile {
    <# 直链下载到缓存目录, 带大小判定与重试。返回落地文件对象。 #>
    param(
        [Parameter(Mandatory)][string] $Url,
        [Parameter(Mandatory)][string] $OutDir,
        [string] $FileName,
        [string] $Sha256,
        [int]    $TimeoutSec = 1800,
        [switch] $Force
    )
    if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force -Path $OutDir | Out-Null }

    $name = $FileName
    if (-not $name) {
        $name = [IO.Path]::GetFileName(($Url -split '\?')[0])
        if (-not $name) { $name = 'download.bin' }
    }
    $dest = Join-Path $OutDir $name
    if ((Test-Path $dest) -and -not $Force) {
        if ($Sha256 -and (Get-FileHash $dest -Algorithm SHA256).Hash.ToLower() -eq $Sha256.ToLower()) {
            return [pscustomobject]@{ Path = $dest; Skipped = $true; Bytes = (Get-Item $dest).Length }
        }
        if (-not $Sha256 -and (Get-Item $dest).Length -gt 0) {
            return [pscustomobject]@{ Path = $dest; Skipped = $true; Bytes = (Get-Item $dest).Length }
        }
    }

    $tmp = "$dest.part"
    $ok = $false
    $prevProgress = $ProgressPreference
    for ($try = 1; $try -le 3 -and -not $ok; $try++) {
        try {
            $ProgressPreference = 'SilentlyContinue'
            Invoke-WebRequest -Uri $Url -OutFile $tmp -UseBasicParsing -TimeoutSec $TimeoutSec `
                -UserAgent 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36'
            $ok = $true
        }
        catch {
            if ($try -ge 3) { throw "下载失败: $Url  ($($_.Exception.Message))" }
            Start-Sleep -Seconds (3 * $try)
        }
        finally { $ProgressPreference = $prevProgress }
    }
    if (-not $ok) {
        Remove-Item $tmp -Force -ErrorAction SilentlyContinue
        throw "下载失败: $Url"
    }
    Move-Item $tmp $dest -Force
    if ($Sha256) {
        $got = (Get-FileHash $dest -Algorithm SHA256).Hash.ToLower()
        if ($got -ne $Sha256.ToLower()) {
            Remove-Item $dest -Force
            throw "SHA256 校验失败: 期望 $($Sha256.ToLower()) 实际 $got"
        }
    }
    return [pscustomobject]@{ Path = $dest; Skipped = $false; Bytes = (Get-Item $dest).Length }
}

function Expand-SoftwareArchive {    <# 7z 解压, 按"解出来的体积对不对得上压缩包"判断成败, 不对就重试 #>
    param([string]$File, [string]$Dest, [string]$SevenZip, [scriptblock]$Log)

    $arcSize = (Get-Item $File).Length
    $gotBytes = 0; $got = @()
    for ($try = 1; $try -le 3; $try++) {
        if (Test-Path $Dest) { Remove-Item $Dest -Recurse -Force -ErrorAction SilentlyContinue }
        New-Item -ItemType Directory -Force -Path $Dest | Out-Null
        & $Log ("  解压 {0} → {1}{2}" -f [IO.Path]::GetFileName($File), $Dest, $(if ($try -gt 1) { " (第 $try 次)" } else { '' }))

        # 7z 会把 "Headers Error" 写到 stderr; ErrorActionPreference=Stop 时会被当成终止性错误
        $eap = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        $out = & $SevenZip x $File "-o$Dest" -y -bso0 -bsp0 2>&1 | Out-String
        $rc  = $LASTEXITCODE
        $ErrorActionPreference = $eap

        $got = @(Get-ChildItem $Dest -Recurse -File -ErrorAction SilentlyContinue)
        $gotBytes = ($got | Measure-Object Length -Sum).Sum
        if ($gotBytes -ge $arcSize * 0.8) { break }
        & $Log ("  只得到 {0:N0} 字节 (包 {1:N0}), 重试" -f $gotBytes, $arcSize)
    }
    if ($gotBytes -lt $arcSize * 0.5) {
        throw ("解压结果异常: 得到 {0:N0} 字节 / 包 {1:N0} 字节 (7z 退出码 {2})" -f $gotBytes, $arcSize, $rc)
    }
    return $rc
}

function Install-SoftwarePackage {
    <# 落地一个本地安装包: 便携类解压, 安装类跑安装器, 解不出 exe 就直接复制原文件 #>
    param($Item, [string]$File, [string]$Root, [string]$SevenZip, [scriptblock]$Log)

    $dest = Get-SoftwareDestDir -Item $Item -Root $Root
    if ($Item.mode -eq '安装') {
        $ext = [IO.Path]::GetExtension($File).ToLower()
        if ($ext -notin @('.exe','.msi','.bat','.cmd','.ps1')) {
            throw "不是可执行的安装器 ($ext), 拒绝运行: $File"
        }
        & $Log ("  运行安装器 {0}" -f [IO.Path]::GetFileName($File))
        if ($ext -eq '.msi') {
            $p = Start-Process msiexec.exe -ArgumentList @('/i', "`"$File`"", '/qn', '/norestart') -Wait -PassThru
        }
        else {
            # exe 安装器必须带静默参数, 否则带 UI 的安装器会弹窗并因 -Wait 卡死整个流水线 (BUG-5)
            if ($Item.silent) {
                $p = Start-Process -FilePath $File -ArgumentList $Item.silent -Wait -PassThru
            }
            else {
                & $Log "  警告: 清单未提供静默参数, 直接运行安装器 (可能弹交互窗口)" 'WARN'
                $p = Start-Process -FilePath $File -Wait -PassThru
            }
        }
        & $Log "  安装器退出码 $($p.ExitCode)"
        return $dest
    }

    # 便携类: 解压
    if (-not (Test-Path $SevenZip)) { throw "找不到 7z: $SevenZip (便携类无法解压)" }
    [void](Expand-SoftwareArchive -File $File -Dest $dest -SevenZip $SevenZip -Log $Log)
    $exes = @(Get-ChildItem $dest -Recurse -Filter *.exe -File -ErrorAction SilentlyContinue)
    if (-not $exes.Count) {
        Copy-Item -LiteralPath $File -Destination $dest -Force
        & $Log "  解压产物里没有可执行文件, 已直接复制原文件"
    }
    return $dest
}

function Invoke-SoftwarePatch {
    <# 清单里标了 补丁程序 就跑它 —— 解压后打授权/汉化补丁 #>
    param($Item, [string]$Dest, [string]$SevenZip, [scriptblock]$Log)

    if (-not $Item.patch) { return }
    $patchPath = Join-Path $Dest $Item.patch
    if (-not (Test-Path $patchPath)) {
        & $Log ("  补丁未就位: {0}" -f $Item.patch)
        return
    }
    & $Log ("  运行补丁 {0}" -f [IO.Path]::GetFileName($patchPath))
    try {
        if ([IO.Path]::GetExtension($patchPath).ToLower() -eq '.exe') {
            $pp = Start-Process -FilePath $patchPath -Wait -PassThru
            & $Log "  补丁退出码 $($pp.ExitCode)"
        }
        elseif (Test-Path $SevenZip) {
            [void](Expand-SoftwareArchive -File $patchPath -Dest $Dest -SevenZip $SevenZip -Log $Log)
        }
    } catch { & $Log "  补丁执行失败: $($_.Exception.Message)" }
}
