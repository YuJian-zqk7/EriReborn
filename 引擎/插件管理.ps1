#Requires -Version 5.1
<#
  插件管理.ps1  --  插件体系的引擎侧

  一个插件 = 插件\<英文id>.json, 描述"我自己下载/制作的应用"该进哪个分类、从哪拿包、怎么装。
  放到 E:\安装系统\插件\ 就生效 —— 启动器下次重读清单就会列出来。

  插件格式 (v1, 只支持 123 云盘 + 官方链接 + winget + 本地文件):

  {
    "schema": 1,
    "id": "derek-tools",
    "name": "小明常用工具",
    "author": "小明",
    "version": "1.0",
    "description": "自用便携小工具集合",
    "categories": [                      // 自定义分类, 可选
      { "name": "我的工具", "dir": "MyTools" }
    ],
    "apps": [
      {
        "name": "我的截图工具",           // 显示名
        "id": "myshot",                  // 可选, 缺省按名字生成
        "category": "我的工具",           // 中文分类名 (内置或自定义)
        "dir": "MyTools",                // 可选, 直接指定英文目录名
        "sub": "MyShot",                 // 可选, 英文子目录名
        "tier": "推荐",                  // 核心/推荐/按需
        "source": "pan123",              // pan123 | official | winget | local | manual
        "shareUrl": "https://<uid>.share.123pan.cn/123pan/<key>",   // pan123 时必填
        "file": "我的截图工具.7z",        // pan123/local 时的文件名
        "url": "https://.../setup.exe",  // official 时必填 (可带 {FILENAME})
        "wingetId": "Publisher.App",     // winget 时必填
        "mode": "便携",                  // 安装 | 便携 | 手动
        "silent": "/S",                  // 安装类的静默参数, 可选
        "detect": { "method": "arp", "param": "MyShot" },
        "patch": "patch\\fix.exe",       // 可选, 解压后要跑的补丁
        "note": "说明"
      }
    ]
  }

  用法:
    插件管理.ps1                      列出所有插件并重建索引 (默认)
    插件管理.ps1 -List                只列插件
    插件管理.ps1 -RefreshOnly         不列, 只重建索引
    插件管理.ps1 -Validate <文件>      校验一个插件文件
    插件管理.ps1 -New <id> -Name <名>  生成一个插件骨架
#>
[CmdletBinding()]
param(
    [string] $Root        = 'E:\安装系统',
    [string] $PluginDir   = 'E:\安装系统\插件',
    [string] $LibDir      = 'E:\安装系统\引擎\库',
    [string] $UserCatFile = 'E:\安装系统\清单\用户分类.csv',
    [string] $IndexFile   = 'E:\安装系统\清单\插件目录.csv',
    [string] $Validate,
    [string] $New,
    [string] $Name,
    [string] $FromJson,
    [switch] $List,
    [switch] $RefreshOnly,
    [switch] $PassThru
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
. (Join-Path $LibDir 'SoftwareLibrary.ps1')
. (Join-Path $LibDir '插件解析.ps1')

if (-not (Test-Path $PluginDir)) { New-Item -ItemType Directory -Force -Path $PluginDir | Out-Null }

# ------------------------------------------------------------------ 从 JSON 写入 (工作区用)
if ($FromJson) {
    if (-not (Test-Path $FromJson -PathType Leaf)) { throw "找不到 JSON: $FromJson" }
    $p = Read-PluginFile -Path $FromJson
    $errs = @(Test-Plugin -Plugin $p)
    if ($errs.Count) {
        Write-Host '插件校验未通过:' -ForegroundColor Red
        $errs | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
        throw "插件有问题, 没有写入 ($($errs.Count) 处)"
    }
    $slug = Get-EnSlug -Text $p.id -Id 'plugin'
    $target = Join-Path $PluginDir ($slug + '.json')
    Copy-Item -LiteralPath $FromJson -Destination $target -Force
    Write-Host ("插件已写入: {0}  ({1} 个应用)" -f $target, $p.apps.Count) -ForegroundColor Green
    $skipList = $true     # 写完了直接往下重建索引
}
else { $skipList = $false }

# ------------------------------------------------------------------ 骨架
if ($New) {
    $slug = Get-EnSlug -Text $New -Id 'plugin'
    $file = Join-Path $PluginDir ($slug + '.json')
    if (Test-Path $file) { throw "插件已存在: $file" }
    $tpl = [ordered]@{
        schema = 1
        id = $slug
        name = $(if ($Name) { $Name } else { $slug })
        author = $env:USERNAME
        version = '1.0'
        description = ''
        categories = @()
        apps = @(
            [ordered]@{
                name = '示例应用'
                id = 'sample'
                category = '我的工具'
                dir = 'MyTools'
                sub = 'Sample'
                tier = '推荐'
                source = 'pan123'
                shareUrl = 'https://xxx.share.123pan.cn/123pan/xxxxxxxx'
                file = '示例应用.7z'
                mode = '便携'
                detect = [ordered]@{ method = 'none'; param = '' }
                note = '把这里改成你自己的'
            }
        )
    }
    [IO.File]::WriteAllText($file, ($tpl | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding $false))
    Write-Host "已生成插件骨架: $file" -ForegroundColor Green
    return
}

# ------------------------------------------------------------------ 校验单个文件
if ($Validate) {
    $p = Read-PluginFile -Path $Validate
    $errs = Test-Plugin -Plugin $p
    if ($errs.Count) {
        Write-Host "校验失败 ($($errs.Count) 处):" -ForegroundColor Red
        $errs | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
        return
    }
    Write-Host "校验通过: $($p.name)   应用 $($p.apps.Count) 个" -ForegroundColor Green
    $p.apps | ForEach-Object { Write-Host ("  {0,-8} {1,-24} {2}" -f $_.dir, $_.name, $_.source) }
    return
}

# ------------------------------------------------------------------ 载入全部插件
$plugins = @(Get-InstalledPlugins -PluginDir $PluginDir)
$bad = @($plugins | Where-Object { $_.invalid })
$good = @($plugins | Where-Object { -not $_.invalid })

if (-not $skipList -and -not $RefreshOnly) {
    Write-Host ''
    Write-Host ("插件目录: {0}" -f $PluginDir) -ForegroundColor Cyan
    if (-not $plugins.Count) {
        Write-Host '  （还没有插件。启动器「插件工作区」里点几下就能做一个）' -ForegroundColor DarkGray
    }
    foreach ($p in $plugins) {
        if ($p.invalid) {
            Write-Host ("  [无效] {0}" -f $p.file) -ForegroundColor Red
            $p.errors | ForEach-Object { Write-Host "         $_" -ForegroundColor Red }
            continue
        }
        Write-Host ("  [{0}] {1}  应用 {2} 个  分类: {3}" -f `
            $(if ($p.author) { $p.author } else { '匿名' }), $p.name, $p.apps.Count,
            (($p.apps | Select-Object -ExpandProperty dir -Unique) -join ', '))
    }
}

# ------------------------------------------------------------------ 汇总用户分类
$userCats = @()
foreach ($p in $good) {
    foreach ($c in $p.categories) { $userCats += [pscustomobject]@{ 类别 = $c.name; 英文目录 = $c.dir; 插件 = $p.name } }
}
$userCats = @($userCats | Sort-Object 英文目录 -Unique)
if ($userCats.Count) {
    $userCats | Export-Csv -LiteralPath $UserCatFile -NoTypeInformation -Encoding UTF8
    Write-Host ("已更新用户分类: {0} ({1} 个自定义分类)" -f $UserCatFile, $userCats.Count)
}
elseif (Test-Path $UserCatFile) {
    Remove-Item $UserCatFile -Force
    Write-Host '没有自定义分类了, 已清掉 用户分类.csv'
}

# ------------------------------------------------------------------ 汇总插件目录
$indexRows = New-Object System.Collections.ArrayList
foreach ($p in $good) {
    foreach ($a in $p.apps) {
        [void]$indexRows.Add([pscustomobject][ordered]@{
            Id         = $a.id
            名称       = $a.name
            类别       = $a.category
            英文目录   = $a.dir
            层级       = $a.tier
            来源       = $a.source
            wingetId   = $a.wingetId
            安装方式   = $a.mode
            检测方式   = $a.detectMethod
            检测参数   = $a.detectParam
            官网直链   = $a.url
            包文件名   = $a.file
            本地包     = $(if ($a.file) { 'yes' } else { '' })
            安装子目录 = $a.sub
            补丁程序   = $a.patch
            说明       = $a.note
            插件       = $p.name
            插件Id     = $p.id
            分享地址   = $a.shareUrl
            静默参数   = $a.silent
        })
    }
}
$indexRows | Export-Csv -LiteralPath $IndexFile -NoTypeInformation -Encoding UTF8
Write-Host ("插件索引: {0} ({1} 个应用)" -f $IndexFile, $indexRows.Count) -ForegroundColor Green

if ($PassThru) { $indexRows }
