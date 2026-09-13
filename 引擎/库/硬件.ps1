# =====================================================================
#  硬件.ps1  --  hardware detection library (dot-source this)
#
#  跨设备原则: 只依据设备自报的 PCI/USB 厂商号与设备号判断, 不写死
#  "这台机器是 NVIDIA" / "这台是联想". 换一台机器跑同一套代码, 结论
#  自动跟着变。
#
#  公开入口:
#     Get-HardwareInventory      → 全机硬件清单 (含厂商归属与驱动状态)
#     Get-ProblemDevice          → 缺驱动/有问题的设备
#     Get-OemSupportUrl          → 整机厂支持页地址
#     Resolve-DriverSource       → 给定一个设备, 给出该去哪拿驱动
# =====================================================================

# --- PCI / USB 厂商号 → 厂商 -------------------------------------------
$script:_HwCache = @{}
$script:_DrvCache = $null

$script:VendorMap = @{
    '8086' = 'Intel';      '8087' = 'Intel';      '10DE' = 'NVIDIA';   '0955' = 'NVIDIA'
    '1002' = 'AMD';        '1022' = 'AMD';        '10EC' = 'Realtek';  '0BDA' = 'Realtek'
    '14E4' = 'Broadcom';   '11AB' = 'Marvell';    '1969' = 'Qualcomm/Atheros'
    '168C' = 'Qualcomm/Atheros'; '1814' = 'MediaTek'; '0E8D' = 'MediaTek'
    '1B21' = 'ASMedia';    '1D6B' = 'Linux Foundation'
    '046D' = 'Logitech';   '1532' = 'Razer';      '056A' = 'Wacom'
    '04F3' = 'ELAN';       '06CB' = 'Synaptics';  '04D9' = 'Holtek'
    '0951' = 'Kingston';   '0781' = 'SanDisk';    '0BC2' = 'Seagate'
    '04E8' = 'Samsung';    '18D1' = 'Google';     '054C' = 'Sony'
    '057E' = 'Nintendo';   '045E' = 'Microsoft';  '0B05' = 'ASUS'
    '0957' = 'Agilent';    '0925' = 'Syntek';     '1A86' = 'QinHeng'
    '0483' = 'STMicro';    '2341' = 'Arduino';    '048D' = 'ITE'
    '0458' = 'KYE/Genius'; '093A' = 'Pixart';     '0C45' = 'Sonix'
    '0AC8' = 'Vimicro';    '1BCF' = 'SunplusIT';  '0D8C' = 'C-Media'
    '1235' = 'Focusrite';  '0763' = 'M-Audio';    '0582' = 'Roland'
    '17AA' = 'Lenovo';     '1E49' = 'YMTC/长江存储'; '2646' = 'Kingston'
    '5986' = 'Bison/摄像头'; '256C' = '通用HID外设';  '00FF' = '虚拟/占位设备'
    '275D' = '通用HID外设'
}

# --- 厂商官方驱动源 -----------------------------------------------------
# auto = 该源可以被脚本自动获取并安装; 否则只做到"直达正确页面"
$script:DriverSource = @{
    'NVIDIA' = [pscustomobject]@{
        Kind = '厂商'; Auto = $false
        Name = 'NVIDIA 驱动下载 (按型号查询)'
        Url  = 'https://www.nvidia.com/en-us/drivers/'
        Note = '按设备号自动查询的旧接口(AjaxDriverService LookupValueSearch / DriverService*.asp)已全部下线，剩下的那个需要厂商内部产品号 psid/pfid，无法从设备 ID 推导。改为直达官方查询页。'
    }
    'AMD' = [pscustomobject]@{
        Kind = '厂商'; Auto = $false
        Name = 'AMD 驱动与支持'
        Url  = 'https://www.amd.com/en/support/download/drivers.html'
        Note = '官方页面按产品型号检索'
    }
    'Intel' = [pscustomobject]@{
        Kind = '厂商'; Auto = $false
        Name = 'Intel 驱动下载中心'
        Url  = 'https://www.intel.com/content/www/us/en/download-center/home.html'
        Note = '实测 Intel 显卡驱动会通过 Windows Update 推送(本机可见 32.0.101.7076)，自动化优先走 WU'
    }
    'Realtek' = [pscustomobject]@{
        Kind = '厂商'; Auto = $false
        Name = 'Realtek 下载中心'
        Url  = 'https://www.realtek.com/Download/List?cate_id=584'
        Note = '网卡/声卡驱动；笔记本机型建议优先用 OEM 版本'
    }
    'Qualcomm/Atheros' = [pscustomobject]@{
        Kind = '厂商'; Auto = $false
        Name = 'Qualcomm 无线驱动'
        Url  = 'https://www.qualcomm.com/support/product-security/wifi'
        Note = '无线网卡驱动通常由 OEM 提供'
    }
    'MediaTek' = [pscustomobject]@{
        Kind = '厂商'; Auto = $false
        Name = 'MediaTek 支持'
        Url  = 'https://www.mediatek.com/products/broadband-wifi'
        Note = ''
    }
    'Broadcom' = [pscustomobject]@{
        Kind = '厂商'; Auto = $false
        Name = 'Broadcom 支持'
        Url  = 'https://www.broadcom.com/support'
        Note = ''
    }
}

# --- 整机厂支持页 -------------------------------------------------------
$script:OemMap = @{
    'LENOVO'  = @{ Name='联想'; Url='https://pcsupport.lenovo.com/' }
    'ASUS'    = @{ Name='华硕'; Url='https://www.asus.com.cn/support/' }
    'ACER'    = @{ Name='宏碁'; Url='https://www.acer.com.cn/support' }
    'DELL'    = @{ Name='戴尔'; Url='https://www.dell.com/support/home/zh-cn' }
    'HP'      = @{ Name='惠普'; Url='https://support.hp.com/cn-zh' }
    'HEWLETT' = @{ Name='惠普'; Url='https://support.hp.com/cn-zh' }
    'MSI'     = @{ Name='微星'; Url='https://cn.msi.com/support' }
    'MICRO-STAR' = @{ Name='微星'; Url='https://cn.msi.com/support' }
    'GIGABYTE'= @{ Name='技嘉'; Url='https://www.gigabyte.cn/Support' }
    'SAMSUNG' = @{ Name='三星'; Url='https://www.samsung.com/cn/support/' }
    'HUAWEI'  = @{ Name='华为'; Url='https://consumer.huawei.com/cn/support/' }
    'XIAOMI'  = @{ Name='小米'; Url='https://www.mi.com/service/' }
    'MECHREVO'= @{ Name='机械革命'; Url='https://www.mechrevo.com/' }
    'THUNDEROBOT' = @{ Name='雷神'; Url='https://www.thunderobot.com/' }
    'HASEe'   = @{ Name='神舟'; Url='https://www.hasee.com/' }
    'MICROSOFT' = @{ Name='微软'; Url='https://support.microsoft.com/zh-cn/surface' }
}

function Get-VendorName {
    param([string]$VendorId)
    if (-not $VendorId) { return $null }
    $v = $VendorId.ToUpper()
    if ($script:VendorMap.ContainsKey($v)) { return $script:VendorMap[$v] }
    return "未知($v)"
}

# 从一串硬件 ID 里抽出 VEN_/DEV_/SUBSYS_/VID_/PID_
function Parse-HardwareIds {
    param([string[]]$HwIds)
    $r = [ordered]@{ VendorId=$null; DeviceId=$null; SubSys=$null; SubVendor=$null
                     Vid=$null; Pid=$null; Raw=@($HwIds) }
    foreach ($h in $HwIds) {
        if (-not $h) { continue }
        if (-not $r.VendorId -and $h -match 'VEN_([0-9A-Fa-f]{4})') { $r.VendorId = $Matches[1].ToUpper() }
        if (-not $r.DeviceId -and $h -match 'DEV_([0-9A-Fa-f]{4})') { $r.DeviceId = $Matches[1].ToUpper() }
        if (-not $r.SubSys   -and $h -match 'SUBSYS_([0-9A-Fa-f]{8})') {
            $r.SubSys = $Matches[1].ToUpper()
            $r.SubVendor = $r.SubSys.Substring(4,4)     # SUBSYS_ 后 4 位是子系统厂商
        }
        if (-not $r.Vid -and $h -match 'VID_([0-9A-Fa-f]{4})') { $r.Vid = $Matches[1].ToUpper() }
        if (-not $r.Pid -and $h -match 'PID_([0-9A-Fa-f]{4})') { $r.Pid = $Matches[1].ToUpper() }
    }
    return [pscustomobject]$r
}

# ---------------------------------------------------------------------
#  全机硬件清单
# ---------------------------------------------------------------------
function Get-HardwareInventory {
    param(
        [string[]]$Class = @('Display','MEDIA','AudioEndpoint','Net','Bluetooth','Camera',
                             'USB','USBDevice','HIDClass','Keyboard','Mouse','Monitor',
                             'SCSIAdapter','DiskDrive','PrintQueue','Image','SmartCardReader')
    )
    $cacheKey = if ($Class) { ($Class -join ',') } else { '*' }
    if ($script:_HwCache.ContainsKey($cacheKey)) { return $script:_HwCache[$cacheKey] }

    # 驱动版本/日期只有 Win32_PnPSignedDriver 有, 先建索引
    if (-not $script:_DrvCache) {
        $script:_DrvCache = @{}
        foreach ($d in (Get-CimInstance Win32_PnPSignedDriver -ErrorAction SilentlyContinue)) {
            if ($d.DeviceID) { $script:_DrvCache[$d.DeviceID] = $d }
        }
    }
    $drvIdx = $script:_DrvCache

    $out = New-Object System.Collections.ArrayList
    foreach ($p in (Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue)) {
        if ($Class -and ($Class -notcontains $p.PNPClass)) { continue }

        $ids  = Parse-HardwareIds -HwIds $p.HardwareID
        $vend = Get-VendorName -VendorId $(if ($ids.VendorId) { $ids.VendorId } else { $ids.Vid })
        $d    = if ($p.DeviceID -and $drvIdx.ContainsKey($p.DeviceID)) { $drvIdx[$p.DeviceID] } else { $null }

        [void]$out.Add([pscustomobject]@{
            Class       = $p.PNPClass
            Name        = $p.Name
            InstanceId  = $p.DeviceID
            IsPhantom   = ("$($p.DeviceID)" -like 'ROOT\*')
            VendorId    = $(if ($ids.VendorId) { $ids.VendorId } else { $ids.Vid })
            VendorName  = $vend
            DevId       = $(if ($ids.DeviceId) { $ids.DeviceId } else { $ids.Pid })
            SubSys      = $ids.SubSys
            SubVendor   = $(if ($ids.SubVendor) { Get-VendorName $ids.SubVendor } else { $null })
            Status      = $p.ConfigManagerErrorCode
            Problem     = ($p.ConfigManagerErrorCode -ne 0)
            DriverVer   = $(if ($d) { $d.DriverVersion } else { $null })
            DriverDate  = $(if ($d) { $d.DriverDate } else { $null })
            DriverProv  = $(if ($d) { $d.DriverProviderName } else { $null })
            HardwareIds = $ids.Raw
        })
    }
    $script:_HwCache[$cacheKey] = $out
    return $out
}

# ---------------------------------------------------------------------
#  缺驱动的设备
#  状态码 28 = 驱动未安装, 1 = 未正确配置, 10 = 无法启动, 18 = 需重装驱动
# ---------------------------------------------------------------------
$script:ProblemText = @{
    1  = '设备未正确配置'
    3  = '驱动已损坏或内存不足'
    10 = '设备无法启动'
    12 = '资源不足'
    14 = '需重启才能生效'
    18 = '需重新安装驱动'
    19 = '注册表配置损坏'
    21 = 'Windows 正在移除设备'
    22 = '设备已被禁用'
    24 = '设备未找到'
    28 = '未安装驱动程序'      # 最常见
    31 = 'Windows 无法加载所需驱动'
    43 = '设备报告故障'
    45 = '设备当前未连接'
}

function Get-ProblemDevice {
    $all = Get-HardwareInventory -Class $null
    $bad = $all | Where-Object { $_.Problem } |
           Sort-Object Status, Class, Name
    foreach ($b in $bad) {
        $b | Add-Member -NotePropertyName 状态说明 -NotePropertyValue $(
            if ($script:ProblemText.ContainsKey([int]$b.Status)) { $script:ProblemText[[int]$b.Status] }
            else { "错误码 $($b.Status)" }
        ) -Force
        $b | Add-Member -NotePropertyName 真实性 -NotePropertyValue $(
            if ($b.IsPhantom) { '虚拟/残留节点' } else { '真实硬件' }
        ) -Force
    }
    return $bad
}

# ---------------------------------------------------------------------
#  整机厂支持页
# ---------------------------------------------------------------------
function Get-OemSupportUrl {
    $cs = Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue
    if (-not $cs) { return $null }
    $mfr = "$($cs.Manufacturer)".ToUpper()
    foreach ($k in $script:OemMap.Keys) {
        if ($mfr -like "*$k*") {
            $o = $script:OemMap[$k]
            return [pscustomobject]@{
                Manufacturer = $cs.Manufacturer
                Model        = $cs.Model
                Brand        = $o.Name
                Url          = $o.Url
                SearchUrl    = "$($o.Url)"
            }
        }
    }
    return [pscustomobject]@{
        Manufacturer = $cs.Manufacturer; Model = $cs.Model
        Brand = $cs.Manufacturer; Url = $null; SearchUrl = $null
    }
}

# ---------------------------------------------------------------------
#  给定一个设备, 决定该去哪拿驱动
# ---------------------------------------------------------------------
function Resolve-DriverSource {
    param([Parameter(Mandatory)] $Device)

    $vendor = $Device.VendorName
    $src = if ($vendor -and $script:DriverSource.ContainsKey($vendor)) { $script:DriverSource[$vendor] } else { $null }

    # 1) Windows Update 永远优先 —— 它按设备 ID 匹配, 不依赖任何厂商知识
    $tier = [ordered]@{
        Tier    = 'Windows Update'
        Auto    = $true
        Name    = 'Windows Update 驱动更新'
        Url     = 'ms-settings:windowsupdate'
        Note    = '按本机实际设备 ID 匹配, 硬件无关'
        Vendor  = $vendor
    }

    # 2) 厂商官方源作为补充信息一并给出
    if ($src) {
        $tier.VendorUrl  = $src.Url
        $tier.VendorName = $src.Name
        $tier.VendorNote = $src.Note
    }
    else {
        $tier.VendorUrl  = $null
        $tier.VendorName = if ($vendor) { "$vendor (无内置源映射)" } else { $null }
        $tier.VendorNote = ''
    }

    # 3) 整机厂支持页
    $oem = Get-OemSupportUrl
    $tier.OemUrl = $(if ($oem) { $oem.Url } else { $null })

    return [pscustomobject]$tier
}

# ---------------------------------------------------------------------
#  汇总: 按厂商归类, 便于看"这台机器归谁管"
# ---------------------------------------------------------------------
function Get-HardwareSummary {
    $all = Get-HardwareInventory
    $g = $all | Where-Object { $_.VendorName } | Group-Object VendorName |
         Sort-Object Count -Descending
    foreach ($x in $g) {
        [pscustomobject]@{
            VendorName = $x.Name
            Count      = $x.Count
            Classes    = (($x.Group | Select-Object -ExpandProperty Class -Unique | Sort-Object) -join ', ')
            有厂商源    = $script:DriverSource.ContainsKey($x.Name)
        }
    }
}
