#Requires -Version 5.1
<#
  导出状态.ps1  --  给图形启动器用的状态导出

  界面只做壳, 检测逻辑全部复用 库\检测.ps1 与 库\硬件.ps1, 保证 GUI 与
  命令行引擎看到的结论完全一致, 不会出现两套判断分叉。

  JSON 的键一律用英文, 值才是中文 —— C# 侧取字段干净。

  用法:
    导出状态.ps1                    输出到 stdout
    导出状态.ps1 -Out 状态.json     写入文件
    导出状态.ps1 -WithWu            连 Windows Update 驱动列表一起查 (慢 15~90 秒)
#>
[CmdletBinding()]
param(
    [string] $Manifest   = 'E:\安装系统\清单\运行库.csv',
    [string] $MetaFile   = 'E:\安装系统\清单\winget_meta.json',
    [string] $SourceCsv  = 'E:\安装系统\清单\驱动源.csv',
    [string] $PeriphCsv  = 'E:\安装系统\清单\外设软件.csv',
    [string] $LibDir     = 'E:\安装系统\引擎\库',
    [string] $Out,
    [switch] $WithWu,
    [switch] $RuntimesOnly
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

. (Join-Path $LibDir '检测.ps1')
. (Join-Path $LibDir '硬件.ps1')
. (Join-Path $LibDir 'SoftwareLibrary.ps1')

# ---------------------------------------------------------------- 统一软件目录
# 软件目录.csv 由 引擎\生成目录.ps1 汇总, 是"清单上所有软件"的唯一来源。
# 界面上只需要读这一份, 就能列出全部软件 + 目标英文目录 + 怎么落地。
$software = New-Object System.Collections.ArrayList
$catalogPath = Join-Path (Split-Path $Manifest -Parent) '软件目录.csv'
$pkgDirs = @('E:\安全软件', 'E:\安装系统\缓存\安装包', 'E:\Work')
$targetRoot = 'E:\Apps'
if (Test-Path $catalogPath) {
    . (Join-Path $LibDir '网盘目录.ps1')
    $cred = Get-Pan123Credential
    $arpForSw = Get-ArpTable
    foreach ($it in (Get-SoftwareCatalog -Path $catalogPath)) {
        $st = Test-SoftwareInstalled -Item $it -Roots @($targetRoot) -Arp $arpForSw
        $pkgFile = Get-LocalPackageFile -Item $it -Dirs $pkgDirs
        $pkgReady = [bool]$pkgFile
        # 云盘 / 官网 / winget / 手动 —— 界面按这个给按钮
        $how = switch ($it.source) {
            'winget'   { 'winget' }
            'package'  { if ($pkgReady) { '本地安装包' } else { '云盘自取' } }
            'dist'     { if ($pkgReady) { '本地安装包' } else { '云盘自取' } }
            'pan123'   { if ($pkgReady) { '本地安装包' } else { '云盘自取' } }
            'official' { '官网下载' }
            'local'    { if ($pkgReady) { '本地安装包' } else { '本地包缺失' } }
            default    { '手动' }
        }
        [void]$software.Add([ordered]@{
            id=$it.id; name=$it.name; category=$it.category; dir=$it.dir; tier=$it.tier
            source=$it.source; wingetId=$it.wingetId; mode=$it.mode
            archive=$it.archive; official=$it.official; patch=$it.patch
            dest=(Get-SoftwareDestDir -Item $it -Root $targetRoot)
            packageReady=$pkgReady
            packageFile=$(if ($pkgFile) { $pkgFile.FullName } else { '' })
            installed=[bool]$st.installed; detected=[bool]$st.detected
            evidence=$st.evidence; how=$how; note=$it.note
            plugin=$it.plugin; pluginId=$it.pluginId; shareUrl=$it.shareUrl
        })
    }
    $software = @($software | Sort-Object @{E={$_.dir}}, @{E={@{'核心'=0;'推荐'=1;'按需'=2}[$_.tier]}}, @{E={$_.name}})
}

# ---------------------------------------------------------------- 运行库
$items = Import-Csv -LiteralPath $Manifest -Encoding UTF8
$meta  = @{}
if (Test-Path $MetaFile) {
    foreach ($m in (Get-Content $MetaFile -Raw -Encoding UTF8 | ConvertFrom-Json)) { $meta[$m.Id] = $m }
}

$runtimes = New-Object System.Collections.ArrayList
foreach ($it in $items) {
    $s = Test-RuntimeItem -Item $it
    $m = if ($it.wingetId -and $meta.ContainsKey($it.wingetId)) { $meta[$it.wingetId] } else { $null }
    [void]$runtimes.Add([ordered]@{
        id        = $it.Id
        name      = $it.名称
        category  = $it.类别
        tier      = $it.层级
        wingetId  = $it.wingetId
        source    = $(if ($it.直链) { '直链' } elseif ($it.wingetId) { 'winget' } else { '无' })
        available = $(if ($m) { [string]$m.Version } else { '' })
        arch      = $it.架构
        note      = $it.备注
        installed = [bool]$s.Installed
        detected  = [bool]$s.Detected
        status    = $(if (-not $s.Detected) { '未知' } elseif ($s.Installed) { '已安装' } else { '缺失' })
        version   = [string]$s.Version
        evidence  = $s.Evidence
    })
}

# ---------------------------------------------------------------- 3D 建模
$shareModel = 'https://1828566527.share.123pan.cn/123pan/2KXljv-OaNUv'
$shareClean = 'https://1828566527.share.123pan.cn/123pan/2KXljv-YXcuv'
$arpNames = @(Get-ArpTable | ForEach-Object { "$($_.Name) $($_.KeyName)" })

function Test-ArpHit { param([string]$Pattern)
    if (-not $Pattern) { return $null }
    return ($arpNames | Where-Object { $_ -match $Pattern } | Select-Object -First 1)
}

$modeling = @()
$modelCsv = Join-Path (Split-Path $Manifest -Parent) '3D建模.csv'
if (Test-Path $modelCsv) {
    $modeling = @(Import-Csv -LiteralPath $modelCsv -Encoding UTF8 | ForEach-Object {
        $have = $false
        if ($_.包文件名) { $have = Test-Path (Join-Path 'E:\Work' $_.包文件名) }
        $inst = $false; $ev = '不做检测'
        if ($_.检测方式 -eq 'arp') {
            $hit = Test-ArpHit $_.检测参数
            if ($hit) { $inst = $true; $ev = "ARP 命中: $hit" } else { $ev = 'ARP 中无匹配' }
        }
        [ordered]@{
            id=$_.Id; name=$_.名称; category=$_.类别; tier=$_.层级
            archive=$_.包文件名; size=[int64]$_.包大小
            payload=$have; installed=$inst; evidence=$ev; note=$_.说明
        }
    })
}

# ---------------------------------------------------------------- 本机已装组件 (驱动/外设)
$components = @()
$compCsv = Join-Path (Split-Path $Manifest -Parent) '组件.csv'
if (Test-Path $compCsv) {
    $components = @(Import-Csv -LiteralPath $compCsv -Encoding UTF8 | ForEach-Object {
        $s = Test-RuntimeItem -Item $_
        [ordered]@{
            id=$_.Id; name=$_.名称; category=$_.类别
            installed=[bool]$s.Installed; detected=[bool]$s.Detected
            version=[string]$s.Version; evidence=$s.Evidence
        }
    })
}

# ---------------------------------------------------------------- 基础工具 + 其它类别
# 读 类别.csv 注册表, 把除"安全清理"外的所有类别合并成一个列表给界面。
# 以后加类别只改 类别.csv, 不用动代码和界面。
$basics = @()
$catCsv = Join-Path (Split-Path $Manifest -Parent) '类别.csv'
if (Test-Path $catCsv) {
    foreach ($cat in (Import-Csv -LiteralPath $catCsv -Encoding UTF8)) {
        if ($cat.英文目录 -eq 'security_clear') { continue }
        $mcsv = Join-Path (Split-Path $Manifest -Parent) $cat.清单文件
        if (-not (Test-Path $mcsv)) { continue }
        $mdir = Join-Path 'E:\Apps' $cat.英文目录
        foreach ($row in (Import-Csv -LiteralPath $mcsv -Encoding UTF8)) {
            $inst = $false; $ev = '不做检测'
            $folder = Join-Path $mdir $row.名称
            if (Test-Path $folder) {
                $exes = @(Get-ChildItem $folder -Recurse -Filter *.exe -File -ErrorAction SilentlyContinue)
                if ($exes.Count) { $inst = $true; $ev = "便携目录: $($exes[0].Name)" } else { $ev = '目录无 exe' }
            }
            elseif ($row.检测方式 -eq 'arp' -and $row.检测参数) {
                $hit = Test-ArpHit $row.检测参数
                if ($hit) { $inst = $true; $ev = "ARP 命中: $hit" } else { $ev = 'ARP 中无匹配' }
            }
            $pkg = if ($row.分享文件名) { Test-Path (Join-Path 'E:\安全软件' $row.分享文件名) } else { $false }
            $basics += [ordered]@{
                id=$row.Id; name=$row.名称; category=($cat.类别 + '·' + $row.类别); tier=$row.层级
                shareFile=$row.分享文件名; official=$row.官网直链; mode=$row.安装方式
                package=$pkg; installed=$inst; evidence=$ev; note=$row.说明
                补丁程序=$row.补丁程序
            }
        }
    }
}
# ---------------------------------------------------------------- 安全清理
$cleanup = @()
$cleanCsv   = Join-Path (Split-Path $Manifest -Parent) '安全清理.csv'
$cleanDir   = 'E:\Apps\security_clear'
$cleanPkg   = 'E:\安全软件'
if (Test-Path $cleanCsv) {
    $cleanup = @(Import-Csv -LiteralPath $cleanCsv -Encoding UTF8 | ForEach-Object {
        $inst = $false; $ev = '不做检测'
        $folder = Join-Path $cleanDir $_.名称
        if (Test-Path $folder) {
            # 便携类: 目录里必须真有 exe 才算装上 —— 否则解压一半也会被当成装好
            $exes = @(Get-ChildItem $folder -Recurse -Filter *.exe -File -ErrorAction SilentlyContinue)
            if ($exes.Count) { $inst = $true; $ev = "便携目录: $($exes[0].Name)" }
            else { $ev = '目录存在但无可执行文件' }
        }
        elseif ($_.检测方式 -eq 'arp') {
            $hit = Test-ArpHit $_.检测参数
            if ($hit) { $inst = $true; $ev = "ARP 命中: $hit" } else { $ev = 'ARP 中无匹配' }
        }
        $pkg = if ($_.分享文件名) { Test-Path (Join-Path $cleanPkg $_.分享文件名) } else { $false }
        [ordered]@{
            id=$_.Id; name=$_.名称; category=$_.类别; tier=$_.层级
            shareFile=$_.分享文件名; official=$_.官网直链; mode=$_.安装方式
            package=$pkg; installed=$inst; evidence=$ev; note=$_.说明
        }
    })
}

# --- 只导出运行库 (秒级) -------------------------------------------------
if ($RuntimesOnly) {
    $o = Get-OemSupportUrl
    $payload = [ordered]@{
        generated = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
        machine   = [ordered]@{
            oem=[string]$o.Manufacturer; model=[string]$o.Model
            brand=[string]$o.Brand; oemUrl=[string]$o.Url
            os="$((Get-CimInstance Win32_OperatingSystem).Caption)"; deviceCount=0
        }
        runtimes=$runtimes; vendors=@(); problems=@(); display=@(); net=@(); audio=@()
        modeling=$modeling; cleanup=$cleanup; components=$components; basics=$basics
        software=$software
        shares=[ordered]@{ modeling=$shareModel; cleanup=$shareClean }
        driverSources=@(); peripherals=@(); wuDrivers=@(); wuFirmware=@()
        wuError=$null; wuQueried=$false; partial=$true
    }
    $json = $payload | ConvertTo-Json -Depth 8 -Compress
    if ($Out) { [IO.File]::WriteAllText($Out, $json, (New-Object Text.UTF8Encoding $false)) }
    else { $json }
    return
}
# ---------------------------------------------------------------- 硬件
$hw  = Get-HardwareInventory
$oem = Get-OemSupportUrl
$vendors = @(Get-HardwareSummary | ForEach-Object {
    [ordered]@{ name=$_.VendorName; count=$_.Count; classes=$_.Classes; hasSource=[bool]$_.有厂商源 }
})

$problems = @(Get-ProblemDevice | ForEach-Object {
    [ordered]@{
        name=   [string]$_.Name
        reason= [string]$_.状态说明
        phantom=[bool]$_.IsPhantom
        vendor= [string]$_.VendorName
        vid=    [string]$_.VendorId
        dev=    [string]$_.DevId
        cls=    [string]$_.Class
        instance=[string]$_.InstanceId
        driver= [string]$_.DriverVer
    }
})

$display = @(Get-HardwareInventory -Class Display | ForEach-Object {
    [ordered]@{
        name=$_.Name; vendor=$_.VendorName; vid=$_.VendorId; dev=$_.DevId
        subVendor=$_.SubVendor; driver=$_.DriverVer; date=[string]$_.DriverDate
        problem=($_.Status -ne 0)
    }
})
$net = @(Get-HardwareInventory -Class Net | ForEach-Object {
    [ordered]@{ name=$_.Name; vendor=$_.VendorName; vid=$_.VendorId; dev=$_.DevId; driver=$_.DriverVer }
})
$audio = @(Get-HardwareInventory -Class MEDIA | ForEach-Object {
    [ordered]@{ name=$_.Name; vendor=$_.VendorName; vid=$_.VendorId; dev=$_.DevId; driver=$_.DriverVer }
})

# ---------------------------------------------------------------- 驱动源
$vendorsPresent = @($hw | Where-Object { $_.VendorName } | Select-Object -ExpandProperty VendorName -Unique)
$driverSources = @()
if (Test-Path $SourceCsv) {
    $driverSources = @(Import-Csv -LiteralPath $SourceCsv -Encoding UTF8 | ForEach-Object {
        $hit = switch ($_.匹配类型) {
            '厂商'   { $vendorsPresent -contains $_.匹配值 }
            '整机厂' { "$($oem.Manufacturer)" -like "*$($_.匹配值)*" }
            '内置'   { $true }
            default  { $false }
        }
        if (-not $hit) { return }
        [ordered]@{
            id=$_.Id; name=$_.名称; category=$_.类别; match=$_.匹配值
            auto=($_.自动 -eq '是'); url=$_.地址; note=$_.说明
        }
    })
}

# ---------------------------------------------------------------- 外设软件
$peripherals = @()
if (Test-Path $PeriphCsv) {
    $peripherals = @(Import-Csv -LiteralPath $PeriphCsv -Encoding UTF8 | ForEach-Object {
        $auto = ($_.匹配类型 -eq '厂商' -and $vendorsPresent -contains $_.匹配值)
        if (-not $auto) { return }
        [ordered]@{ id=$_.Id; name=$_.名称; category=$_.类别; wingetId=$_.wingetId; note=$_.说明 }
    })
}

# ---------------------------------------------------------------- WU 驱动
$wuDrivers = @(); $wuFirmware = @(); $wuError = $null
if ($WithWu) {
    try {
        $session  = New-Object -ComObject Microsoft.Update.Session
        $searcher = $session.CreateUpdateSearcher()
        $searcher.ServerSelection = 0
        $res = $searcher.Search("IsInstalled=0 and Type='Driver'")
        foreach ($u in $res.Updates) {
            $isFw = ($u.Title -match 'Firmware|BIOS|UEFI|EC Firmware') -or ("$($u.DriverClass)" -match 'Firmware')
            $row = [ordered]@{
                title=[string]$u.Title; cls=[string]$u.DriverClass; mfr=[string]$u.DriverManufacturer
                kb=$(if ($u.KBArticleIDs.Count) { "KB$($u.KBArticleIDs.Item(0))" } else { '' })
                firmware=$isFw
            }
            if ($isFw) { $wuFirmware += $row } else { $wuDrivers += $row }
        }
    }
    catch { $wuError = $_.Exception.Message }
}

# ---------------------------------------------------------------- 组装
$payload = [ordered]@{
    generated = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    machine   = [ordered]@{
        oem=[string]$oem.Manufacturer; model=[string]$oem.Model
        brand=[string]$oem.Brand; oemUrl=[string]$oem.Url
        os="$((Get-CimInstance Win32_OperatingSystem).Caption)"
        deviceCount=$hw.Count
    }
    runtimes      = $runtimes
    vendors       = $vendors
    problems      = $problems
    display       = $display
    net           = $net
    audio         = $audio
    driverSources = $driverSources
    peripherals   = $peripherals
    wuDrivers     = $wuDrivers
    wuFirmware    = $wuFirmware
    wuError       = $wuError
    wuQueried     = [bool]$WithWu
    modeling      = $modeling
    cleanup       = $cleanup
    components    = $components
    basics        = $basics
    software      = $software
    shares        = [ordered]@{ modeling=$shareModel; cleanup=$shareClean }
}

$json = $payload | ConvertTo-Json -Depth 8 -Compress
if ($Out) {
    [IO.File]::WriteAllText($Out, $json, (New-Object Text.UTF8Encoding $false))
    Write-Host "已写入 $Out ($($json.Length) 字节)"
}
else { $json }
