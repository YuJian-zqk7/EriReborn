#Requires -Version 5.1
<#
  生成目录.ps1  --  把 清单\*.csv 汇总成一张统一的 软件目录.csv

  目的: 启动器只需要读一张表就能列出"清单上所有软件"。
  本机已有 清单\3D建模.csv + winget_meta.json 也在合并范围内,
  winget 条目直接复用抓好的元数据键。

  规则:
    * 软件目录.csv 是生成产物, 不要手改 —— 改各自的清单再跑本脚本。
    * 安装子目录: 用英文目录, 不出现中文。3D建模这类需要独立根目录的
      在这里显式写出 (其余留空 = 用 \目录\名称)。
    * 来源 一列预先算好, 引擎与界面都只看这一列:
        winget   走 winget 静默安装
        official 走官网直链/官网页面自行下载
        package  本地安装包 (E:\安全软件 或 指定的包目录)
        dist     123 云盘分享文件, 由用户自己下载
        manual   只提示, 不自动装

  用法:  生成目录.ps1
#>
[CmdletBinding()]
param(
    [string] $Root     = 'E:\安装系统',
    [string] $OutFile,
    [switch] $PassThru
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$ManifestDir = Join-Path $Root '清单'
if (-not $OutFile) { $OutFile = Join-Path $ManifestDir '软件目录.csv' }

# 分类解析/英文名生成的规则收在 库\插件解析.ps1 里, 插件和工作区用的是同一套
. (Join-Path $Root '引擎\库\插件解析.ps1')

# ---------------------------------------------------------------- 全插件模式
# 官方基础插件 (插件\00-官方基础软件.json) 接管了这些分类 —— 清单 CSV 只是它的
# 数据源 (引擎\生成基础插件.ps1 负责翻译), 不再直接进目录, 避免"插件+清单"两份重复。
$basePluginFile = Join-Path (Join-Path $Root '插件') '00-官方基础软件.json'
$baseCats = @()
if (Test-Path $basePluginFile) {
    try {
        $bp = Get-Content -LiteralPath $basePluginFile -Raw -Encoding UTF8 | ConvertFrom-Json
        $baseCats = @($bp.categories | ForEach-Object { [string]$_.name })
        if ($baseCats.Count) {
            Write-Host ("官方基础插件已接管 {0} 个分类: {1}" -f $baseCats.Count, ($baseCats -join ', ')) -ForegroundColor Cyan
        }
    } catch {
        Write-Warning "官方基础插件解析失败, 按无插件处理: $($_.Exception.Message)"
        $baseCats = @()
    }
}

# 英文目录 → (分类显示名, 清单文件)
$registry = @(
    @{ Dir='security_clear'; Name='安全清理'; File='安全清理.csv' }
    @{ Dir='BasicTools';   Name='基础工具'; File='基础工具.csv' }
    @{ Dir='ChatSocial';   Name='聊天社交'; File='聊天社交.csv' }
    @{ Dir='CloudDrive';   Name='网盘';     File='网盘.csv' }
    @{ Dir='Download';     Name='下载';     File='下载.csv' }
    @{ Dir='Browser';      Name='浏览器';   File='浏览器.csv' }
    @{ Dir='NetAccel';     Name='网络加速'; File='网络加速.csv' }
    @{ Dir='Modeling3D';   Name='3D建模';   File='3D建模.csv' }
    @{ Dir='Peripheral';   Name='外设软件'; File='外设软件.csv' }
    @{ Dir='Runtime';      Name='组件';     File='组件.csv' }
    @{ Dir='Runtime';      Name='运行库';   File='运行库.csv' }
)

# 插件带来的自定义分类: 由 插件管理.ps1 汇总到 清单\用户分类.csv
$userCatFile = Join-Path $ManifestDir '用户分类.csv'
if (Test-Path $userCatFile) {
    foreach ($uc in (Import-Csv -LiteralPath $userCatFile -Encoding UTF8)) {
        $d = "$($uc.英文目录)".Trim()
        if (-not $d) { continue }
        if ($registry | Where-Object { $_.Dir -eq $d }) { continue }
        $registry += @{ Dir = $d; Name = "$($uc.类别)"; File = '' }
    }
}

function Pick {
    param($Row, [string[]]$Names)
    foreach ($n in $Names) {
        $p = $Row.PSObject.Properties[$n]
        if ($p -and "$($p.Value)".Trim()) { return "$($p.Value)".Trim() }
    }
    return ''
}

$rows = New-Object System.Collections.ArrayList
foreach ($cat in $registry) {
    if (-not $cat.File) { continue }        # 自定义分类没有对应的基础清单文件
    # 全插件模式: 官方基础插件存在时, 除运行库/组件外的分类全部由插件提供
    if ($baseCats.Count -and $cat.Name -notin @('运行库','组件')) { continue }
    $csv = Join-Path $ManifestDir $cat.File
    if (-not (Test-Path $csv -PathType Leaf)) { Write-Warning "缺清单: $csv"; continue }

    foreach ($r in (Import-Csv -LiteralPath $csv -Encoding UTF8)) {
        $pkg    = Pick $r @('分享文件名','包文件名')
        $url    = Pick $r @('官网直链','直链')
        $winget = Pick $r @('wingetId')
        $mode   = Pick $r @('安装方式')
        if (-not $mode) { $mode = '安装' }
        if ($mode -eq 'none') { $mode = '手动' }

        $source = 'manual'
        if ($winget)      { $source = 'winget' }
        elseif ($url)     { $source = 'official' }
        elseif ($pkg)     { $source = 'dist' }
        if ($mode -eq '手动') { $source = 'manual' }
        if ($source -eq 'manual' -and $pkg) { $source = 'dist' }   # 云盘里有包, 就不算"只能手动"
        # 3D建模本地包在 E:\Work 里备着, 优先按本地包处理
        if ($cat.Dir -eq 'Modeling3D' -and $pkg) {
            $source = if (Test-Path (Join-Path 'E:\Work' $pkg)) { 'package' } else { 'dist' }
        }

        # 每一项都显式给一个"纯英文目录名", 目标目录里就不可能出现中文
        $sub = Get-EnSlug -Text (Pick $r @('名称')) -Id (Pick $r @('Id'))
        [void]$rows.Add([pscustomobject][ordered]@{
            Id         = Pick $r @('Id')
            名称       = Pick $r @('名称')
            类别       = $cat.Name
            英文目录   = $cat.Dir
            层级       = $(if (Pick $r @('层级')) { Pick $r @('层级') } else { '推荐' })
            来源       = $source
            wingetId   = $winget
            安装方式   = $mode
            检测方式   = Pick $r @('检测方式')
            检测参数   = Pick $r @('检测参数')
            官网直链   = $url
            包文件名   = $pkg
            本地包     = $(if ($pkg) { 'yes' } else { '' })
            安装子目录 = $sub
            补丁程序   = Pick $r @('补丁程序')
            说明       = Pick $r @('说明','备注')
        })
    }
}

# ---- 插件带来的应用: 由 插件管理.ps1 汇总到 清单\插件目录.csv ----
$pluginIndex = Join-Path $ManifestDir '插件目录.csv'
$pluginCount = 0
if (Test-Path $pluginIndex) {
    foreach ($pr in (Import-Csv -LiteralPath $pluginIndex -Encoding UTF8)) {
        if (-not $pr.Id) { continue }
        $pluginCount++
        [void]$rows.Add([pscustomobject][ordered]@{
            Id         = $pr.Id
            名称       = $pr.名称
            类别       = $pr.类别
            英文目录   = $pr.英文目录
            层级       = $(if ($pr.层级) { $pr.层级 } else { '推荐' })
            来源       = $(if ($pr.来源) { $pr.来源 } else { 'local' })
            wingetId   = $pr.wingetId
            安装方式   = $(if ($pr.安装方式) { $pr.安装方式 } else { '便携' })
            检测方式   = $pr.检测方式
            检测参数   = $pr.检测参数
            官网直链   = $pr.官网直链
            包文件名   = $pr.包文件名
            本地包     = $(if ($pr.包文件名) { 'yes' } else { '' })
            安装子目录 = $(if ($pr.安装子目录) { $pr.安装子目录 } else { (Get-EnSlug -Text $pr.名称 -Id $pr.Id) })
            补丁程序   = $pr.补丁程序
            说明       = $(if ($pr.说明) { $pr.说明 } else { '来自插件: ' + $pr.插件 })
            插件       = $pr.插件
            插件Id     = $pr.插件Id
            分享地址   = $pr.分享地址
            静默参数   = $pr.静默参数
        })
    }
    Write-Host ("已并入插件应用 {0} 个 (来自 插件目录.csv)" -f $pluginCount) -ForegroundColor Cyan
}

$rows = @($rows | Where-Object { $_.Id -and $_.名称 })
# 必须显式 Select 一遍: Export-Csv 5.1 拿"第一行"的列当表头,
# 不 select 的话插件行多出来的列（插件 / 分享地址 / 静默参数）会被整列丢掉。
$cols = @('Id','名称','类别','英文目录','层级','来源','wingetId','安装方式','检测方式','检测参数',
          '官网直链','包文件名','本地包','安装子目录','补丁程序','说明','插件','插件Id','分享地址','静默参数')
$rows = @($rows | Select-Object $cols)
$rows | Export-Csv -LiteralPath $OutFile -NoTypeInformation -Encoding UTF8
Write-Host ("已写入 {0}  ({1} 项)" -f $OutFile, $rows.Count) -ForegroundColor Green

foreach ($g in ($rows | Group-Object 英文目录)) {
    $src = ($g.Group | Group-Object 来源 | ForEach-Object { "$($_.Name)×$($_.Count)" }) -join ' '
    Write-Host ("  {0,-12} {1,3} 项   {2}" -f $g.Name, $g.Count, $src)
}
if ($PassThru) { $rows }