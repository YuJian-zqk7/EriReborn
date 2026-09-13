using System;
using System.Collections.Generic;

namespace SetupLauncher.Core
{
    // 这些类的字段名与 导出状态.ps1 输出的 JSON 键一一对应（键用英文，值才是中文）。

    public class RuntimeItem
    {
        public string id { get; set; }
        public string name { get; set; }
        public string category { get; set; }
        public string tier { get; set; }
        public string wingetId { get; set; }
        public string source { get; set; }
        public string available { get; set; }
        public string arch { get; set; }
        public string note { get; set; }
        public bool installed { get; set; }
        public bool detected { get; set; }
        public string status { get; set; }
        public string version { get; set; }
        public string evidence { get; set; }
    }

    public class VendorInfo
    {
        public string name { get; set; }
        public int count { get; set; }
        public string classes { get; set; }
        public bool hasSource { get; set; }
    }

    public class ProblemDevice
    {
        public string name { get; set; }
        public string reason { get; set; }
        public bool phantom { get; set; }
        public string vendor { get; set; }
        public string vid { get; set; }
        public string dev { get; set; }
        public string cls { get; set; }
        public string instance { get; set; }
        public string driver { get; set; }
    }

    public class HwDevice
    {
        public string name { get; set; }
        public string vendor { get; set; }
        public string vid { get; set; }
        public string dev { get; set; }
        public string subVendor { get; set; }
        public string driver { get; set; }
        public string date { get; set; }
        public bool problem { get; set; }
    }

    public class DriverSource
    {
        public string id { get; set; }
        public string name { get; set; }
        public string category { get; set; }
        public string match { get; set; }
        public bool auto { get; set; }
        public string url { get; set; }
        public string note { get; set; }
    }

    public class PeripheralItem
    {
        public string id { get; set; }
        public string name { get; set; }
        public string category { get; set; }
        public string wingetId { get; set; }
        public string note { get; set; }
    }

    public class WuDriver
    {
        public string title { get; set; }
        public string cls { get; set; }
        public string mfr { get; set; }
        public string kb { get; set; }
        public bool firmware { get; set; }
    }

    public class ModelingItem
    {
        public string id { get; set; }
        public string name { get; set; }
        public string category { get; set; }
        public string tier { get; set; }
        public string archive { get; set; }
        public long size { get; set; }
        public bool payload { get; set; }
        public bool installed { get; set; }
        public string evidence { get; set; }
        public string note { get; set; }
    }

    public class CleanupItem
    {
        public string id { get; set; }
        public string name { get; set; }
        public string category { get; set; }
        public string tier { get; set; }
        public string shareFile { get; set; }
        public string official { get; set; }
        public string mode { get; set; }
        public bool package { get; set; }
        public bool installed { get; set; }
        public string evidence { get; set; }
        public string note { get; set; }
        public string 补丁程序 { get; set; }
    }

    public class ComponentItem
    {
        public string id { get; set; }
        public string name { get; set; }
        public string category { get; set; }
        public bool installed { get; set; }
        public bool detected { get; set; }
        public string version { get; set; }
        public string evidence { get; set; }
    }

    /// <summary>
    /// 统一软件目录里的一项（清单\软件目录.csv 汇总而来）。
    /// 界面上的"软件清单"页读的就是这个 —— 清单上所有软件都在这里，
    /// 不带任何筛除，用户自己决定装什么、装哪里。
    /// </summary>
    public class SoftwareItem
    {
        public string id { get; set; }
        public string name { get; set; }
        public string category { get; set; }
        public string dir { get; set; }          // 英文分类目录: Security / BasicTools / ...
        public string tier { get; set; }
        public string source { get; set; }       // winget / package / dist / official / manual
        public string wingetId { get; set; }
        public string mode { get; set; }         // 安装 / 便携 / 手动
        public string archive { get; set; }      // 网盘 / 本地包文件名
        public string official { get; set; }
        public string patch { get; set; }
        public string dest { get; set; }         // 预设的目标目录（全英文）
        public bool packageReady { get; set; }
        public string packageFile { get; set; }
        public bool installed { get; set; }
        public bool detected { get; set; }
        public string evidence { get; set; }
        public string how { get; set; }          // 本地安装包 / 云盘自取 / 官网下载 / winget / 手动
        public string note { get; set; }
        public string plugin { get; set; }       // 来自哪个插件（内置条目为空）
        public string pluginId { get; set; }
        public string shareUrl { get; set; }
    }

    public class ShareLinks
    {
        public string modeling { get; set; }
        public string cleanup { get; set; }
    }
    public class MachineInfo
    {
        public string oem { get; set; }
        public string model { get; set; }
        public string brand { get; set; }
        public string oemUrl { get; set; }
        public string os { get; set; }
        public int deviceCount { get; set; }

        public string Display
        {
            get { return string.Format("{0} {1}", oem, model); }
        }
    }

    public class StateSnapshot
    {
        public string generated { get; set; }
        public MachineInfo machine { get; set; }
        public List<RuntimeItem> runtimes { get; set; }
        public List<VendorInfo> vendors { get; set; }
        public List<ProblemDevice> problems { get; set; }
        public List<HwDevice> display { get; set; }
        public List<HwDevice> net { get; set; }
        public List<HwDevice> audio { get; set; }
        public List<DriverSource> driverSources { get; set; }
        public List<PeripheralItem> peripherals { get; set; }
        public List<WuDriver> wuDrivers { get; set; }
        public List<WuDriver> wuFirmware { get; set; }
        public List<ModelingItem> modeling { get; set; }
        public List<CleanupItem> cleanup { get; set; }
        public List<ComponentItem> components { get; set; }
        public List<CleanupItem> basics { get; set; }
        public List<SoftwareItem> software { get; set; }
        public ShareLinks shares { get; set; }
        public string wuError { get; set; }
        public bool wuQueried { get; set; }
        public bool partial { get; set; }

        public StateSnapshot()
        {
            runtimes = new List<RuntimeItem>();
            vendors = new List<VendorInfo>();
            problems = new List<ProblemDevice>();
            display = new List<HwDevice>();
            net = new List<HwDevice>();
            audio = new List<HwDevice>();
            driverSources = new List<DriverSource>();
            peripherals = new List<PeripheralItem>();
            wuDrivers = new List<WuDriver>();
            wuFirmware = new List<WuDriver>();
            modeling = new List<ModelingItem>();
            cleanup = new List<CleanupItem>();
            components = new List<ComponentItem>();
            basics = new List<CleanupItem>();
            software = new List<SoftwareItem>();
            shares = new ShareLinks();
        }
    }
}