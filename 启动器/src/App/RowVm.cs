using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using SetupLauncher.Core;

namespace SetupLauncher.App
{
    /// <summary>从已合并的主题资源里取颜色，避免把色值抄两遍。</summary>
    public static class ThemeColors
    {
        public static string Hex(string key, string fallback)
        {
            try
            {
                var res = Application.Current != null ? Application.Current.Resources[key] : null;
                var scb = res as SolidColorBrush;
                if (scb != null)
                {
                    var c = scb.Color;
                    return string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
                }
                if (res is Color) { var c = (Color)res; return string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B); }
            }
            catch { }
            return fallback;
        }
    }

    /// <summary>运行库卡片。</summary>
    public class RuntimeRow : INotifyPropertyChanged
    {
        private bool _selected;

        public RuntimeItem Item { get; private set; }

        public RuntimeRow(RuntimeItem item)
        {
            Item = item;
            _selected = !item.installed && item.detected;   // 默认勾选缺失项
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise(string n)
        {
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(n));
        }

        public bool Selected
        {
            get { return _selected; }
            set
            {
                if (_selected == value) return;
                _selected = value;
                Raise("Selected");
                Raise("CardBg");
                Raise("BorderColor");
            }
        }

        /// <summary>已安装的不再让勾（要重装请用命令行 -Force）。</summary>
        public bool CanSelect { get { return !Item.installed; } }

        public string Name { get { return Item.name; } }
        public string Category { get { return Item.category; } }
        public string Tier { get { return Item.tier; } }
        public string Note { get { return Item.note; } }
        public string Status { get { return Item.status; } }
        public string Source { get { return Item.installed ? "" : Item.source; } }

        public string Detail
        {
            get
            {
                if (Item.installed)
                    return string.IsNullOrEmpty(Item.version) ? Item.evidence : Item.version + "  ·  " + Item.evidence;
                return (Item.available ?? "").Length > 0 ? "可装版本 " + Item.available + "  ·  " + Item.evidence : Item.evidence;
            }
        }

        public string TierColor
        {
            get
            {
                switch (Item.tier)
                {
                    case "核心": return "#2F5D8C";
                    case "推荐": return "#3E6B52";
                    default: return "#4A4A52";
                }
            }
        }

        public string StatusColor
        {
            get
            {
                if (Item.installed) return "#2E7D32";
                if (!Item.detected) return "#5A5A62";
                return "#B26A00";
            }
        }

        public string CardBg
        {
            get
            {
                return _selected
                    ? ThemeColors.Hex("CardBgSel", "#22344A")
                    : ThemeColors.Hex("CardBg", "#1B1F26");
            }
        }

        public string BorderColor
        {
            get { return _selected ? ThemeColors.Hex("AccentBrush", "#4A90D9") : ThemeColors.Hex("LineBrush", "#2A2F38"); }
        }

        public void RefreshAll()
        {
            Raise("Selected"); Raise("CanSelect"); Raise("Status"); Raise("Source");
            Raise("Detail"); Raise("CardBg"); Raise("BorderColor");
        }
    }

    /// <summary>驱动页/硬件页里那种一行一条的通用项。</summary>
    public class PlainRow
    {
        public string Title { get; set; }
        public string Sub { get; set; }
        public string Tag { get; set; }
        public string TagColor { get; set; }

        public PlainRow() { Title = ""; Sub = ""; Tag = ""; TagColor = "#4A4A52"; }

        public PlainRow(string title, string sub, string tag, string tagColor)
        {
            Title = title; Sub = sub; Tag = tag; TagColor = tagColor;
        }
    }

    /// <summary>
    /// 软件清单卡片 —— 清单上所有软件都在这一页，勾选决定装什么，
    /// 卡片上直接写清楚会装到哪个英文目录、从哪来（本地包/云盘/官网/winget）。
    /// </summary>
    public class SoftwareRow : INotifyPropertyChanged
    {
        private bool _selected;

        public SoftwareItem Item { get; private set; }

        public SoftwareRow(SoftwareItem item)
        {
            Item = item;
            // 默认勾选"缺失 + 能自动装"的项，装不了的和已装的留给用户自己决定
            _selected = !item.installed && (item.packageReady || item.source == "winget");
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise(string n)
        {
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(n));
        }

        public bool Selected
        {
            get { return _selected; }
            set
            {
                if (_selected == value) return;
                _selected = value;
                Raise("Selected"); Raise("CardBg"); Raise("BorderColor");
            }
        }

        public bool CanSelect { get { return !Item.installed; } }

        public string Name { get { return Item.name; } }
        public string Dir { get { return Item.dir; } }
        public string Tier { get { return Item.tier; } }
        public string Category
        {
            get { return string.IsNullOrEmpty(Item.plugin) ? Item.category : Item.category + " ·来自插件"; }
        }
        public string Plugin { get { return Item.plugin; } }
        public string How
        {
            get
            {
                // 插件应用的来源名在 清单\软件目录.csv 里是英文键, 这里翻成人话
                switch (Item.source)
                {
                    case "pan123": return "123云盘";
                    case "dist": return "云盘自取";
                    case "official": return "官网下载";
                    case "winget": return "winget";
                    case "local": return "本地文件";
                    case "manual": return "手动";
                    default: return string.IsNullOrEmpty(Item.how) ? Item.source : Item.how;
                }
            }
        }
        public string Dest { get { return Item.dest; } }
        public string Note { get { return Item.note; } }

        public string TierColor
        {
            get
            {
                switch (Item.tier)
                {
                    case "核心": return "#2F5D8C";
                    case "推荐": return "#3E6B52";
                    default: return "#4A4A52";
                }
            }
        }

        public string Status { get { return Item.installed ? "已装" : (Item.detected ? "缺失" : "未知"); } }

        public string StatusColor
        {
            get
            {
                if (Item.installed) return "#2E7D32";
                if (!Item.detected) return "#5A5A62";
                return "#B26A00";
            }
        }

        public string HowColor
        {
            get
            {
                switch (Item.source)
                {
                    case "package": return "#2E7D32";
                    case "local": return "#2E7D32";
                    case "pan123": return "#8A6A1F";
                    case "dist": return "#8A6A1F";
                    case "winget": return "#2F5D8C";
                    case "official": return "#7A4A8A";
                    default: return "#5A5A62";
                }
            }
        }

        public string Detail
        {
            get
            {
                var src = string.IsNullOrEmpty(Item.archive) ? "" : "包: " + Item.archive;
                var ev = Item.installed ? Item.evidence : (Item.packageReady ? "本地包就位: " + System.IO.Path.GetFileName(Item.packageFile ?? "") : "");
                var s = string.IsNullOrEmpty(src) ? ev : (ev.Length > 0 ? src + "   ·   " + ev : src);
                return s;
            }
        }

        public string CardBg
        {
            get
            {
                return _selected
                    ? ThemeColors.Hex("CardBgSel", "#22344A")
                    : ThemeColors.Hex("CardBg", "#1B1F26");
            }
        }

        public string BorderColor
        {
            get { return _selected ? ThemeColors.Hex("AccentBrush", "#4A90D9") : ThemeColors.Hex("LineBrush", "#2A2F38"); }
        }

        public void RefreshAll()
        {
            Raise("Selected"); Raise("CanSelect"); Raise("Status"); Raise("Detail");
            Raise("CardBg"); Raise("BorderColor"); Raise("How"); Raise("Dest");
        }
    }

    /// <summary>分类的中文显示名（英文目录名是分组键）。</summary>
    public class CategoryChip
    {
        public string Dir { get; set; }        // "" = 全部
        public string Text { get; set; }
        public int Count { get; set; }
        public string BadgeColor { get; set; }
        public string Hint { get; set; }
        public bool Active { get; set; }

        public static readonly string[] Order = new string[]
        {
            "security_clear", "BasicTools", "ChatSocial", "CloudDrive", "Download",
            "Browser", "NetAccel", "Modeling3D", "Peripheral", "Runtime"
        };

        public static string Cn(string dir)
        {
            switch (dir)
            {
                case "security_clear":
                case "Security": return "安全清理";   // Security 是旧目录名, 兼容已生成的旧 CSV
                case "BasicTools": return "基础工具";
                case "ChatSocial": return "聊天社交";
                case "CloudDrive": return "网盘";
                case "Download": return "下载";
                case "Browser": return "浏览器";
                case "NetAccel": return "网络加速";
                case "Modeling3D": return "3D建模";
                case "Peripheral": return "外设软件";
                case "Runtime": return "组件运行库";
                case "SystemBase": return "系统基础";
                case "NetDownload": return "网络下载";
                case "DevAI": return "开发AI";
                case "DigitalArt": return "平面绘画";
                case "AudioVideo": return "音视频";
                case "Games": return "游戏";
                case "OfficeRead": return "办公阅读";
                case "Beautify": return "美化便携";
                default: return dir;
            }
        }
    }


}