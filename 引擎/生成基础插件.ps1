#Requires -Version 5.1
<#
  生成基础插件.ps1  --  全插件模式的官方基础插件生成器 (v2, 名单驱动)

  数据源: 清单\完整名单.txt  (txt2_修改后的完整名单, [有]=云盘已有)
  输出:   插件\00-官方基础软件.json
  分享:   https://1828566527.share.123pan.cn/123pan/2KXljv-SqGSv

  规则:
    * 只映射 01~09 大类里的 [有]/[有？] 条目 (软件/可下载资产);
      [补] 云盘没有、[挪] 只是移动建议、10_扩展与清单按名单要求不做启动器项、
      11~14 是课程/模型/素材/个人资料 (非软件)、15/16 与 07 重复 —— 这些跳过。
    * 旧版基础插件里调好的 检测/补丁/静默参数/winget/官网直链, 按名字或文件名
      匹配后继承到新条目; 旧插件里名单没有的应用也保留, 并按大类归位。
    * 安装方式按扩展名猜: exe/msi/bat/cmd = 安装, zip/7z/rar = 便携。
#>
[CmdletBinding()]
param(
    [string] $Root     = 'E:\安装系统',
    [string] $ListFile = 'E:\安装系统\清单\完整名单.txt',
    [string] $OutFile
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
. (Join-Path $Root '引擎\库\插件解析.ps1')

if (-not $OutFile) { $OutFile = Join-Path (Join-Path $Root '插件') '00-官方基础软件.json' }

$shareNew = 'https://1828566527.share.123pan.cn/123pan/2KXljv-SqGSv'

# ---- 大类 -> 英文目录 -----------------------------------------------------
$catMap = [ordered]@{
    '01_系统基础' = 'SystemBase'
    '02_网络下载' = 'NetDownload'
    '03_开发AI'   = 'DevAI'
    '04_平面绘画' = 'DigitalArt'
    '05_3D建模'   = 'Modeling3D'
    '06_音视频'   = 'AudioVideo'
    '07_游戏'     = 'Games'
    '08_办公阅读' = 'OfficeRead'
    '09_美化便携' = 'Beautify'
    'EdgeExt'     = 'EdgeExt'   # Edge 扩展 (仅 extraDefs 用, 名单里 10_扩展与清单 仍不解析)
}
$catCn = [ordered]@{
    'SystemBase' = '系统基础'; 'NetDownload' = '网络下载'; 'DevAI' = '开发AI'
    'DigitalArt' = '平面绘画'; 'Modeling3D' = '3D建模';  'AudioVideo' = '音视频'
    'Games' = '游戏';          'OfficeRead' = '办公阅读'; 'Beautify' = '美化便携'
    'EdgeExt' = 'Edge 扩展'
}
# 旧分类 -> 新大类 (旧插件里名单没提到的应用按这个归位)
$oldDirMap = @{
    'security_clear' = 'SystemBase';  'BasicTools' = 'SystemBase'; 'Peripheral' = 'SystemBase'
    'ChatSocial'     = 'NetDownload'; 'CloudDrive' = 'NetDownload'; 'Download'  = 'NetDownload'
    'Browser'        = 'NetDownload'; 'NetAccel'   = 'NetDownload'; 'Modeling3D' = 'Modeling3D'
}
$skipInstExt = @('.pdf','.docx','.doc','.txt','.xlsx','.pptx','.mp4','.jpg','.png','.sutg','.mepack','.html')

function Get-Norm([string]$s) {
    if (-not $s) { return '' }
    return ($s -replace '[^\p{L}\p{N}]','').ToLower()
}

# ---- 0) 旧版基础插件: 字段继承 + 名单外应用保留 ----------------------------
$oldApps = @()
$oldPluginFile = $OutFile
if (Test-Path $oldPluginFile) {
    try {
        $op = Get-Content -LiteralPath $oldPluginFile -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($a in @($op.apps)) {
            $a | Add-Member -NotePropertyName _normName  -NotePropertyValue (Get-Norm $a.name)  -Force
            $a | Add-Member -NotePropertyName _normFile  -NotePropertyValue (Get-Norm (
                $(if ($a.file) { [IO.Path]::GetFileNameWithoutExtension([string]$a.file) } else { '' }))) -Force
            $oldApps += $a
        }
    } catch { Write-Warning "旧插件解析失败, 按无旧插件处理: $($_.Exception.Message)" }
}
$oldByNorm = @{}
foreach ($a in $oldApps) {
    foreach ($k in @($a._normName, $a._normFile)) {
        if ($k -and -not $oldByNorm.ContainsKey($k)) { $oldByNorm[$k] = $a }
    }
}

# ---- 1) 解析名单 ----------------------------------------------------------
if (-not (Test-Path $ListFile)) { throw "缺名单文件: $ListFile" }
$lines = Get-Content -LiteralPath $ListFile -Encoding UTF8

$entries  = New-Object System.Collections.ArrayList
$stats = @{ you=0; bu=0; nuo=0; other=0; asset=0 }
$curCat = $null
$last = $null          # 上一条 [有] 条目 (用于多行续行)

foreach ($raw in $lines) {
    $line = $raw.TrimEnd()
    if ($line -match '^(={3,}|-{3,})') { continue }

    # 大类标题: "01_系统基础        子组：..."
    if ($line -match '^(\d{2}_[^\s]+)') {
        $curCat = $Matches[1]
        $last = $null
        continue
    }

    # [有] / [有？] 条目
    if ($line -match '^\s*\[(有？?)\]\s*(.+)$') {
        if (-not $curCat) { continue }
        $text = $Matches[2].Trim()
        $e = [pscustomobject]@{ Cat=$curCat; Text=$text; File='' ; Done=$false }
        [void]$entries.Add($e)
        $last = $e
        $stats.you++
        continue
    }

    # 其它状态 ([补] [挪] [] [让用户...] 等): 只统计
    if ($line -match '^\s*\[([^\]]*)\]\s*(.*)$') {
        $st = $Matches[1]; $last = $null
        if     ($st -eq '补') { $stats.bu++ }
        elseif ($st -eq '挪') { $stats.nuo++ }
        else   { $stats.other++ }
        continue
    }

    # 多行续行: 归并到上一条 [有] (Adobe 合集 / 位置：...)
    if ($last -and $line.Trim() -and $line -notmatch '^【' ) {
        $t = $line.Trim()
        if ($t -match '^(推荐插件|这一类|1\)|2\)|3\))') { $last = $null; continue }
        $last.Text += ' ' + $t
    }
}

# ---- 2) 条目 -> 应用 ------------------------------------------------------
function Get-FileHint([string]$text) {
    # 从文本里找最长的 安装包/压缩包 文件名
    $best = ''
    foreach ($m in [regex]::Matches($text, '[^\s，、；()（）]*\.(?:zip|7z|rar|exe|msi|bat|cmd|iso)')) {
        if ($m.Value.Length -gt $best.Length) { $best = $m.Value }
    }
    if ($best -match '^(.+?)(（[^）]*）|\([^)]*\))*$') { }   # 保留原样, 后缀括号极少数情况
    if ($best -and $best -match '^(.*[\\/])?([^\\/]+)$') { $best = $Matches[2] }
    return $best
}
function Get-CleanName([string]$text, [string]$file) {
    $n = $text
    if ($file) {
        # 去掉 "名字 → 文件" 的后半段
        $idx = $n.IndexOf($file)
        if ($idx -gt 0) { $n = $n.Substring(0, $idx) }
        $n = $n -replace '\s*→\s*$', ''
    }
    # 去掉结尾的说明性括号
    $n = $n -replace '\s*（[^）]*）\s*$', ''
    $n = $n -replace '\s*\([^)]*\)\s*$', ''
    return $n.Trim(' ', '　', '·', '-', '—')
}

$apps     = New-Object System.Collections.ArrayList
$usedIds  = @{}
$carried  = 0
$noFile   = New-Object System.Collections.ArrayList
$skipped  = New-Object System.Collections.ArrayList
$seenCatName = @{}

function Add-App {
    param($Cat, $Name, $File, $Old, $Note, [string]$Winget, [string]$Url, [string]$Mode)
    if (-not $Name) { return }
    $key = "$Cat|$((Get-Norm $Name))"
    if ($seenCatName.ContainsKey($key)) { return }
    $seenCatName[$key] = $true

    $id = Get-EnSlug -Text $Name -Id 'app'
    if (-not $id -or $id -match '^\d+$') { $id = 'app' }
    if ($usedIds.ContainsKey($id)) { $usedIds[$id]++; $id = "$id$($usedIds[$id])" } else { $usedIds[$id] = 1 }

    $app = [ordered]@{
        name = $Name; id = $id; category = $Cat
        tier = '推荐'; source = 'pan123'; mode = '便携'
    }
    $share = $shareNew
    if ($Old) {
        # 继承旧插件调好的字段
        if ($Old.source)        { $app.source = $Old.source }
        if ($Old.mode)          { $app.mode   = $Old.mode }
        if ($Old.wingetId)      { $app.wingetId = $Old.wingetId }
        if ($Old.url)           { $app.url      = $Old.url }
        if ($Old.file)          { $app.file     = $Old.file }
        if ($Old.shareUrl)      { $share        = $Old.shareUrl }
        if ($Old.silent)        { $app.silent   = $Old.silent }
        if ($Old.patch)         { $app.patch    = $Old.patch }
        if ($Old.detect -and $Old.detect.method -and $Old.detect.method -ne 'none' -and $Old.detect.param) {
            $app.detect = [ordered]@{ method = [string]$Old.detect.method; param = [string]$Old.detect.param }
        }
        if (-not $app.file -and $File) { $app.file = $File }
        if ($app.source -eq 'pan123' -and -not $app.file -and $File) { $app.file = $File }
    } else {
        if ($File) { $app.file = $File }
        else       { $app.file = $Name }     # 没写文件名的, 用名字去云盘里匹配
        $ext = [IO.Path]::GetExtension([string]$app.file).ToLower()
        if ($ext -in @('.exe','.msi','.bat','.cmd')) { $app.mode = '安装' }
    }
    if ($app.source -eq 'pan123') { $app.shareUrl = $share }
    # [补] 条目: 有 wingetId 走 winget, 否则给官方/GitHub 链接 (引擎自动打开下载页)
    if ($Winget) {
    $app.source = 'winget'; $app.wingetId = $Winget
    $app.mode = $(if ($Mode) { $Mode } else { '安装' })
    if ($app.Contains('file')) { $app.Remove('file') }
    if ($app.Contains('shareUrl')) { $app.Remove('shareUrl') }
    } elseif ($Url) {
    $app.source = 'official'; $app.url = $Url
    $app.mode = $(if ($Mode) { $Mode } else { '安装' })
    if ($app.Contains('file')) { $app.Remove('file') }
    if ($app.Contains('shareUrl')) { $app.Remove('shareUrl') }
    }
    if ($Note) { $app.note = $Note }
    [void]$script:apps.Add($app)
    }

foreach ($e in $entries) {
    if (-not $catMap.Contains($e.Cat)) { continue }   # 跳过 10~16 大类
    $dir = $catMap[$e.Cat]
    $cat = $catCn[$dir]

    $file = Get-FileHint $e.Text
    $name = Get-CleanName $e.Text $file

    # 纯文档/资产: 跳过
    $ext = ''
    if ($file) { $ext = [IO.Path]::GetExtension($file).ToLower() }
    if ($ext -and $ext -in $skipInstExt) { $stats.asset++; [void]$skipped.Add("[$($e.Cat)] $name ($ext 资产)"); continue }
    if ($name -match '\.(pdf|docx|doc|txt|xlsx|pptx|mp4|jpg|png|sutg|mepack|html)$') {
        $stats.asset++; [void]$skipped.Add("[$($e.Cat)] $name (文档/资产)"); continue
    }
    if (-not $file) { [void]$noFile.Add("[$($e.Cat)] $name") }

    # 名单切分: "AIMP、Listen1、AniCh（在 06...）" 这类无文件名的多名字条目
    if (-not $file -and $name -match '、' -and $name -notmatch '：') {
        $parts = @($name -split '、' | ForEach-Object {
            $p = $_.Trim() -replace '\s*（[^）]*）\s*$', ''
            $p.Trim()
        } | Where-Object { $_ })
        if ($parts.Count -ge 2 -and $parts.Count -le 4 -and (@($parts | Where-Object { $_.Length -gt 20 })).Count -eq 0) {
            foreach ($p in $parts) { Add-App -Cat $cat -Name $p -File '' -Old $null }
            continue
        }
    }

    # 字段继承: 按名字或文件名匹配旧插件
    $old = $oldByNorm[(Get-Norm $name)]
    if (-not $old -and $file) { $old = $oldByNorm[(Get-Norm [IO.Path]::GetFileNameWithoutExtension($file))] }
    if ($old) { $script:carried++ }

    Add-App -Cat $cat -Name $name -File $file -Old $old -Note $(if ($name -ne $e.Text.Trim()) { $e.Text.Trim() })
}

# ---- 3) 旧插件里名单没有的应用: 保留, 按大类归位 ---------------------------
$leftover = 0
foreach ($a in $oldApps) {
    $key = "$((Get-Norm $a.name))"
    $found = $false
    foreach ($app in $apps) {
        if ((Get-Norm $app.name) -eq $key -or ($a._normFile -and (Get-Norm $app.file) -eq $a._normFile)) { $found = $true; break }
    }
    if ($found) { continue }
    $newDir = $oldDirMap[[string]$a.dir]
    if (-not $newDir) { continue }   # 插件目录里非官方来源的不管
    Add-App -Cat $catCn[$newDir] -Name $a.name -File ([string]$a.file) -Old $a
    $leftover++
}

# ---- 3.5) [补] 条目: 云盘没有、官方/winget 好找的, 给官方来源 --------------
# Winget 优先 (引擎原生支持静默安装); 没有 winget 包的给官网/GitHub 链接,
# 引擎会自动打开下载页, 用户下完放进包目录重跑即可。
$extraDefs = @(
    # 驱动
    @{ Cat='01_系统基础'; Name='NVIDIA 应用程序';        Url='https://www.nvidia.com/en-us/software/nvidia-app/'; Note='NVIDIA App (含 GeForce Experience 功能)' }
    @{ Cat='01_系统基础'; Name='NVIDIA Container';       Url='https://www.nvidia.com/en-us/software/nvidia-app/'; Note='随 NVIDIA App / 显卡驱动一起安装' }
    @{ Cat='01_系统基础'; Name='Intel Graphics 显卡驱动'; Url='https://www.intel.com/content/www/us/en/support/detect.html'; Note='Intel 驱动程序和支持助理自动检测' }
    @{ Cat='01_系统基础'; Name='Realtek 音频驱动';        Url='https://www.realtek.com/Download/List?cate_id=584' }
    @{ Cat='01_系统基础'; Name='Realtek 网卡驱动';        Url='https://www.realtek.com/Download/List?cate_id=584' }
    @{ Cat='01_系统基础'; Name='Nahimic';                 Url='https://apps.microsoft.com/store/search?query=Nahimic'; Note='微软商店搜索安装 (随 OEM 提供)' }
    @{ Cat='01_系统基础'; Name='Lenovo Fn 快捷键驱动';    Url='https://newsupport.lenovo.com.cn/'; Note='联想驱动页按型号搜索 Hotkeys' }
    @{ Cat='01_系统基础'; Name='Lenovo ACPI 驱动';        Url='https://newsupport.lenovo.com.cn/'; Note='联想驱动页按型号搜索 ACPI' }
    @{ Cat='01_系统基础'; Name='GaomonTablet';            Url='https://www.gaomon.net/download/' }
    @{ Cat='01_系统基础'; Name='HUION 数位板驱动';        Url='https://www.huion.com/download/' }
    @{ Cat='01_系统基础'; Name='Iriun Webcam';            Url='https://iriun.com' }
    @{ Cat='01_系统基础'; Name='VB-Audio Cable A';        Url='https://vb-audio.com/Cable/' }
    @{ Cat='01_系统基础'; Name='Voicemeeter';             Url='https://vb-audio.com/Voicemeeter/' }
    @{ Cat='01_系统基础'; Name='VBCABLE';                 Url='https://vb-audio.com/Cable/' }
    @{ Cat='01_系统基础'; Name='X-Rite Color Assistant';  Url='https://www.xrite.com/page/x-rite-color-assistant' }
    @{ Cat='01_系统基础'; Name='AISpeech';                Url='https://apps.microsoft.com/store/search?query=AISpeech'; Note='微软商店搜索安装' }
    @{ Cat='01_系统基础'; Name='Xvdd SCSI';               Url='https://apps.microsoft.com/store/search?query=Xbox'; Note='装 Xbox 应用 / Gaming Services 后自动修复' }
    # 运行库
    @{ Cat='01_系统基础'; Name='Visual C++ 2008 (x86)';       Winget='Microsoft.VCRedist.2008.x86' }
    @{ Cat='01_系统基础'; Name='Visual C++ 2008 (x64)';       Winget='Microsoft.VCRedist.2008.x64' }
    @{ Cat='01_系统基础'; Name='Visual C++ 2010 (x86)';       Winget='Microsoft.VCRedist.2010.x86' }
    @{ Cat='01_系统基础'; Name='Visual C++ 2010 (x64)';       Winget='Microsoft.VCRedist.2010.x64' }
    @{ Cat='01_系统基础'; Name='Visual C++ 2012 (x86)';       Winget='Microsoft.VCRedist.2012.x86' }
    @{ Cat='01_系统基础'; Name='Visual C++ 2012 (x64)';       Winget='Microsoft.VCRedist.2012.x64' }
    @{ Cat='01_系统基础'; Name='Visual C++ 2013 (x86)';       Winget='Microsoft.VCRedist.2013.x86' }
    @{ Cat='01_系统基础'; Name='Visual C++ 2013 (x64)';       Winget='Microsoft.VCRedist.2013.x64' }
    @{ Cat='01_系统基础'; Name='Visual C++ 2015-2022 (x86)';  Winget='Microsoft.VCRedist.2015+.x86' }
    @{ Cat='01_系统基础'; Name='Visual C++ 2015-2022 (x64)';  Winget='Microsoft.VCRedist.2015+.x64' }
    @{ Cat='01_系统基础'; Name='.NET 6 Desktop Runtime';      Winget='Microsoft.DotNet.DesktopRuntime.6' }
    @{ Cat='01_系统基础'; Name='.NET 8 Desktop Runtime';      Winget='Microsoft.DotNet.DesktopRuntime.8' }
    @{ Cat='01_系统基础'; Name='XNA Framework 4.0';           Url='https://www.microsoft.com/en-us/download/details.aspx?id=20914' }
    @{ Cat='01_系统基础'; Name='Java 25';                     Url='https://www.java.com/download/' }
    @{ Cat='01_系统基础'; Name='OpenJDK 21';                  Winget='Microsoft.OpenJDK.21' }
    @{ Cat='01_系统基础'; Name='GameInput';                   Winget='Microsoft.GameInput' }
    @{ Cat='01_系统基础'; Name='Edge WebView2 Runtime';       Url='https://developer.microsoft.com/microsoft-edge/webview2/' }
    # 聊天 / 网盘 / 浏览器
    @{ Cat='02_网络下载'; Name='超级互联';                    Url='https://apps.microsoft.com/store/search?query=超级互联'; Note='联想 Smart Connect, 微软商店搜索' }
    @{ Cat='02_网络下载'; Name='Microsoft Teams';             Winget='Microsoft.Teams' }
    @{ Cat='02_网络下载'; Name='Outlook 桌面版';              Winget='Microsoft.OutlookForWindows' }
    @{ Cat='02_网络下载'; Name='OneDrive';                    Winget='Microsoft.OneDrive' }
    @{ Cat='02_网络下载'; Name='Microsoft Edge 离线安装包';   Url='https://www.microsoft.com/zh-cn/edge/business/download' }
    # 终端 / 开发工具
    @{ Cat='03_开发AI';   Name='Windows Terminal';            Winget='Microsoft.WindowsTerminal' }
    @{ Cat='03_开发AI';   Name='GitHub-Store';                Url='https://github.com/search?q=github-store&type=repositories'; Note='GitHub 搜索项目' }
    # 图像处理 (与 01 驱动重复, 双分类各一份)
    @{ Cat='04_平面绘画'; Name='X-Rite Color Assistant';      Url='https://www.xrite.com/page/x-rite-color-assistant' }
    # Edge 扩展: 商店没有静默接口, 点安装自动打开对应的 Edge 商店页, 点一下「获取」即装
    @{ Cat='EdgeExt'; Name='AdGuard 广告拦截器';              Url=('https://microsoftedge.microsoft.com/addons/search/' + [uri]::EscapeDataString('AdGuard'));   Note='Edge 商店搜索页, 点「获取」安装' }
    @{ Cat='EdgeExt'; Name='Tampermonkey';                    Url='https://chromewebstore.google.com/detail/tampermonkey/dhdgffkkebhmkfjojejmpbldmpobfkfo'; Note='Chrome 商店页, Edge 打开点「获取」' }
    @{ Cat='EdgeExt'; Name='沉浸式翻译 (Immersive Translate)'; Url=('https://microsoftedge.microsoft.com/addons/search/' + [uri]::EscapeDataString('Immersive Translate')); Note='Edge 商店搜索页' }
    @{ Cat='EdgeExt'; Name='iTab 新标签页';                   Url=('https://microsoftedge.microsoft.com/addons/search/' + [uri]::EscapeDataString('iTab')); Note='Edge 商店搜索页' }
    @{ Cat='EdgeExt'; Name='ScriptCat 脚本猫';                Url=('https://microsoftedge.microsoft.com/addons/search/' + [uri]::EscapeDataString('ScriptCat')); Note='Edge 商店搜索页' }
    @{ Cat='EdgeExt'; Name='SteamDB';                         Url=('https://microsoftedge.microsoft.com/addons/search/' + [uri]::EscapeDataString('SteamDB')); Note='Edge 商店搜索页' }
    @{ Cat='EdgeExt'; Name='Global Speed';                    Url=('https://microsoftedge.microsoft.com/addons/search/' + [uri]::EscapeDataString('Global Speed')); Note='Edge 商店搜索页' }
    @{ Cat='EdgeExt'; Name='AIX Downloader (AIX智能下载)';    Url=('https://microsoftedge.microsoft.com/addons/search/' + [uri]::EscapeDataString('AIX Downloader')); Note='Edge 商店搜索页' }
    @{ Cat='EdgeExt'; Name='ImageAssistant 图片助手';         Url=('https://microsoftedge.microsoft.com/addons/search/' + [uri]::EscapeDataString('ImageAssistant')); Note='Edge 商店搜索页' }
    @{ Cat='EdgeExt'; Name='NeatDownloadManager';             Url=('https://microsoftedge.microsoft.com/addons/search/' + [uri]::EscapeDataString('NeatDownloadManager')); Note='Edge 商店搜索页' }
    @{ Cat='EdgeExt'; Name='Listen1';                         Url=('https://microsoftedge.microsoft.com/addons/search/' + [uri]::EscapeDataString('Listen1')); Note='Edge 商店搜索页' }
    @{ Cat='EdgeExt'; Name='初音未来主题';                    Url=('https://microsoftedge.microsoft.com/addons/search/' + [uri]::EscapeDataString('Hatsune Miku theme')); Note='Edge 商店搜索页 (主题类)' }
)
foreach ($x in $extraDefs) {
    $dir = $catMap[$x.Cat]
    Add-App -Cat $catCn[$dir] -Name $x.Name -File '' -Old $null -Note $x.Note -Winget $x.Winget -Url $x.Url
}

# ---- 4) 输出 --------------------------------------------------------------
$categories = @($catCn.Keys | ForEach-Object { [ordered]@{ name = $catCn[$_]; dir = $_ } })

$plugin = [ordered]@{
    schema      = 1
    id          = 'official-base'
    name        = '官方基础软件'
    author      = '官方'
    version     = '2.0'
    description = ('按「完整名单」(清单\完整名单.txt) 生成的官方基础插件, 覆盖 9 个大类共 {0} 个应用, 云盘来源 {1}。名单改动后重跑 引擎\生成基础插件.ps1。' -f $apps.Count, '2KXljv-SqGSv')
    categories  = $categories
    apps        = $apps
}
$json = $plugin | ConvertTo-Json -Depth 8

# 自校验
$tmp = Join-Path $env:TEMP ('base_plugin_' + [guid]::NewGuid().ToString('N') + '.json')
[IO.File]::WriteAllText($tmp, $json, (New-Object Text.UTF8Encoding $false))
try {
    $p = Read-PluginFile -Path $tmp
    $errs = @(Test-Plugin -Plugin $p)
    if ($errs.Count) { $errs | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }; throw '生成的插件校验未通过' }
}
finally { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }

[IO.File]::WriteAllText($OutFile, $json, (New-Object Text.UTF8Encoding $false))
Write-Host ("已写入 {0}   ({1} 个分类 / {2} 个应用)" -f $OutFile, $categories.Count, $apps.Count) -ForegroundColor Green
Write-Host ("名单统计: [有] {0}   [补]跳过 {1}   [挪]跳过 {2}   其它状态 {3}   文档资产跳过 {4}   字段继承 {5}   名单外保留 {6}" -f `
    $stats.you, $stats.bu, $stats.nuo, $stats.other, $stats.asset, $carried, $leftover)
foreach ($d in $catCn.Keys) {
    $n = @($apps | Where-Object { $_.category -eq $catCn[$d] }).Count
    Write-Host ("  {0,-8} {1,3} 项   -> {2}" -f $catCn[$d], $n, $d)
}
if ($noFile.Count) {
    Write-Host "`n以下条目名单里没写文件名, 用应用名去云盘匹配 (装不上就在插件里手改 file):" 'WARN'
    $noFile | ForEach-Object { Write-Host ("  " + $_) -ForegroundColor DarkYellow }
}
if ($skipped.Count) {
    Write-Host "`n跳过的文档/资产:" 'WARN'
    $skipped | ForEach-Object { Write-Host ("  " + $_) -ForegroundColor DarkYellow }
}
