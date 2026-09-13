using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SetupLauncher.Services;

namespace SetupLauncher.App
{
    /// <summary>插件工作区里一条待写入的应用。</summary>
    public class PluginAppEntry
    {
        public string name { get; set; }
        public string id { get; set; }
        public string category { get; set; }
        public string dir { get; set; }
        public string sub { get; set; }
        public string tier { get; set; }
        public string source { get; set; }
        public string wingetId { get; set; }
        public string shareUrl { get; set; }
        public string url { get; set; }
        public string file { get; set; }
        public string mode { get; set; }
        public string silent { get; set; }
        public string patch { get; set; }
        public string detectMethod { get; set; }
        public string detectParam { get; set; }
        public string note { get; set; }

        public string SourceCn
        {
            get
            {
                switch (source)
                {
                    case "pan123": return "123云盘";
                    case "official": return "官方链接";
                    case "winget": return "winget";
                    case "local": return "本地文件";
                    default: return "手动";
                }
            }
        }

        public string Payload
        {
            get
            {
                switch (source)
                {
                    case "pan123": return shareUrl + "   ·   " + file;
                    case "official": return url;
                    case "winget": return wingetId;
                    case "local": return file;
                    default: return note;
                }
            }
        }
    }

    /// <summary>写进插件文件的结构。</summary>
    public class PluginFileModel
    {
        public int schema { get; set; }
        public string id { get; set; }
        public string name { get; set; }
        public string author { get; set; }
        public string version { get; set; }
        public string description { get; set; }
        public List<Dictionary<string, string>> categories { get; set; }
        public List<Dictionary<string, object>> apps { get; set; }
    }

    /// <summary>
    /// 插件工作区：图形化做一个插件文件。
    /// 用户只做三件事 —— 填插件信息、选分区、加应用；剩下的 JSON 与索引由程序生成。
    /// </summary>
    public class PluginWorkspaceController
    {
        private readonly Window _win;
        private readonly List<PluginAppEntry> _apps = new List<PluginAppEntry>();
        private readonly Dictionary<string, string> _cats = new Dictionary<string, string>();   // 中文名 → 英文目录
        private static readonly Dictionary<string, string> BuiltinDirs = new Dictionary<string, string>
        {
            { "安全清理", "security_clear" }, { "基础工具", "BasicTools" }, { "聊天社交", "ChatSocial" },
            { "网盘", "CloudDrive" }, { "下载", "Download" }, { "浏览器", "Browser" },
            { "网络加速", "NetAccel" }, { "3D建模", "Modeling3D" }, { "外设软件", "Peripheral" },
            { "组件", "Runtime" }, { "运行库", "Runtime" }
        };

        private TextBox _txtPluginName, _txtPluginAuthor, _txtPluginId, _txtPluginDesc,
                        _txtNewCategory, _txtAppName, _txtShareUrl, _txtFileOrUrl,
                        _txtDetectParam, _txtAppNote;
        private ComboBox _cmbSource, _cmbMode, _cmbTier, _cmbDetect;
        private Button _btnPickCats;
        private TextBlock _txtPickedCats;
        private Popup _catPopup;
        private readonly List<CheckBox> _catChecks = new List<CheckBox>();
        private readonly List<string> _pickedCats = new List<string>();   // 一个应用可以进多个分区
        private ListBox _appList;
        private TextBlock _txtStatus, _txtCount, _txtCatHint;
        private Button _btnAddApp, _btnDelApp, _btnGenPlugin, _btnImportJson, _btnSaveJson,
                       _btnNewCategory, _btnOpenPluginDir;

        public event Action Applied;

        public PluginWorkspaceController(Window win)
        {
            _win = win;
            Wire();
            InitCombos();
            // 不在 TextChanged 里回写 Slug —— 每敲一个字符就重设 Text 会把光标弹回开头,
            // 还会打断中文输入法; 生成/导出时再 Slug 一次即可 (P1-7)
            _txtPluginAuthor.Text = Environment.UserName;
            RefreshList();
            SetStatus("填好上面的插件信息，选一个分区，然后把应用一个个加进来。");
        }

        private T N<T>(string name) where T : class
        {
            var o = _win.FindName(name) as T;
            if (o == null) throw new InvalidOperationException("插件窗口元素缺失: " + name);
            return o;
        }

        private void Wire()
        {
            _txtPluginName   = N<TextBox>("TxtPluginName");
            _txtPluginAuthor = N<TextBox>("TxtPluginAuthor");
            _txtPluginId     = N<TextBox>("TxtPluginId");
            _txtPluginDesc   = N<TextBox>("TxtPluginDesc");
            _txtNewCategory  = N<TextBox>("TxtNewCategory");
            _txtAppName      = N<TextBox>("TxtAppName");
            _txtShareUrl     = N<TextBox>("TxtShareUrl");
            _txtFileOrUrl    = N<TextBox>("TxtFileOrUrl");
            _txtDetectParam  = N<TextBox>("TxtDetectParam");
            _txtAppNote      = N<TextBox>("TxtAppNote");
            _btnPickCats     = N<Button>("BtnPickCats");
            _txtPickedCats   = N<TextBlock>("TxtPickedCats");
            _cmbSource       = N<ComboBox>("CmbSource");
            _cmbMode         = N<ComboBox>("CmbMode");
            _cmbTier         = N<ComboBox>("CmbTier");
            _cmbDetect       = N<ComboBox>("CmbDetect");
            _appList         = N<ListBox>("AppList");
            _txtStatus       = N<TextBlock>("TxtStatus");
            _txtCount        = N<TextBlock>("TxtCount");
            _txtCatHint      = N<TextBlock>("TxtCatHint");
            _btnAddApp       = N<Button>("BtnAddApp");
            _btnDelApp       = N<Button>("BtnDelApp");
            _btnGenPlugin    = N<Button>("BtnGenPlugin");
            _btnImportJson   = N<Button>("BtnImportJson");
            _btnSaveJson     = N<Button>("BtnSaveJson");
            _btnNewCategory  = N<Button>("BtnNewCategory");
            _btnOpenPluginDir= N<Button>("BtnOpenPluginDir");

            _btnAddApp.Click += (s, e) => AddApp();
            _btnDelApp.Click += (s, e) => DelApp();
            _btnNewCategory.Click += (s, e) => NewCategory();
            _btnPickCats.Click += (s, e) => ToggleCatPopup();
            _btnGenPlugin.Click += (s, e) => Generate();
            _btnImportJson.Click += (s, e) => ImportJson();
            _btnSaveJson.Click += (s, e) => ExportJson();
            _btnOpenPluginDir.Click += (s, e) => OpenFolder(Path.Combine(AppPaths.Root, "插件"));
        }

        private void InitCombos()
        {
            foreach (var kv in BuiltinDirs.Keys.Distinct()) _cats[kv] = BuiltinDirs[kv];
            _cats["我的工具"] = "MyTools";
            _cats["我的应用"] = "MyApps";
            if (!_pickedCats.Contains("我的工具")) _pickedCats.Add("我的工具");
            RefreshCatChecks();

            _cmbSource.Items.Add("123 云盘（自取）");
            _cmbSource.Items.Add("官方链接（自动下载）");
            _cmbSource.Items.Add("winget");
            _cmbSource.Items.Add("本地文件");
            _cmbSource.Items.Add("只提示（手动）");
            _cmbSource.SelectedIndex = 0;

            _cmbMode.Items.Add("便携（解压到目录）");
            _cmbMode.Items.Add("安装（跑安装器）");
            _cmbMode.Items.Add("手动");
            _cmbMode.SelectedIndex = 0;

            _cmbTier.Items.Add("核心");
            _cmbTier.Items.Add("推荐");
            _cmbTier.Items.Add("按需");
            _cmbTier.SelectedIndex = 1;

            _cmbDetect.Items.Add("none（不检测）");
            _cmbDetect.Items.Add("arp（按程序名）");
            _cmbDetect.Items.Add("file（按文件）");
            _cmbDetect.Items.Add("pnp（按驱动）");
            _cmbDetect.SelectedIndex = 0;
        }

        /// <summary>重建分区勾选列表（_cats 变化后调用），并同步勾选状态。</summary>
        private void RefreshCatChecks()
        {
            _pickedCats.RemoveAll(c => !_cats.ContainsKey(c));
            if (_catPopup == null) BuildCatPopup();
            foreach (var cb in _catChecks)
            {
                var should = _pickedCats.Contains(cb.Tag as string);
                if (cb.IsChecked != should) cb.IsChecked = should;
            }
            UpdatePickedText();
            UpdateCatHint();
        }

        private void BuildCatPopup()
        {
            _catChecks.Clear();
            var sp = new StackPanel { Margin = new Thickness(12, 10, 12, 10) };
            foreach (var kv in _cats.OrderBy(x => x.Key))
            {
                var cb = new CheckBox
                {
                    Content = kv.Key,
                    Tag = kv.Key,
                    Margin = new Thickness(0, 4, 0, 4),
                    Foreground = (System.Windows.Media.Brush)_win.FindResource("TextPrimary"),
                };
                var key = kv.Key;
                cb.Checked   += (s, e) => { if (!_pickedCats.Contains(key)) _pickedCats.Add(key); UpdatePickedText(); UpdateCatHint(); };
                cb.Unchecked += (s, e) => { _pickedCats.Remove(key); UpdatePickedText(); UpdateCatHint(); };
                sp.Children.Add(cb);
                _catChecks.Add(cb);
            }
            var host = new Border
            {
                Background = (System.Windows.Media.Brush)_win.FindResource("PanelBg"),
                BorderBrush = (System.Windows.Media.Brush)_win.FindResource("LineBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Child = new ScrollViewer { MaxHeight = 340, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = sp },
            };
            _catPopup = new Popup
            {
                PlacementTarget = _btnPickCats,
                Placement = PlacementMode.Bottom,
                StaysOpen = false,
                Child = host,
            };
        }

        private void ToggleCatPopup()
        {
            if (_catPopup == null) BuildCatPopup();
            _catPopup.IsOpen = !_catPopup.IsOpen;
        }

        private void UpdatePickedText()
        {
            _txtPickedCats.Text = _pickedCats.Count == 0
                ? "（还没选分区）"
                : "已选：" + string.Join("、", _pickedCats);
        }

        private void UpdateCatHint()
        {
            if (_pickedCats.Count == 0)
            {
                _txtCatHint.Text = "勾选多个分区后，同一个应用会分别添加到每个分区、装进各自的目录。";
                return;
            }
            var cn = _pickedCats[0];
            var dir = _cats.ContainsKey(cn) ? _cats[cn] : Slug(cn);
            var more = _pickedCats.Count > 1 ? string.Format("  （共 {0} 个分区）", _pickedCats.Count) : "";
            _txtCatHint.Text = string.Format("「{0}」→ 目标英文目录 {1}\\{2}{3}", cn, dir, "<应用名>", more);
        }

        private static string Slug(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder();
            foreach (var ch in text)
            {
                if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') ||
                    ch == '.' || ch == '_' || ch == '-' || ch == '+')
                    sb.Append(ch);
                else if (ch == ' ') sb.Append('_');
            }
            var s = sb.ToString().Trim('_', '.', '-');
            while (s.Contains("__")) s = s.Replace("__", "_");
            return s;
        }

        private void SetStatus(string text) { _txtStatus.Text = text; }

        // ---------------------------------------------------------- 应用列表
        private void RefreshList()
        {
            _appList.ItemsSource = null;
            _appList.ItemsSource = _apps.Select(a => new PlainRow(
                a.name + "   [" + a.category + "]",
                string.Format("来源 {0}   ·   {1}   ·   {2}\n目标 {3}\\{4}",
                    a.SourceCn, a.Payload, a.mode, a.dir, a.sub),
                a.source, SourceColor(a.source))).ToList();
            _txtCount.Text = string.Format("共 {0} 个应用", _apps.Count);
        }

        private static string SourceColor(string src)
        {
            switch (src)
            {
                case "pan123": return "#2F5D8C";
                case "official": return "#7A4A8A";
                case "winget": return "#3E6B52";
                case "local": return "#8A6A1F";
                default: return "#4A4A52";
            }
        }

        private void AddApp()
        {
            var name = _txtAppName.Text.Trim();
            if (string.IsNullOrEmpty(name)) { SetStatus("先填应用名称。"); return; }
            if (_pickedCats.Count == 0) { SetStatus("先在「分区」里勾选至少一个分区（可以多选）。"); return; }

            var srcIdx = _cmbSource.SelectedIndex;
            var payload = _txtFileOrUrl.Text.Trim();
            var shareUrl = _txtShareUrl.Text.Trim();

            // 先全部校验再落列表 —— 多分区里任何一个来源没填好, 就一个都不加
            var entries = new List<PluginAppEntry>();
            foreach (var cat in _pickedCats.ToList())
            {
                var e = BuildAppEntry(name, cat, srcIdx, payload, shareUrl);
                if (e == null) return;   // BuildAppEntry 里已给出失败原因
                entries.Add(e);
            }
            _apps.AddRange(entries);
            RefreshList();
            _txtAppName.Text = ""; _txtShareUrl.Text = ""; _txtFileOrUrl.Text = "";
            _txtDetectParam.Text = ""; _txtAppNote.Text = "";
            SetStatus(string.Format("已把「{0}」加到 {1} 个分区：{2}",
                name, entries.Count, string.Join("、", entries.Select(x => x.dir))));
        }

        /// <summary>按当前表单构建一个应用条目（指定分区）；校验失败返回 null 并写状态。</summary>
        private PluginAppEntry BuildAppEntry(string name, string cat, int srcIdx, string payload, string shareUrl)
        {
            var e = new PluginAppEntry();
            e.name = name;
            e.category = cat;
            e.dir = _cats.ContainsKey(cat) ? _cats[cat] : Slug(cat);
            if (string.IsNullOrEmpty(e.dir)) e.dir = "MyApps";
            e.sub = Slug(name);
            if (string.IsNullOrEmpty(e.sub)) e.sub = "app";
            e.tier = _cmbTier.SelectedItem as string;
            e.mode = _cmbMode.SelectedIndex == 1 ? "安装" : (_cmbMode.SelectedIndex == 2 ? "手动" : "便携");
            e.note = _txtAppNote.Text.Trim();

            switch (srcIdx)
            {
                case 0:
                    e.source = "pan123";
                    if (!shareUrl.Contains("123pan")) { SetStatus("123 云盘来源需要填分享链接。"); return null; }
                    if (string.IsNullOrEmpty(payload)) { SetStatus("123 云盘来源需要填要下载的文件名。"); return null; }
                    e.shareUrl = shareUrl; e.file = payload;
                    break;
                case 1:
                    e.source = "official";
                    if (!payload.StartsWith("http")) { SetStatus("官方链接要填 https:// 开头的直链。"); return null; }
                    e.url = payload;
                    break;
                case 2:
                    e.source = "winget";
                    if (string.IsNullOrEmpty(payload)) { SetStatus("winget 来源要填包 ID。"); return null; }
                    e.wingetId = payload;
                    break;
                case 3:
                    e.source = "local";
                    if (string.IsNullOrEmpty(payload)) { SetStatus("本地文件要填文件名。"); return null; }
                    e.file = payload;
                    break;
                default:
                    e.source = "manual";
                    break;
            }

            var dm = _cmbDetect.SelectedIndex;
            e.detectMethod = dm == 1 ? "arp" : (dm == 2 ? "file" : (dm == 3 ? "pnp" : "none"));
            e.detectParam = _txtDetectParam.Text.Trim();
            return e;
        }

        private void DelApp()
        {
            var idx = _appList.SelectedIndex;
            if (idx < 0 || idx >= _apps.Count) { SetStatus("先在列表里点一行，再删。"); return; }
            var gone = _apps[idx];
            _apps.RemoveAt(idx);
            RefreshList();
            SetStatus("已删除 " + gone.name);
        }

        private void NewCategory()
        {
            var cn = _txtNewCategory.Text.Trim();
            if (string.IsNullOrEmpty(cn)) { SetStatus("先填新分区的名字。"); return; }
            var dir = Slug(cn);
            if (string.IsNullOrEmpty(dir))
            {
                // 全中文名字 → 用拼音式兜底不现实, 让用户自己给英文目录名
                SetStatus("这个名字压不出英文目录名，请在名字里掺一点英文，例如「我的工具 MyTools」。");
                return;
            }
            _cats[cn] = dir;
            if (!_pickedCats.Contains(cn)) _pickedCats.Add(cn);
            RefreshCatChecks();
            _txtNewCategory.Text = "";
            SetStatus(string.Format("新分区「{0}」→ {1}\\  （已自动勾选）", cn, dir));
        }

        // ---------------------------------------------------------- 生成
        private void Generate()
        {
            if (_apps.Count == 0) { SetStatus("一个应用都还没加，先生成不出来。"); return; }
            var name = _txtPluginName.Text.Trim();
            if (string.IsNullOrEmpty(name)) { SetStatus("先填插件名称。"); return; }
            var id = Slug(_txtPluginId.Text.Trim());
            if (string.IsNullOrEmpty(id)) id = Slug(name);
            if (string.IsNullOrEmpty(id)) { SetStatus("插件 id 压不出英文名，请手动填一个英文 id。"); return; }

            var model = BuildModel(name, id);
            var json = new JavaScriptSerializer().Serialize(model);

            var tmp = Path.Combine(Path.GetTempPath(), "plugin_" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(tmp, json, new UTF8Encoding(false));

            SetStatus("正在生成插件 ...");
            var task = new Task(() =>
            {
                try
                {
                    // 回调直接进 UI 线程更新状态 —— OutputDataReceived 来自线程池,
                    // 往普通 List 里并发 Add 会丢行/损坏 (P1-6)
                    var code = EngineRunner.Run(AppPaths.PluginScript, "-FromJson " + Ps.Psq(tmp),
                        ln => _win.Dispatcher.BeginInvoke(new Action(() => SetStatus(ln))));
                    _win.Dispatcher.Invoke(() =>
                    {
                        if (code != 0)
                        {
                            SetStatus("生成失败（退出码 " + code + "），详情见日志。");
                            return;
                        }
                        var target = Path.Combine(AppPaths.Root, "插件", id + ".json");
                        SetStatus(string.Format("插件已生成：{0}（{1} 个应用）。" +
                            "启动器会自动重读清单，这些应用就出现在「软件清单」里了。", target, _apps.Count));
                        if (Applied != null) Applied();
                    });
                }
                catch (Exception ex)
                {
                    _win.Dispatcher.BeginInvoke(new Action(() =>
                        SetStatus("生成异常：" + ex.Message)));
                }
                finally
                {
                    try { File.Delete(tmp); } catch { }
                }
            });
            task.Start();
        }

        private PluginFileModel BuildModel(string name, string id)
        {
            var m = new PluginFileModel();
            m.schema = 1;
            m.id = id;
            m.name = name;
            m.author = _txtPluginAuthor.Text.Trim();
            m.version = "1.0";
            m.description = _txtPluginDesc.Text.Trim();

            // 只把"自定义分区"写进 categories, 内置分区的英文目录别人机器上一样
            m.categories = new List<Dictionary<string, string>>();
            foreach (var used in _apps.Select(a => a.category).Distinct())
            {
                if (BuiltinDirs.ContainsKey(used)) continue;
                m.categories.Add(new Dictionary<string, string>
                {
                    { "name", used }, { "dir", _cats.ContainsKey(used) ? _cats[used] : Slug(used) }
                });
            }

            m.apps = new List<Dictionary<string, object>>();
            var seen = new Dictionary<string, int>();
            foreach (var a in _apps)
            {
                var appId = Slug(a.name);
                if (string.IsNullOrEmpty(appId)) appId = "app";
                int n;
                if (seen.TryGetValue(appId, out n)) { seen[appId] = n + 1; appId = appId + n; }
                else seen[appId] = 1;

                a.id = appId;
                var d = new Dictionary<string, object>();
                d["name"] = a.name;
                d["id"] = appId;
                d["category"] = a.category;
                d["sub"] = a.sub;
                d["tier"] = a.tier;
                d["source"] = a.source;
                d["mode"] = a.mode;
                if (!string.IsNullOrEmpty(a.shareUrl)) d["shareUrl"] = a.shareUrl;
                if (!string.IsNullOrEmpty(a.file)) d["file"] = a.file;
                if (!string.IsNullOrEmpty(a.url)) d["url"] = a.url;
                if (!string.IsNullOrEmpty(a.wingetId)) d["wingetId"] = a.wingetId;
                if (!string.IsNullOrEmpty(a.silent)) d["silent"] = a.silent;
                if (!string.IsNullOrEmpty(a.patch)) d["patch"] = a.patch;
                d["detect"] = new Dictionary<string, string>
                {
                    { "method", string.IsNullOrEmpty(a.detectMethod) ? "none" : a.detectMethod },
                    { "param", a.detectParam ?? "" }
                };
                if (!string.IsNullOrEmpty(a.note)) d["note"] = a.note;
                m.apps.Add(d);
            }
            return m;
        }

        // ---------------------------------------------------------- 导入 / 导出
        private void ExportJson()
        {
            if (_apps.Count == 0) { SetStatus("还没有应用，先加几个再导出。"); return; }
            var name = _txtPluginName.Text.Trim();
            var id = Slug(_txtPluginId.Text.Trim());
            if (string.IsNullOrEmpty(id)) id = Slug(name);
            if (string.IsNullOrEmpty(id)) { SetStatus("插件 id 不能为空。"); return; }

            var dlg = new Microsoft.Win32.SaveFileDialog();
            dlg.FileName = id + ".json";
            dlg.Filter = "插件文件|*.json";
            dlg.InitialDirectory = Path.Combine(AppPaths.Root, "插件");
            if (dlg.ShowDialog(_win) != true) return;

            var json = new JavaScriptSerializer().Serialize(BuildModel(string.IsNullOrEmpty(name) ? id : name, id));
            // 注: id 在上面已过 Slug
            File.WriteAllText(dlg.FileName, json, new UTF8Encoding(false));
            SetStatus("已导出：" + dlg.FileName + "（可以发给别人，放到 插件\\ 目录就生效）");
        }

        private void ImportJson()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.Filter = "插件文件|*.json";
            dlg.InitialDirectory = Path.Combine(AppPaths.Root, "插件");
            if (dlg.ShowDialog(_win) != true) return;

            try
            {
                var json = File.ReadAllText(dlg.FileName, Encoding.UTF8);
                var ser = new JavaScriptSerializer();
                var obj = (Dictionary<string, object>)ser.DeserializeObject(json);
                if (obj == null) throw new Exception("文件是空的");

                _txtPluginName.Text = Str(obj, "name");
                _txtPluginId.Text = Slug(Str(obj, "id"));
                if (obj.ContainsKey("author")) _txtPluginAuthor.Text = Str(obj, "author");
                if (obj.ContainsKey("description")) _txtPluginDesc.Text = Str(obj, "description");

                var cats = obj.ContainsKey("categories") ? obj["categories"] as object[] : null;
                if (cats != null)
                {
                    foreach (var c in cats)
                    {
                        var cd = c as Dictionary<string, object>;
                        if (cd == null) continue;
                        var cn = Str(cd, "name");
                        if (string.IsNullOrEmpty(cn)) continue;
                        // 别人发来的 JSON 不可信: dir 必须过 Slug, 防 "..\.." 路径遍历 (P0-5)
                        var dirSlugged = Slug(Str(cd, "dir"));
                        _cats[cn] = string.IsNullOrEmpty(dirSlugged) ? Slug(cn) : dirSlugged;
                    }
                    RefreshCatChecks();
                }

                _apps.Clear();
                var apps = obj.ContainsKey("apps") ? obj["apps"] as object[] : null;
                if (apps != null)
                {
                    foreach (var a in apps)
                    {
                        var ad = a as Dictionary<string, object>;
                        if (ad == null) continue;
                        var e = new PluginAppEntry();
                        e.name = Str(ad, "name");
                        if (string.IsNullOrEmpty(e.name)) continue;
                        e.category = Str(ad, "category");
                        if (string.IsNullOrEmpty(e.category)) e.category = "我的工具";
                        // 别人发来的 JSON 不可信: dir/sub 必须过 Slug, 防 "..\.." 路径遍历 (P0-5)
                        var dirRaw = Slug(Str(ad, "dir"));
                        e.dir = string.IsNullOrEmpty(dirRaw)
                            ? (e.category == null ? "MyApps" : (_cats.ContainsKey(e.category) ? _cats[e.category] : Slug(e.category)))
                            : dirRaw;
                        if (string.IsNullOrEmpty(e.dir)) e.dir = "MyApps";
                        if (!_cats.ContainsKey(e.category)) _cats[e.category] = e.dir;
                        var subSlugged = Slug(Str(ad, "sub"));
                        e.sub = string.IsNullOrEmpty(subSlugged) ? Slug(e.name) : subSlugged;
                        if (string.IsNullOrEmpty(e.sub)) e.sub = "app";
                        e.tier = string.IsNullOrEmpty(Str(ad, "tier")) ? "推荐" : Str(ad, "tier");
                        e.source = Str(ad, "source");
                        if (string.IsNullOrEmpty(e.source)) e.source = "manual";
                        e.mode = string.IsNullOrEmpty(Str(ad, "mode")) ? "便携" : Str(ad, "mode");
                        e.shareUrl = Str(ad, "shareUrl");
                        e.file = Str(ad, "file");
                        e.url = Str(ad, "url");
                        e.wingetId = Str(ad, "wingetId");
                        e.silent = Str(ad, "silent");
                        e.patch = Str(ad, "patch");
                        e.note = Str(ad, "note");
                        if (ad.ContainsKey("detect"))
                        {
                            var dd = ad["detect"] as Dictionary<string, object>;
                            if (dd != null) { e.detectMethod = Str(dd, "method"); e.detectParam = Str(dd, "param"); }
                        }
                        _apps.Add(e);
                    }
                    RefreshCatChecks();
                }
                RefreshList();
                SetStatus(string.Format("已读入 {0}（{1} 个应用），改完点「生成插件」就会写回 插件\\ 目录。",
                    Path.GetFileName(dlg.FileName), _apps.Count));
            }
            catch (Exception ex) { SetStatus("读入失败：" + ex.Message); }
        }

        private static string Str(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.ContainsKey(key)) return "";
            var v = d[key];
            return v == null ? "" : Convert.ToString(v);
        }

        private static void OpenFolder(string path)
        {
            try
            {
                if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                { FileName = path, UseShellExecute = true });
            }
            catch { }
        }
    }
}
