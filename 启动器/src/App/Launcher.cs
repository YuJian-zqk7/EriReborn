using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SetupLauncher.Core;
using SetupLauncher.Services;

namespace SetupLauncher.App
{
    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            // --selftest: 不显示窗口, 把"XAML 解析 → 控件绑定 → 状态加载 → 各页填充 → 页面切换"
            // 全部真跑一遍, 任何一步失败都会打印完整堆栈。这是给排查用的, 正常双击不走这条路。
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--selftest") >= 0) { RunSelfTest(); return; }

            // 单实例: 双开会并发读写 状态.json / 凭据文件 / 引擎脚本输出
            bool createdNew;
            using (var mutex = new System.Threading.Mutex(true, "SetupLauncher_SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("启动器已经在运行了。", "提示");
                    return;
                }

                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                    LogCrash(e.ExceptionObject == null ? "null" : e.ExceptionObject.ToString());

                var app = new Application();
                app.ShutdownMode = ShutdownMode.OnMainWindowClose;
                app.DispatcherUnhandledException += (s, e) =>
                {
                    LogCrash(e.Exception.ToString());
                    MessageBox.Show(e.Exception.Message, "启动器错误");
                    e.Handled = true;
                };

                // 必须在解析 MainWindow 之前合并主题 —— 界面里用了 StaticResource,
                // 解析那一刻就要能查到, 晚一步就会抛 ResourceReferenceKeyNotFoundException。
                app.Resources.MergedDictionaries.Add(Xaml.LoadTheme());

                var win = (Window)Xaml.Parse("MainWindow");
                new MainWindowController(win);
                app.Run(win);
            }
        }

        /// <summary>崩溃日志: 目录不存在先建, 追加而不是覆盖, 自身出错也不能再抛。</summary>
        internal static void LogCrash(string text)
        {
            try
            {
                Directory.CreateDirectory(@"E:\安装系统\日志");
                File.AppendAllText(@"E:\安装系统\日志\启动器异常.txt",
                    DateTime.Now + "\n" + text + "\n----\n", Encoding.UTF8);
            }
            catch { }
        }

        private static void RunSelfTest()
        {
            var report = new StringBuilder();
            Action<string, Action> step = (name, body) =>
            {
                try { body(); report.AppendLine("  OK   " + name); }
                catch (Exception ex)
                {
                    var real = ex;
                    while (real is System.Reflection.TargetInvocationException && real.InnerException != null)
                        real = real.InnerException;
                    report.AppendLine("  FAIL " + name + "  ->  " + real.GetType().Name + ": " + real.Message);
                    report.AppendLine(real.StackTrace);
                }
            };

            report.AppendLine("==== 启动器自检 " + DateTime.Now + " ====");
            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.Resources.MergedDictionaries.Add(Xaml.LoadTheme());

            Window win = null; MainWindowController ctl = null; StateSnapshot snap = null;
            step("解析 MainWindow.xaml", () => { win = (Window)Xaml.Parse("MainWindow"); });
            step("绑定全部控件 (Wire)", () => { ctl = new MainWindowController(win); });
            step("同步加载状态 (导出状态.ps1 -RuntimesOnly)", () =>
            {
                string err;
                snap = StateProvider.Load(false, true, out err);
                if (snap == null) throw new Exception("状态加载失败: " + err);
                report.AppendLine("       运行库 " + snap.runtimes.Count
                    + " / 3D " + snap.modeling.Count
                    + " / 清理 " + snap.cleanup.Count
                    + " / 组件 " + snap.components.Count
                    + " / 基础 " + snap.basics.Count);
            });
            step("写入快照字段", () =>
            {
                var ff = typeof(MainWindowController).GetField("_snap",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                ff.SetValue(ctl, snap);
            });
            step("填充全部页面 (PopulateAll)", () =>
            {
                var f = typeof(MainWindowController).GetMethod("PopulateAll",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                f.Invoke(ctl, null);
            });
            step("页面切换 0..5", () =>
            {
                var f = typeof(MainWindowController).GetMethod("ShowPage",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                for (int i = 0; i <= 5; i++) f.Invoke(ctl, new object[] { i });
            });
            step("左侧分类子页面已生成", () =>
            {
                var nf = typeof(MainWindowController).GetField("_catNavs",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var navs = (List<RadioButton>)nf.GetValue(ctl);
                if (navs.Count < 5) throw new Exception("左侧分类子页面只有 " + navs.Count + " 个");
                report.AppendLine("       左侧分类子页面 " + navs.Count + " 个: " +
                    string.Join(" / ", navs.Select(x => x.Tag as string)));
            });
            step("分类子页面切换", () =>
            {
                var sf = typeof(MainWindowController).GetMethod("ShowSoftwarePage",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var lf = typeof(MainWindowController).GetField("_softwareList",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                sf.Invoke(ctl, new object[] { "SystemBase" });
                var sec = ((System.Collections.IEnumerable)((ItemsControl)lf.GetValue(ctl)).ItemsSource).Cast<SoftwareRow>().ToList();
                if (sec.Count == 0 || sec.Any(r => r.Item.dir != "SystemBase"))
                    throw new Exception("切到 SystemBase 子页后内容不对: " + sec.Count + " 项");
                report.AppendLine("       SystemBase 子页 → " + sec.Count + " 项");
                sf.Invoke(ctl, new object[] { "" });
                var all = ((System.Collections.IEnumerable)((ItemsControl)lf.GetValue(ctl)).ItemsSource);
                int total = 0; foreach (var o in all) total++;
                report.AppendLine("       全部软件页 → " + total + " 项");
                sf.Invoke(ctl, new object[] { "Modeling3D" });
                var m3 = ((System.Collections.IEnumerable)((ItemsControl)lf.GetValue(ctl)).ItemsSource).Cast<SoftwareRow>().ToList();
                if (m3.Count == 0) throw new Exception("Modeling3D 子页为空");
                report.AppendLine("       Modeling3D 子页 → " + m3.Count + " 项");
                sf.Invoke(ctl, new object[] { "" });
            });
            step("插件工作区可打开", () =>
            {
                if (!File.Exists(AppPaths.PluginScript)) throw new Exception("缺 引擎\\插件管理.ps1");
                if (!Directory.Exists(AppPaths.PluginDir)) Directory.CreateDirectory(AppPaths.PluginDir);
                var w = (Window)Xaml.Parse("PluginWorkspace");
                var pc = new PluginWorkspaceController(w);
                report.AppendLine("       插件工作区绑定 OK (已有插件 " +
                    new DirectoryInfo(AppPaths.PluginDir).GetFiles("*.json").Length + " 个)");
            });
            step("软件清单页填充", () =>
            {
                if (snap.software == null || snap.software.Count == 0)
                    throw new Exception("软件清单为空 —— 检查 清单\\软件目录.csv");
                report.AppendLine("       软件 " + snap.software.Count + " 项 / "
                    + snap.software.Select(x => x.dir).Distinct().Count() + " 个英文目录");
            });
            step("云盘凭据库可定位", () =>
            {
                if (!File.Exists(Path.Combine(AppPaths.LibDir, "网盘目录.ps1"))) throw new Exception("缺 库\\网盘目录.ps1");
                if (!File.Exists(Path.Combine(AppPaths.Scripts, "网盘凭据.ps1"))) throw new Exception("缺 引擎\\网盘凭据.ps1");
            });
            step("软件引擎可定位", () =>
            {
                if (!File.Exists(AppPaths.SoftwareScript)) throw new Exception("缺 引擎\\安装软件.ps1");
                if (!File.Exists(AppPaths.Catalog)) throw new Exception("缺 清单\\软件目录.csv");
            });
            step("安全清理引擎可定位", () =>
            {
                if (!File.Exists(AppPaths.CleanupScript)) throw new Exception("缺 " + AppPaths.CleanupScript);
            });
            step("3D 引擎可定位", () =>
            {
                if (!File.Exists(@"E:\Work\ModelInstaller\发布\ModelInstaller.exe")) throw new Exception("ModelInstaller.exe 不在预期位置");
            });

            report.AppendLine("==== 自检结束 ====");
            var outPath = @"E:\安装系统\日志\启动器自检.txt";
            File.WriteAllText(outPath, report.ToString(), Encoding.UTF8);
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine(report.ToString());
            Console.WriteLine("已写入 " + outPath);
        }
    }

    public class MainWindowController
    {
        private readonly Window _win;

        private StateSnapshot _snap;
        private List<RuntimeRow> _allRows = new List<RuntimeRow>();
        private string _swCategory = "";   // "" = 全部分类（全部软件页）
        private List<SoftwareRow> _swRows = new List<SoftwareRow>();
        private readonly List<RadioButton> _catNavs = new List<RadioButton>();
        private bool _busy;
        private bool _wantFull, _wantWu;
        private bool _busyNoPopup;                                      // 忙碌时不弹提示窗（如等浏览器登录）
        private bool _bindBusy;                                         // 绑定窗口正在验证登录态
        private Window _busyWin; private TextBlock _busyTxt;            // 检测中提示弹窗
        private Dictionary<string, string> _dirCn;                      // 英文目录 → 中文分类名

        // 界面元素
        private TextBlock _txtMachine, _txtSummary, _txtStatus, _txtRoot, _txtWuHint,
                          _txtCleanHint, _txtBasicHint, _txtSwHint, _txtSwTitle, _txtSwDest,
                          _txtCredState, _txtCloudHint;
        private Button _btnRefresh, _btnInstall, _btnDrivers, _btnAll, _btnNone;
        private Button _btnOpenOem, _btnOpenCleanShare, _btnOpenPkg, _btnImportCleanup,
                       _btnBrowsePkg, _btnBrowseRoot;
        private Button _btnSwAll, _btnSwNone, _btnSwInstall, _btnSwPatch,
                       _btnCloudInstall, _btnSaveCred, _btnProbeCred, _btnOpenShare,
                       _btnChromiumLogin, _btnPluginWorkspace, _btnOpenPluginDir,
                       _btnBrowseAppsRoot, _btnRebuildCatalog;
        private CheckBox _chkCore, _chkRec, _chkOpt, _chkMissing, _chkFirmware,
                         _chkSwCore, _chkSwRec, _chkSwOpt, _chkSwMissing;
        private RadioButton _navOverview, _navRuntimes, _navCleanup, _navPlugins,
                            _navHardware, _navCloud, _navLog;
        private Button _btnCloudAcct;
        private ScrollViewer _panelSoftware, _panelCloud, _panelRuntimes, _panelCleanup,
                             _panelHardware, _logScroll;
        private Border _panelLog, _filterBar, _softwareBar;
        private StackPanel _pluginBar;
        private Panel _navCats;
        private ItemsControl _cleanupList, _basicList, _compList,
                             _runtimeList, _softwareList,
                             _wuList, _fwList, _sourceList, _periphList,
                             _problemList, _displayList, _netList, _audioList, _vendorList;
        private TextBox _txtLog, _txtPkgDir, _txtRootDir, _txtAppsRoot, _txtCred;

        public MainWindowController(Window win)
        {
            _win = win;
            Wire();
            _win.Loaded += (s, e) => { ShowPage(0); Reload(false, false); };
        }

        private T N<T>(string name) where T : class
        {
            var o = _win.FindName(name) as T;
            if (o == null) throw new InvalidOperationException("界面元素缺失: " + name);
            return o;
        }

        private void Wire()
        {
            _txtMachine = N<TextBlock>("TxtMachine");
            _txtSummary = N<TextBlock>("TxtSummary");
            _txtStatus  = N<TextBlock>("TxtStatus");
            _txtRoot    = N<TextBlock>("TxtRoot");
            _txtWuHint  = N<TextBlock>("TxtWuHint");

            _btnRefresh = N<Button>("BtnRefresh");
            _btnInstall = N<Button>("BtnInstall");
            _btnDrivers = N<Button>("BtnDrivers");
            _btnAll     = N<Button>("BtnAll");
            _btnNone    = N<Button>("BtnNone");

            _chkCore    = N<CheckBox>("ChkCore");
            _chkRec     = N<CheckBox>("ChkRec");
            _chkOpt     = N<CheckBox>("ChkOpt");
            _chkMissing = N<CheckBox>("ChkMissing");
            _chkFirmware= N<CheckBox>("ChkFirmware");

            _navOverview = N<RadioButton>("NavOverview");
            _navRuntimes = N<RadioButton>("NavRuntimes");
            _navCleanup  = N<RadioButton>("NavCleanup");
            _navPlugins  = N<RadioButton>("NavPlugins");
            _navHardware = N<RadioButton>("NavHardware");
            _navCloud    = N<RadioButton>("NavCloud");
            _navLog      = N<RadioButton>("NavLog");
            _navCats     = N<Panel>("NavCats");

            _panelSoftware = N<ScrollViewer>("PanelSoftware");
            _panelCloud    = N<ScrollViewer>("PanelCloud");
            _panelRuntimes = N<ScrollViewer>("PanelRuntimes");
            _panelCleanup  = N<ScrollViewer>("PanelCleanup");
            _panelHardware = N<ScrollViewer>("PanelHardware");
            _panelLog      = N<Border>("PanelLog");
            _logScroll     = N<ScrollViewer>("LogScroll");
            _filterBar     = N<Border>("FilterBar");
            _softwareBar   = N<Border>("SoftwareBar");

            _softwareList = N<ItemsControl>("SoftwareList");
            _pluginBar    = N<StackPanel>("PluginBar");
            _cleanupList = N<ItemsControl>("CleanupList");
            _basicList   = N<ItemsControl>("BasicList");
            _compList    = N<ItemsControl>("CompList");
            _runtimeList = N<ItemsControl>("RuntimeList");
            _wuList      = N<ItemsControl>("WuList");
            _fwList      = N<ItemsControl>("FwList");
            _sourceList  = N<ItemsControl>("SourceList");
            _periphList  = N<ItemsControl>("PeriphList");
            _problemList = N<ItemsControl>("ProblemList");
            _displayList = N<ItemsControl>("DisplayList");
            _netList     = N<ItemsControl>("NetList");
            _audioList   = N<ItemsControl>("AudioList");
            _vendorList  = N<ItemsControl>("VendorList");
            _txtLog      = N<TextBox>("TxtLog");
            _txtCleanHint= N<TextBlock>("TxtCleanHint");
            _txtBasicHint= N<TextBlock>("TxtBasicHint");
            _txtSwHint   = N<TextBlock>("TxtSwHint");
            _txtSwTitle  = N<TextBlock>("TxtSwTitle");
            _txtSwDest   = N<TextBlock>("TxtSwDest");
            _txtCredState= N<TextBlock>("TxtCredState");
            _txtCloudHint= N<TextBlock>("TxtCloudHint");
            _txtPkgDir   = N<TextBox>("TxtPkgDir");
            _txtRootDir  = N<TextBox>("TxtRootDir");
            _txtAppsRoot = N<TextBox>("TxtAppsRoot");
            _txtCred     = N<TextBox>("TxtCred");
            _btnOpenOem        = N<Button>("BtnOpenOem");
            _btnOpenCleanShare = N<Button>("BtnOpenCleanShare");
            _btnOpenPkg        = N<Button>("BtnOpenPkg");
            _btnImportCleanup  = N<Button>("BtnImportCleanup");
            _btnBrowsePkg  = N<Button>("BtnBrowsePkg");
            _btnBrowseRoot = N<Button>("BtnBrowseRoot");
            _btnSwAll          = N<Button>("BtnSwAll");
            _btnSwNone         = N<Button>("BtnSwNone");
            _btnSwInstall      = N<Button>("BtnSwInstall");
            _btnSwPatch        = N<Button>("BtnPatchRun");
            _btnCloudInstall   = N<Button>("BtnCloudInstall");
            _btnSaveCred       = N<Button>("BtnSaveCred");
            _btnProbeCred      = N<Button>("BtnProbeCred");
            _btnChromiumLogin  = N<Button>("BtnChromiumLogin");
            _btnOpenShare      = N<Button>("BtnOpenShare");
            _btnBrowseAppsRoot = N<Button>("BtnBrowseAppsRoot");
            _btnRebuildCatalog = N<Button>("BtnRebuildCatalog");
            _btnPluginWorkspace = N<Button>("BtnPluginWorkspace");
            _btnOpenPluginDir   = N<Button>("BtnOpenPluginDir");
            _chkSwCore     = N<CheckBox>("ChkSwCore");
            _chkSwRec      = N<CheckBox>("ChkSwRec");
            _chkSwOpt      = N<CheckBox>("ChkSwOpt");
            _chkSwMissing  = N<CheckBox>("ChkSwMissing");

            _txtPkgDir.Text  = AppPaths.CleanupPkgDir;
            _txtRootDir.Text = AppPaths.AppsRoot;
            _txtAppsRoot.Text = AppPaths.AppsRoot;
            RefreshCredState();

            _txtRoot.Text = "数据目录\n" + AppPaths.Root;

            _btnRefresh.Click += (s, e) => Reload(_wantFull, _wantWu);
            _btnInstall.Click += (s, e) => StartInstallRuntimes();
            _btnDrivers.Click += (s, e) => StartInstallDrivers();
            _btnAll.Click     += (s, e) => SetAllSelected(true);
            _btnNone.Click    += (s, e) => SetAllSelected(false);
            _btnOpenOem.Click        += (s, e) => OpenUrl(_snap != null && _snap.machine != null ? _snap.machine.oemUrl : null);
            _btnOpenCleanShare.Click += (s, e) => OpenUrl(_snap != null && _snap.shares != null ? _snap.shares.cleanup : null);
            _btnOpenPkg.Click        += (s, e) => OpenFolder(_txtPkgDir.Text);
            _btnImportCleanup.Click  += (s, e) => StartImportCleanup();
            _btnBrowsePkg.Click  += (s, e) => BrowseDir(_txtPkgDir,  "选择安装包所在的文件夹");
            _btnBrowseRoot.Click += (s, e) => BrowseDir(_txtRootDir, "选择解压根目录（会按类别建英文子文件夹）");

            _chkCore.Checked   += (s, e) => ApplyFilter();
            _chkCore.Unchecked += (s, e) => ApplyFilter();
            _chkRec.Checked    += (s, e) => ApplyFilter();
            _chkRec.Unchecked  += (s, e) => ApplyFilter();
            _chkOpt.Checked    += (s, e) => ApplyFilter();
            _chkOpt.Unchecked  += (s, e) => ApplyFilter();
            _chkMissing.Checked   += (s, e) => ApplyFilter();
            _chkMissing.Unchecked += (s, e) => ApplyFilter();

            _navOverview.Checked += (s, e) => ShowSoftwarePage("");
            _navRuntimes.Checked += (s, e) => ShowPage(1);
            _navCleanup.Checked  += (s, e) => ShowPage(2);
            _navPlugins.Checked  += (s, e) => ShowPluginPage();
            _navHardware.Checked += (s, e) => ShowPage(3);
            _navCloud.Checked    += (s, e) => ShowPage(4);
            _navLog.Checked      += (s, e) => ShowPage(5);

            // ---- 软件清单页 ----
            _chkSwCore.Checked   += (s, e) => ApplySoftwareFilter();
            _chkSwCore.Unchecked += (s, e) => ApplySoftwareFilter();
            _chkSwRec.Checked    += (s, e) => ApplySoftwareFilter();
            _chkSwRec.Unchecked  += (s, e) => ApplySoftwareFilter();
            _chkSwOpt.Checked    += (s, e) => ApplySoftwareFilter();
            _chkSwOpt.Unchecked  += (s, e) => ApplySoftwareFilter();
            _chkSwMissing.Checked   += (s, e) => ApplySoftwareFilter();
            _chkSwMissing.Unchecked += (s, e) => ApplySoftwareFilter();
            _btnSwAll.Click  += (s, e) => SetSoftwareAll(true);
            _btnSwNone.Click += (s, e) => SetSoftwareAll(false);
            _btnSwInstall.Click += (s, e) => StartInstallSoftware(false);
            _btnSwPatch.Click   += (s, e) => StartInstallSoftware(true);
            // 筛选条上的"只打补丁"之前没接线, 点了没反应 —— 补上 (P1-8)
            N<Button>("BtnSwPatchOnly").Click += (s, e) => StartInstallSoftware(true);
            _btnCloudInstall.Click += (s, e) => StartInstallSoftware(false, true);
            _btnBrowseAppsRoot.Click += (s, e) => BrowseDir(_txtAppsRoot, "选择安装根目录（下面会按类别建英文子目录）");
            _btnRebuildCatalog.Click += (s, e) => StartRebuildCatalog();
            _btnPluginWorkspace.Click += (s, e) => OpenPluginWorkspace();
            _btnOpenPluginDir.Click   += (s, e) =>
            {
                if (!Directory.Exists(AppPaths.PluginDir)) Directory.CreateDirectory(AppPaths.PluginDir);
                OpenFolder(AppPaths.PluginDir);
            };

            // ---- 网盘下载页 ----
            _btnCloudAcct = N<Button>("BtnCloudAcct");
            _btnCloudAcct.Click += (s, e) => { if (!_busy) OpenBindWindow(); };
            _btnChromiumLogin.Click += (s, e) => StartChromiumLogin();
            _btnSaveCred.Click  += (s, e) => StartSaveCredential();
            _btnProbeCred.Click += (s, e) => StartProbeCredential();
            _btnOpenShare.Click += (s, e) => OpenUrl(AppPaths.ShareUrl);
        }

        // ------------------------------------------------------------ 网盘凭据
        private void RefreshCredState()
        {
            var text = EngineRunner.ReadPanCredentialState();
            _txtCredState.Text = string.IsNullOrEmpty(text)
                ? "当前未配置登录态 —— 云盘里的包只能手动下载。"
                : string.Format("已保存登录态（{0} 字符，{1}）。填新的会覆盖旧的。",
                    text.Length, text.StartsWith("token=") ? "token 形式" : "cookie 形式");
            _txtCloudHint.Text = "缓存目录：" + AppPaths.CloudCache + "    分享地址：" + AppPaths.ShareUrl;
        }

        /// <summary>
        /// 开 Chromium（Edge/Chrome）窗口让用户自己登录 123 云盘。
        /// 用户只负责登录，登录态由脚本从浏览器里取回来并验证。
        /// </summary>
        /// <summary>
        /// 后台任务统一入口: 包 try/catch。裸 new Task 的异常会被静默吞掉,
        /// SetBusy(false) 永远不执行, 界面就永久卡在"忙碌" (P0-1)。
        /// </summary>
        private void RunBg(Action body)
        {
            var task = new Task(() =>
            {
                try { body(); }
                catch (Exception ex)
                {
                    Program.LogCrash("后台任务异常: " + ex);
                    try
                    {
                        _win.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            AppendLog("[错误] " + ex.Message);
                            SetBusy(false, "后台任务失败：" + ex.Message);
                        }));
                    }
                    catch { }
                }
            });
            task.Start();
            return;
        }

        private void StartChromiumLogin()
        {
            if (_busy) return;
            // 登录要等用户操作浏览器, 置顶提示窗会挡住浏览器 —— 这段时间不弹
            _busyNoPopup = true;
            SetBusy(true, "已打开浏览器登录窗口：请在窗口里登录 123 云盘，登录完关掉窗口。");
            _busyNoPopup = false;
            ShowPage(5);
            _navLog.IsChecked = true;
            AppendLog("==== 用 Chromium 窗口登录 123 云盘 ====");
            _txtCredState.Text = "登录窗口已打开 —— 在窗口里登录 123 云盘，登录完成后关掉窗口，登录态会自动取回并验证。";

            RunBg(() =>
            {
                var json = EngineRunner.StartChromiumLogin(600,
                    ln => _win.Dispatcher.BeginInvoke(new Action(() => AppendLog(ln))));
                _win.Dispatcher.Invoke(() =>
                {
                    AppendLog(json);
                    ApplyCredResult(json);
                    SetBusy(false, _txtStatus.Text);
                    RefreshCredState();
                });
            });
        }

        private void StartSaveCredential()
        {
            if (_busy) return;
            var cred = _txtCred.Text.Trim();
            if (cred.Length < 8) { _txtStatus.Text = "先把 cookie 或 token 粘进上面的框里。"; return; }
            SetBusy(true, "正在保存并验证 123 云盘登录态 ...");
            AppendLog("==== 保存 123 云盘登录态并自检 ====");
            RunBg(() =>
            {
                var json = EngineRunner.SavePanCredential(cred);
                _win.Dispatcher.Invoke(() =>
                {
                    AppendLog(json);
                    ApplyCredResult(json);
                    SetBusy(false, _txtStatus.Text);
                    _txtCred.Text = "";
                });
            });
        }

        private void StartProbeCredential()
        {
            if (_busy) return;
            var cred = _txtCred.Text.Trim();
            SetBusy(true, "正在测试 123 云盘登录态 ...");
            AppendLog("==== 测试现有 123 云盘登录态 ====");
            RunBg(() =>
            {
                var json = string.IsNullOrEmpty(cred)
                    ? EngineRunner.SavePanCredential(EngineRunner.ReadPanCredentialState())
                    : EngineRunner.SavePanCredential(cred);
                _win.Dispatcher.Invoke(() =>
                {
                    AppendLog(json);
                    ApplyCredResult(json);
                    SetBusy(false, _txtStatus.Text);
                    if (!string.IsNullOrEmpty(cred)) _txtCred.Text = "";
                });
            });
        }

        private void ApplyCredResult(string json)
        {
            try
            {
                var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
                var d = (Dictionary<string, object>)ser.DeserializeObject(json);
                var login = d.ContainsKey("hasLogin") && d["hasLogin"] != null && (bool)d["hasLogin"];
                var msg = d.ContainsKey("message") ? Convert.ToString(d["message"]) : "";
                RefreshCredState();
                if (login)
                {
                    _txtStatus.Text = "123 云盘登录态可用（已成功取到下载直链）。";
                    _txtCredState.Text = "登录态可用 ✓  " + msg;
                }
                else
                {
                    _txtStatus.Text = "登录态不可用：" + msg;
                    _txtCredState.Text = "登录态不可用 ✗  " + msg +
                        "\n如果报 5112，就是 123 云盘不认这份登录态 —— 重新登录 123pan.com 后按页面上的说明再取一次 Cookie。";
                }
            }
            catch (Exception ex) { _txtStatus.Text = "解析自检结果失败：" + ex.Message; }
        }

        // ------------------------------------------------------------ 页面切换
        private void ShowPage(int i)
        {
            // i: 0 = 软件（全部 / 各分类子页共用 PanelSoftware）, 1..5 = 运行库 / 清理 / 硬件 / 网盘 / 日志
            _panelSoftware.Visibility = i == 0 ? Visibility.Visible : Visibility.Collapsed;
            _panelRuntimes.Visibility = i == 1 ? Visibility.Visible : Visibility.Collapsed;
            _panelCleanup.Visibility  = i == 2 ? Visibility.Visible : Visibility.Collapsed;
            _panelHardware.Visibility = i == 3 ? Visibility.Visible : Visibility.Collapsed;
            _panelCloud.Visibility    = i == 4 ? Visibility.Visible : Visibility.Collapsed;
            _panelLog.Visibility      = i == 5 ? Visibility.Visible : Visibility.Collapsed;

            _filterBar.Visibility     = i == 1 ? Visibility.Visible : Visibility.Collapsed;
            _softwareBar.Visibility   = i == 0 ? Visibility.Visible : Visibility.Collapsed;

            _btnInstall.Visibility    = i == 1 ? Visibility.Visible : Visibility.Collapsed;
            _btnDrivers.Visibility    = i == 1 ? Visibility.Visible : Visibility.Collapsed;
            _btnSwInstall.Visibility  = i == 0 ? Visibility.Visible : Visibility.Collapsed;
            _btnSwPatch.Visibility    = i == 0 ? Visibility.Visible : Visibility.Collapsed;
            _btnCloudInstall.Visibility = i == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (i == 4) RefreshCredState();
            if (i == 1) { if (_snap == null || _snap.partial || !_snap.wuQueried) Reload(true, true); }
            else if (i == 3) { if (_snap == null || _snap.partial) Reload(true, false); }
        }

        /// <summary>切到某个分类子页（""=全部软件）。左侧导航是一套单选框，这里同时把选中项对齐。</summary>
        private void ShowSoftwarePage(string dir)
        {
            _swCategory = dir ?? "";
            ShowPage(0);
            foreach (var rb in _catNavs) rb.IsChecked = ((rb.Tag as string) ?? "") == _swCategory;
            _navPlugins.IsChecked = false;
            _navOverview.IsChecked = _swCategory == "";
            ApplySoftwareFilter();
        }

        /// <summary>插件应用聚合子页：所有插件带来的应用都在这里，不再混进内置分类。</summary>
        private void ShowPluginPage()
        {
            _swCategory = "__plugins__";
            ShowPage(0);
            foreach (var rb in _catNavs) rb.IsChecked = false;
            _navOverview.IsChecked = false;
            ApplySoftwareFilter();
        }

        // ------------------------------------------------------------ 加载
        private void Reload(bool full, bool withWu)
        {
            if (_busy) return;
            _wantFull = full; _wantWu = withWu;
            SetBusy(true, full
                ? (withWu ? "正在枚举硬件并查询 Windows Update 驱动（较慢，约 1~2 分钟）..." : "正在枚举硬件（约 1 分钟）...")
                : "正在检测本机状态 ...");
            RunBg(() =>
            {
                string err;
                var snap = StateProvider.Load(withWu, !full, out err);
                _win.Dispatcher.Invoke(() =>
                {
                    if (snap == null)
                    {
                        SetBusy(false, "检测失败：" + err);
                        AppendLog("检测失败：" + err);
                        return;
                    }
                    _snap = snap;
                    PopulateAll();
                    SetBusy(false, "检测于 " + _snap.generated + (full ? "" : "（硬件信息将在切到驱动/硬件页时加载）"));
                    AskCloudBind();
                });
            });
        }

        private void SetBusy(bool busy, string status)
        {
            _busy = busy;
            // 忙碌期间禁用全部操作按钮, 防止并发触发多个引擎任务 (P1-1)
            foreach (var b in new[] {
                _btnRefresh, _btnInstall, _btnDrivers, _btnAll, _btnNone,
                _btnOpenOem, _btnOpenCleanShare, _btnOpenPkg, _btnImportCleanup,
                _btnBrowsePkg, _btnBrowseRoot,
                _btnSwAll, _btnSwNone, _btnSwInstall, _btnSwPatch, _btnCloudInstall,
                _btnSaveCred, _btnProbeCred, _btnOpenShare, _btnChromiumLogin,
                _btnPluginWorkspace, _btnOpenPluginDir, _btnBrowseAppsRoot, _btnRebuildCatalog,
                _btnCloudAcct })
            {
                b.IsEnabled = !busy;
            }
            // 检测/引擎运行期间给一个置顶提示小窗, 不让用户面对"没反应"的界面
            if (busy) ShowBusy(status ?? "正在处理，请稍候…");
            else HideBusy();
            if (status != null) _txtStatus.Text = status;
        }

        // ---------------------------------------------------------- 云盘绑定询问
        /// <summary>
        /// 每次启动都问一句"是否要绑定云盘账号"。已绑定 (凭据文件存在) 或勾过
        /// 「不再提醒」就不再弹; 绑定成功后凭据落盘, 自然也不再弹。
        /// </summary>
        private void AskCloudBind()
        {
            try
            {
                if (File.Exists(AppPaths.CredFile)) return;   // 已绑定
                var noRemind = Path.Combine(AppPaths.Root, "缓存", "云盘不再提醒.flag");
                if (File.Exists(noRemind)) return;

                var w = new Window
                {
                    Title = "云盘账号",
                    Width = 580,
                    SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = _win,
                    ResizeMode = ResizeMode.NoResize,
                    Background = (System.Windows.Media.Brush)_win.FindResource("PanelBg"),
                };
                var root = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };
                root.Children.Add(new TextBlock
                {
                    Text = "是否要绑定云盘账号来下载相应的安装包？",
                    FontSize = 16,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (System.Windows.Media.Brush)_win.FindResource("TextPrimary"),
                    Margin = new Thickness(0, 0, 0, 12),
                });
                root.Children.Add(new TextBlock
                {
                    Text = "绑定后，启动器会用你的 123 云盘登录态把安装包自动下载到本地缓存再安装，全程不用手动点。\n" +
                           "不绑定的话，可以打开分享页手动把安装包下载到 " + AppPaths.CloudCache + "。\n" +
                           "目前自动下载只支持 123 云盘。",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13,
                    LineHeight = 22,
                    Foreground = (System.Windows.Media.Brush)_win.FindResource("TextSecondary"),
                    Margin = new Thickness(0, 0, 0, 14),
                });
                var chkNoRemind = new CheckBox
                {
                    Content = "不再提醒（之后可以点左侧栏「云盘账号」随时绑定）",
                    FontSize = 12,
                    Foreground = (System.Windows.Media.Brush)_win.FindResource("TextSecondary"),
                    Margin = new Thickness(0, 0, 0, 16),
                };
                root.Children.Add(chkNoRemind);

                var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                var bBind = new Button { Content = "绑定账号", Padding = new Thickness(20, 7, 20, 7) };
                bBind.Click += (s, e) => { w.Close(); OpenBindWindow(); };
                var bLater = new Button { Content = "暂不", Padding = new Thickness(20, 7, 20, 7), Margin = new Thickness(10, 0, 0, 0) };
                bLater.Click += (s, e) =>
                {
                    try
                    {
                        if (chkNoRemind.IsChecked == true)
                        {
                            var dir = Path.GetDirectoryName(noRemind);
                            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                            File.WriteAllText(noRemind, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                        }
                    }
                    catch { }
                    w.Close();
                };
                btns.Children.Add(bBind);
                btns.Children.Add(bLater);
                root.Children.Add(btns);
                w.Content = root;
                w.ShowDialog();
            }
            catch (Exception ex)
            {
                AppendLog("云盘绑定询问弹窗失败: " + ex.Message);
            }
        }

        // ---------------------------------------------------------- 绑定窗口
        /// <summary>
        /// 绑定窗口: 本机配色风格的外壳 + 内嵌 Chromium (WebView2) 显示 123 云盘
        /// 登录页 —— 用户在页面里输账号密码, 登录成功后自动抓取 123pan 域的
        /// cookie 并验证保存。WebView2 不可用时回退为"外部浏览器登录"。
        /// </summary>
        private void OpenBindWindow()
        {
            Window w = null;
            Action dispose = null;
            var lastCookie = "";
            TextBlock status = null;
            Grid host = null;
            StackPanel fallback = null;

            try
            {
                w = new Window
                {
                    Title = "绑定 123 云盘账号",
                    Width = 1100,
                    Height = 720,
                    MinWidth = 800,
                    MinHeight = 560,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = _win,
                    Background = (System.Windows.Media.Brush)_win.FindResource("WindowBg"),
                };

                var root = new Grid();
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                // 顶栏
                var header = new Border
                {
                    Background = (System.Windows.Media.Brush)_win.FindResource("PanelBg"),
                    BorderBrush = (System.Windows.Media.Brush)_win.FindResource("LineBrush"),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(18, 12, 18, 12),
                };
                var headerSp = new StackPanel();
                headerSp.Children.Add(new TextBlock
                {
                    Text = "在下面的内嵌浏览器里登录 123 云盘（就是 123pan.com 的官方登录页）",
                    FontSize = 15,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (System.Windows.Media.Brush)_win.FindResource("TextPrimary"),
                });
                status = new TextBlock
                {
                    Text = "正在启动内嵌浏览器 ...",
                    FontSize = 12,
                    Margin = new Thickness(0, 4, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (System.Windows.Media.Brush)_win.FindResource("TextSecondary"),
                };
                headerSp.Children.Add(status);
                header.Child = headerSp;
                root.Children.Add(header);

                // 中间: 左侧浏览器宿主 + 右侧说明
                var body = new Grid { Margin = new Thickness(12) };
                body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });

                host = new Grid();
                Grid.SetColumn(host, 0);
                var hostBorder = new Border
                {
                    Child = host,
                    Background = System.Windows.Media.Brushes.White,
                    CornerRadius = new CornerRadius(6),
                };
                body.Children.Add(hostBorder);

                var side = new StackPanel { Margin = new Thickness(0, 0, 0, 0) };
                Grid.SetColumn(side, 2);
                side.Children.Add(new TextBlock
                {
                    Text = "怎么绑定",
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (System.Windows.Media.Brush)_win.FindResource("TextPrimary"),
                    Margin = new Thickness(0, 0, 0, 8),
                });
                side.Children.Add(new TextBlock
                {
                    Text = "① 在左侧页面里输入你的 123 云盘账号密码登录（页面就是 123pan.com 官方登录，账号密码只发往 123 云盘）。\n\n" +
                           "② 登录成功后，启动器每隔几秒自动抓取登录态并验证，通过就自动保存、本窗口自动关闭。\n\n" +
                           "③ 也可以登录后自己点下面的「立即验证」。\n\n" +
                           "登录态只保存在本机 E:\\安装系统\\123云盘cookie.txt，只用于下载清单里的分享安装包。",
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 21,
                    FontSize = 12,
                    Foreground = (System.Windows.Media.Brush)_win.FindResource("TextSecondary"),
                });
                var fbBtn = new Button
                {
                    Content = "内嵌浏览器打不开？点这里用外部浏览器登录",
                    Padding = new Thickness(12, 8, 12, 8),
                    Margin = new Thickness(0, 20, 0, 0),
                };
                fbBtn.Click += (s, e) =>
                {
                    try { w.Close(); } catch { }
                    StartChromiumLogin();
                };
                side.Children.Add(fbBtn);
                fallback = new StackPanel { Visibility = Visibility.Collapsed };
                fallback.Children.Add(new TextBlock
                {
                    Text = "本机没有可用的 WebView2 组件/运行时，无法显示内嵌浏览器。",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Foreground = (System.Windows.Media.Brush)_win.FindResource("WarnBrush"),
                    Margin = new Thickness(0, 16, 0, 0),
                });
                side.Children.Add(fallback);
                body.Children.Add(side);
                root.Children.Add(body);

                // 底栏
                var footer = new Border
                {
                    Background = (System.Windows.Media.Brush)_win.FindResource("PanelBg"),
                    BorderBrush = (System.Windows.Media.Brush)_win.FindResource("LineBrush"),
                    BorderThickness = new Thickness(0, 1, 0, 0),
                    Padding = new Thickness(18, 10, 18, 10),
                };
                var fsp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                var bVerify = new Button { Content = "立即验证", Padding = new Thickness(18, 7, 18, 7), Margin = new Thickness(0, 0, 10, 0) };
                bVerify.Click += (s, e) =>
                {
                    if (lastCookie.Length > 20) VerifyBindCookie(lastCookie, w, status);
                    else if (status != null) status.Text = "还没抓到登录态 —— 先在页面里登录，登录成功后这里会自动开始验证。";
                };
                var bClose = new Button { Content = "关闭", Padding = new Thickness(18, 7, 18, 7) };
                bClose.Click += (s, e) => { try { w.Close(); } catch { } };
                fsp.Children.Add(bVerify);
                fsp.Children.Add(bClose);
                footer.Child = fsp;
                root.Children.Add(footer);

                w.Content = root;
                w.Closed += (s, e) => { if (dispose != null) { try { dispose(); } catch { } } };

                // 尝试挂 WebView2 —— 托管 DLL 缺失时这里抛异常, 走回退
                var userDataDir = Path.Combine(AppPaths.Root, "缓存", "WebView2配置");
                if (!Directory.Exists(userDataDir)) Directory.CreateDirectory(userDataDir);
                WebView2Host.Attach(host, "https://www.123pan.com/", userDataDir,
                    cookie =>
                    {
                        lastCookie = cookie;
                        _win.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (status != null) status.Text = "已抓到登录态，正在自动验证 ...";
                            VerifyBindCookie(cookie, w, status);
                        }));
                    },
                    msg => _win.Dispatcher.BeginInvoke(new Action(() => { if (status != null) status.Text = msg; })),
                    out dispose);

                w.ShowDialog();
            }
            catch (Exception ex)
            {
                // WebView2 组件缺失等 —— 回退为外部浏览器登录
                try
                {
                    if (fallback != null && w != null)
                    {
                        if (host != null) host.Visibility = Visibility.Collapsed;
                        fallback.Visibility = Visibility.Visible;
                        if (status != null) status.Text = "内嵌浏览器不可用（" + ex.Message + "），已切换为外部浏览器方式。";
                        w.ShowDialog();
                        return;
                    }
                }
                catch { }
                AppendLog("绑定窗口失败: " + ex.Message);
                MessageBox.Show(_win, "绑定窗口打不开：\n" + ex.Message, "错误");
            }
        }

        /// <summary>把内嵌浏览器抓到的 cookie 交给引擎保存并验证。</summary>
        private void VerifyBindCookie(string cookie, Window w, TextBlock status)
        {
            if (_bindBusy) return;
            _bindBusy = true;
            if (status != null) status.Text = "正在验证并保存登录态 ...";
            RunBg(() =>
            {
                try
                {
                    var json = EngineRunner.SavePanCredential(cookie);
                    _win.Dispatcher.Invoke(() =>
                    {
                        _bindBusy = false;
                        AppendLog(json);
                        ApplyCredResult(json);
                        try
                        {
                            var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
                            var d = (Dictionary<string, object>)ser.DeserializeObject(json);
                            var login = d.ContainsKey("hasLogin") && d["hasLogin"] != null && (bool)d["hasLogin"];
                            if (login)
                            {
                                if (status != null) status.Text = "登录态有效，已保存！以后启动不会再询问绑定。";
                                RefreshCredState();
                                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
                                t.Tick += (s2, e2) => { t.Stop(); try { w.Close(); } catch { } };
                                t.Start();
                            }
                            else
                            {
                                if (status != null) status.Text = "没通过验证：" + (d.ContainsKey("message") ? Convert.ToString(d["message"]) : "") +
                                    "\n请确认在页面里登录成功（能看到网盘首页）后再试。";
                            }
                        }
                        catch { }
                    });
                }
                catch (Exception ex)
                {
                    _bindBusy = false;
                    _win.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (status != null) status.Text = "验证失败: " + ex.Message;
                    }));
                }
            });
        }

        // ---------------------------------------------------------- 检测中提示弹窗
        private void ShowBusy(string text)
        {
            if (_busyNoPopup) return;
            try
            {
                if (_busyWin == null)
                {
                    var w = new Window
                    {
                        Title = "请稍候",
                        Width = 400,
                        SizeToContent = SizeToContent.Height,
                        WindowStyle = WindowStyle.ToolWindow,
                        ResizeMode = ResizeMode.NoResize,
                        ShowInTaskbar = false,
                        Topmost = true,
                        WindowStartupLocation = WindowStartupLocation.CenterScreen,
                        Background = (System.Windows.Media.Brush)_win.FindResource("PanelBg"),
                    };
                    var sp = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };
                    var t = new TextBlock
                    {
                        Text = text,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 14,
                        Foreground = (System.Windows.Media.Brush)_win.FindResource("TextPrimary"),
                    };
                    var bar = new ProgressBar { IsIndeterminate = true, Height = 6, Margin = new Thickness(0, 16, 0, 0) };
                    sp.Children.Add(t);
                    sp.Children.Add(bar);
                    w.Content = sp;
                    _busyWin = w;
                    _busyTxt = t;
                }
                if (_busyTxt != null && text != null) _busyTxt.Text = text;
                if (!_busyWin.IsVisible) _busyWin.Show();
            }
            catch { }
        }

        private void HideBusy()
        {
            if (_busyWin != null)
            {
                try { _busyWin.Close(); } catch { }
                _busyWin = null;
                _busyTxt = null;
            }
        }

        // ------------------------------------------------------------ 填充
        private void PopulateAll()
        {
            var m = _snap.machine ?? new MachineInfo();
            var devTxt = m.deviceCount > 0 ? "   ·   " + m.deviceCount + " 个设备" : "";
            var tag = _snap.partial ? "   ·   快速检测" : "";
            _txtMachine.Text = string.Format("{0} {1}   ·   {2}{3}{4}   ·   检测于 {5}",
                m.oem, m.model, m.os, devTxt, tag, _snap.generated);

            _allRows = _snap.runtimes.Select(r => new RuntimeRow(r)).ToList();
            ApplyFilter();
            PopulateSoftware();
            PopulateCleanup();
            PopulateBasics();
            PopulateComponents();
            PopulateDrivers();
            PopulateHardware();
        }

        private void ApplyFilter()
        {
            var tiers = new List<string>();
            if (_chkCore.IsChecked == true) tiers.Add("核心");
            if (_chkRec.IsChecked == true)  tiers.Add("推荐");
            if (_chkOpt.IsChecked == true)  tiers.Add("按需");
            var onlyMissing = _chkMissing.IsChecked == true;

            var view = _allRows.Where(r => tiers.Contains(r.Item.tier));
            if (onlyMissing) view = view.Where(r => !r.Item.installed);

            var list = view.ToList();
            _runtimeList.ItemsSource = list;

            // 首次 Reload 完成前 _snap 还是 null, 此时点筛选不能崩 (P0-6)
            if (_snap == null)
            {
                if (!_busy) _txtStatus.Text = string.Format("当前显示 {0} 项", list.Count);
                return;
            }

            var sel = _allRows.Count(r => r.Selected);
            var inst = _snap.runtimes.Count(r => r.installed);
            _txtSummary.Text = string.Format("{0} 项 · 已装 {1} · 缺 {2} · 已勾 {3}",
                _snap.runtimes.Count, inst, _snap.runtimes.Count - inst, sel);
            if (!_busy)
                _txtStatus.Text = string.Format("当前显示 {0} 项，已勾选 {1} 项", list.Count, sel);
        }

        private void SetAllSelected(bool on)
        {
            foreach (var r in _allRows) if (r.CanSelect) r.Selected = on;
            ApplyFilter();
        }

        // ------------------------------------------------------------ 软件清单
        // 左侧导航就是分类目录树：全部软件 + 每个分类一个子页面（3D 建模也在里面，
        // 它就是 Modeling3D 这个分类，不再是独立页面）。
        // 目标目录已经定死: <根目录>\<英文分类目录>[\<英文子目录>]
        private void PopulateSoftware()
        {
            var list = _snap.software ?? new List<SoftwareItem>();
            _swRows = list.Select(x => new SoftwareRow(x)).ToList();
            // 英文目录 → 中文分类名（取该目录下第一个非空类别），左侧导航全用中文
            _dirCn = new Dictionary<string, string>();
            foreach (var g in list.GroupBy(x => x.dir))
            {
                var cn = g.Select(x => x.category).FirstOrDefault(c => !string.IsNullOrEmpty(c));
                if (!string.IsNullOrEmpty(cn)) _dirCn[g.Key] = cn;
            }
            BuildCategoryNav();
            ApplySoftwareFilter();
        }

        /// <summary>分类的中文显示名：优先用清单里的中文名，没有再查内置表。</summary>
        private string DirCn(string dir)
        {
            if (_dirCn != null && _dirCn.ContainsKey(dir)) return _dirCn[dir];
            return CategoryChip.Cn(dir);
        }

        private void BuildCategoryNav()
        {
            _navCats.Children.Clear();
            _catNavs.Clear();

            // 数据驱动的中文分类导航: 内置顺序优先, 其余 (插件新增) 按中文名排序
            var allDirs = _swRows.Select(r => r.Item.dir).Distinct().ToList();
            var ordered = allDirs.Where(d => CategoryChip.Order.Contains(d))
                                 .OrderBy(d => Array.IndexOf(CategoryChip.Order, d)).ToList();
            ordered.AddRange(allDirs.Where(d => !CategoryChip.Order.Contains(d))
                                    .OrderBy(DirCn, StringComparer.Create(
                                        System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), true)));
            foreach (var dir in ordered)
            {
                var grp = _swRows.Where(r => r.Item.dir == dir).ToList();
                if (grp.Count == 0) continue;
                AddCategoryNav(dir, grp);
            }

            // 「插件应用」= 第三方插件 (非官方基础插件) 带来的应用聚合子页
            var pluginCount = _swRows.Count(r => IsThirdPartyPlugin(r.Item));
            _navPlugins.Content = string.Format("插件应用 ({0})", pluginCount);
        }

        private static bool IsThirdPartyPlugin(SoftwareItem it)
        {
            return !string.IsNullOrEmpty(it.plugin) && it.plugin != "官方基础软件";
        }

        private void AddCategoryNav(string dir, List<SoftwareRow> grp)
        {
            var installed = grp.Count(r => r.Item.installed);
            var cn = DirCn(dir);
            var rb = new RadioButton();
            rb.Content = string.Format("{0}  ({1}/{2})", cn, installed, grp.Count);
            rb.Style = (Style)_win.FindResource("NavItem");
            rb.Margin = new Thickness(14, 0, 0, 4);   // 子类别缩进
            rb.Tag = dir;
            rb.ToolTip = string.Format("{0}  →  {1}\\    共 {2} 项，已装 {3}", cn, dir, grp.Count, installed);
            rb.Checked += (s, e) =>
            {
                var d = ((RadioButton)s).Tag as string;
                _swCategory = d ?? "";
                ShowPage(0);
                _navOverview.IsChecked = false;
                _navPlugins.IsChecked = false;
                ApplySoftwareFilter();
            };
            _navCats.Children.Add(rb);
            _catNavs.Add(rb);
            if (_swCategory == dir) rb.IsChecked = true;
        }

        private void ApplySoftwareFilter()
        {
            var tiers = new List<string>();
            if (_chkSwCore.IsChecked == true) tiers.Add("核心");
            if (_chkSwRec.IsChecked == true)  tiers.Add("推荐");
            if (_chkSwOpt.IsChecked == true)  tiers.Add("按需");
            var onlyMissing = _chkSwMissing.IsChecked == true;

            var view = _swRows.Where(r => tiers.Contains(r.Item.tier));
            if (_swCategory == "__plugins__")
                view = view.Where(r => IsThirdPartyPlugin(r.Item));
            else if (!string.IsNullOrEmpty(_swCategory))
                view = view.Where(r => r.Item.dir == _swCategory);
            if (onlyMissing) view = view.Where(r => !r.Item.installed);

            var shown = view.OrderBy(r => TierRank(r.Item.tier)).ThenBy(r => r.Item.name).ToList();
            _softwareList.ItemsSource = shown;

            // 插件工作区入口只放在「插件应用」子页
            _pluginBar.Visibility = _swCategory == "__plugins__" ? Visibility.Visible : Visibility.Collapsed;

            var scopeName = _swCategory == "__plugins__" ? "插件应用"
                : string.IsNullOrEmpty(_swCategory) ? "全部软件" : DirCn(_swCategory);
            _txtSwTitle.Text = _swCategory == "__plugins__"
                ? "插件应用   （来自插件，可在「插件工作区」里制作自己的插件）"
                : string.IsNullOrEmpty(_swCategory)
                    ? "全部软件"
                    : string.Format("{0}   （{1}）", scopeName, _swCategory);
            _txtSwDest.Text = _swCategory == "__plugins__"
                ? string.Format("目标目录：{0}\\<插件分类>\\<英文子目录>    这一页共 {1} 项，其中已装 {2} 项",
                    _txtAppsRoot.Text.Trim(), shown.Count, shown.Count(r => r.Item.installed))
                : string.IsNullOrEmpty(_swCategory)
                    ? string.Format("目标目录：{0}\\<英文分类>\\<英文子目录>    分类按左侧导航分开，共 {1} 个分类子树",
                        _txtAppsRoot.Text.Trim(), _swRows.Select(r => r.Item.dir).Distinct().Count())
                    : string.Format("目标目录：{0}\\{1}\\<英文子目录>    这一页共 {2} 项，其中已装 {3} 项",
                        _txtAppsRoot.Text.Trim(), _swCategory,
                        _swRows.Count(r => r.Item.dir == _swCategory),
                        _swRows.Count(r => r.Item.dir == _swCategory && r.Item.installed));

            var total = _swRows.Count;
            var inst = _swRows.Count(r => r.Item.installed);
            var ready = _swRows.Count(r => r.Item.packageReady);
            var sel = _swRows.Count(r => r.Selected);
            var dirs = _swRows.Select(r => r.Item.dir).Distinct().Count();
            _txtSwHint.Text = string.Format(
                "共 {0} 项 · {1} 个英文分类目录 · 已装 {2} · 本地包就位 {3} · 已勾选 {4}   （本页 {5} 项）",
                total, dirs, inst, ready, sel, shown.Count);
            if (!_busy) _txtStatus.Text = string.Format("{0}：已勾选 {1} 项，本页显示 {2} 项", scopeName, sel, shown.Count);
        }

        private static int TierRank(string tier)
        {
            switch (tier)
            {
                case "核心": return 0;
                case "推荐": return 1;
                default: return 2;
            }
        }

        private void SetSoftwareAll(bool on)
        {
            // 全选/全不选只作用于当前这一页（分类子页就只勾这一类）
            foreach (var r in _swRows)
            {
                if (!r.CanSelect) continue;
                if (_swCategory == "__plugins__")
                {
                    if (!IsThirdPartyPlugin(r.Item)) continue;
                }
                else if (!string.IsNullOrEmpty(_swCategory) && r.Item.dir != _swCategory) continue;
                r.Selected = on;
            }
            ApplySoftwareFilter();
        }

        private void StartInstallSoftware(bool patchOnly, bool cloudFirst = false)
        {
            if (_busy) return;
            if (_swRows.Count == 0) { _txtStatus.Text = "没有软件清单数据（清单\\软件目录.csv 没读到）。"; return; }
            var root = _txtAppsRoot.Text.Trim();
            if (string.IsNullOrEmpty(root)) root = AppPaths.AppsRoot;

            var ids = _swRows.Where(r => r.Selected).Select(r => r.Item.id).ToList();
            var cats = _swRows.Where(r => r.Selected).Select(r => r.Item.dir).Distinct().ToList();
            if (ids.Count == 0) { _txtStatus.Text = "先在「软件清单」页勾选要处理的项。"; return; }

            var msg = patchOnly
                ? string.Format("只打补丁：处理已勾选的 {0} 项（从 {1} 开始）。\n\n继续吗？", ids.Count, root)
                : string.Format("即将处理 {0} 项软件，覆盖 {1} 个分类目录。\n\n安装根目录：{2}\n先下云盘：{3}\n\n" +
                                "需要提权的安装器会弹一次 UAC（取消则退回当前用户装）。继续吗？",
                                ids.Count, cats.Count, root, cloudFirst ? "是（用自己的云盘登录态）" : "是（若有登录态）");
            if (MessageBox.Show(_win, msg, patchOnly ? "确认打补丁" : "确认安装软件",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

            ShowPage(5);
            _navLog.IsChecked = true;
            AppendLog(string.Format("================ 软件引擎 开始（{0} 项 / 根目录 {1}）================", ids.Count, root));
            SetBusy(true, "正在安装软件 ...");

            var localIds = ids;
            var localCats = cats;
            RunBg(() =>
            {
                var code = EngineRunner.InstallSoftware(localCats, localIds, root, patchOnly,
                    false, ln => _win.Dispatcher.BeginInvoke(new Action(() => AppendLog(ln))));
                _win.Dispatcher.Invoke(() =>
                {
                    AppendLog(code == 0
                        ? "================ 软件引擎 成功结束，重新检测 ================"
                        : "================ 软件引擎 结束 (退出码 " + code + ")，重新检测 ================");
                    SetBusy(false, code == 0 ? "软件处理结束，正在重新检测 ..." : "软件处理完成，有失败项 (退出码 " + code + ")，正在重新检测 ...");
                    Reload(_wantFull, _wantWu);
                });
            });
        }

        // ------------------------------------------------------------ 插件工作区
        private void OpenPluginWorkspace()
        {
            try
            {
                var win = (Window)Xaml.Parse("PluginWorkspace");
                win.Owner = _win;
                var ctl = new PluginWorkspaceController(win);
                ctl.Applied += () =>
                {
                    AppendLog("插件已生成，正在重读清单 ...");
                    Reload(_wantFull, _wantWu);
                };
                win.ShowDialog();
            }
            catch (Exception ex)
            {
                AppendLog("插件工作区打不开：" + ex.Message);
                MessageBox.Show(_win, "插件工作区打不开：\n" + ex.Message, "错误");
            }
        }

        private void StartRebuildCatalog()
        {
            if (_busy) return;
            ShowPage(5);
            _navLog.IsChecked = true;
            AppendLog("==== 重新汇总 清单\\软件目录.csv ====");
            RunBg(() =>
            {
                var code = EngineRunner.RebuildCatalog(ln => _win.Dispatcher.BeginInvoke(new Action(() => AppendLog(ln))));
                _win.Dispatcher.Invoke(() =>
                {
                    AppendLog(code == 0 ? "==== 汇总结束，重新检测 ====" : "==== 汇总失败 (退出码 " + code + ") ====");
                    SetBusy(false, null);
                    Reload(_wantFull, _wantWu);
                });
            });
        }

        // ------------------------------------------------------------ 3D 建模
        // 3D 建模已经不是独立页面了 —— 它就是 Modeling3D 这个分类子页，
        // 数据走 ModelInstaller.exe 那条单独的流水线，这里保留入口按钮。

        // ------------------------------------------------------------ 安全清理
        private void PopulateCleanup()
        {
            var list = _snap.cleanup;
            var inst = list.Count(c => c.installed);
            var pkg  = list.Count(c => c.package);
            _txtCleanHint.Text = string.Format("共 {0} 项 · 已装 {1} · 安装包就位 {2} · 缺 {3}",
                list.Count, inst, pkg, list.Count - pkg);
            _cleanupList.ItemsSource = list.Select(c => new PlainRow(
                c.name + "   [" + c.category + "]",
                string.Format("{0}\n{1}  ·  {2}", c.note, c.shareFile, c.mode),
                c.installed ? "已装" : (c.package ? "可装" : "缺包"),
                c.installed ? "#2E7D32" : (c.package ? "#2F5D8C" : "#8A6A1F"))).ToList();
        }

        // ------------------------------------------------------------ 基础工具
        private void PopulateBasics()
        {
            var list = _snap.basics;
            var inst = list.Count(b => b.installed);
            var pkg  = list.Count(b => b.package);
            _txtBasicHint.Text = string.Format("共 {0} 项 · 已装 {1} · 安装包就位 {2}", list.Count, inst, pkg);
            _basicList.ItemsSource = list.Select(b => new PlainRow(
                b.name + "   [" + (string.IsNullOrEmpty(b.category) ? "基础工具" : b.category) + "]",
                string.Format("{0}\n{1}  ·  {2}", b.note, b.shareFile, b.mode),
                b.installed ? "已装" : (b.package ? "可装" : "缺包"),
                b.installed ? "#2E7D32" : (b.package ? "#2F5D8C" : "#8A6A1F"))).ToList();
        }

        // ------------------------------------------------------------ 驱动/外设组件
        private void PopulateComponents()
        {
            var list = _snap.components;
            var inst = list.Count(c => c.installed);
            var rows = list.Select(c => new PlainRow(
                c.name + "   [" + c.category + "]",
                c.evidence,
                c.installed ? "已装" : "缺失",
                c.installed ? "#2E7D32" : "#C62828")).ToList();
            if (list.Count == 0)
                rows.Add(new PlainRow("没有组件数据", "清单\\组件.csv 未加载", "", "#4A4A52"));
            else
                rows.Insert(0, new PlainRow(string.Format("驱动/外设组件 {0} 项 · 已装 {1} · 缺 {2}",
                    list.Count, inst, list.Count - inst), "", "", "#4A4A52"));
            _compList.ItemsSource = rows;
        }

        // ------------------------------------------------------------ 驱动页
        private void PopulateDrivers()
        {
            var drv = _snap.wuDrivers.Select(d => new PlainRow(
                d.title,
                string.IsNullOrEmpty(d.kb) ? d.mfr : d.mfr + "  ·  " + d.kb,
                "可装", "#2F5D8C")).ToList();
            _wuList.ItemsSource = drv;

            _fwList.ItemsSource = _snap.wuFirmware.Select(d => new PlainRow(
                d.title, d.mfr, "固件", "#8A6A1F")).ToList();

            _txtWuHint.Text = !_snap.wuQueried
                ? "尚未查询。"
                : (string.IsNullOrEmpty(_snap.wuError)
                    ? string.Format("共 {0} 项驱动更新，{1} 项固件。", drv.Count, _snap.wuFirmware.Count)
                    : "查询失败：" + _snap.wuError);

            _sourceList.ItemsSource = _snap.driverSources.Select(x => new PlainRow(
                x.name + (string.IsNullOrEmpty(x.category) ? "" : "   [" + x.category + "]"),
                x.url + (string.IsNullOrEmpty(x.note) ? "" : "\n" + x.note),
                x.auto ? "可自动" : "手动",
                x.auto ? "#2E7D32" : "#5A5A62")).ToList();

            _periphList.ItemsSource = _snap.peripherals.Count == 0
                ? new List<PlainRow> { new PlainRow("没有匹配到外设配套软件",
                      "本机硬件厂商不在外设软件清单里（清单见 清单\\外设软件.csv）", "", "#4A4A52") }
                : _snap.peripherals.Select(x => new PlainRow(x.name, x.wingetId + "  ·  " + x.note, "可装", "#2F5D8C")).ToList();
        }

        private void PopulateHardware()
        {
            var real = _snap.problems.Where(p => !p.phantom).ToList();
            var ph   = _snap.problems.Where(p => p.phantom).ToList();

            var rows = new List<PlainRow>();
            if (real.Count == 0)
                rows.Add(new PlainRow("真实硬件无缺驱动", "", "", "#2E7D32"));
            foreach (var p in real)
                rows.Add(new PlainRow(p.name, p.reason + "  ·  " + p.instance, "异常", "#C62828"));
            if (ph.Count > 0)
                rows.Add(new PlainRow(ph.Count + " 个 ROOT\\ 虚拟/残留节点缺驱动（无害，通常是卸载残留）", "", "", "#8A6A1F"));
            _problemList.ItemsSource = rows;

            _displayList.ItemsSource = _snap.display.Select(d => new PlainRow(
                d.name, (d.vendor ?? "") + "  ·  " + d.driver, "显示", "#4A4A52")).ToList();
            _netList.ItemsSource = _snap.net.Select(d => new PlainRow(
                d.name, (d.vendor ?? "") + "  ·  " + d.driver, "网络", "#4A4A52")).ToList();
            _audioList.ItemsSource = _snap.audio.Select(d => new PlainRow(
                d.name, (d.vendor ?? "") + "  ·  " + d.driver, "音频", "#4A4A52")).ToList();
            _vendorList.ItemsSource = _snap.vendors.Select(v => new PlainRow(
                v.name, v.classes, v.count + " 个", "#4A4A52")).ToList();
        }

        // ------------------------------------------------------------ 工具
        private void BrowseDir(System.Windows.Controls.TextBox box, string desc)
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.Description = desc;
                dlg.ShowNewFolderButton = true;
                if (System.Windows.Forms.DialogResult.OK == dlg.ShowDialog()) box.Text = dlg.SelectedPath;
            }
        }

        private static void OpenUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            // 数据来自脚本生成的 JSON, 限制协议白名单, 防止意外拉起任意 handler (P2)
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase)) return;
            try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
            catch (Exception ex) { Program.LogCrash("打开链接失败 " + url + ": " + ex.Message); }
        }

        private static void OpenFolder(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
            catch (Exception ex) { Program.LogCrash("打开目录失败 " + path + ": " + ex.Message); }
        }

        // ------------------------------------------------------------ 安装
        private void StartInstallRuntimes()
        {
            if (_busy) return;
            var ids = _allRows.Where(r => r.Selected && r.CanSelect).Select(r => r.Item.id).ToList();
            if (ids.Count == 0) { _txtStatus.Text = "没有勾选任何待安装项。"; return; }

            var ans = MessageBox.Show(_win,
                string.Format("即将安装 {0} 个组件。\n\nwinget 会自行请求提权，过程中可能弹出 UAC 确认。\n\n继续吗？",
                    ids.Count),
                "确认安装", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (ans != MessageBoxResult.OK) return;

            ShowPage(5);
            _navLog.IsChecked = true;
            AppendLog("================ 开始安装 " + ids.Count + " 个运行库 ================");
            SetBusy(true, "正在安装 ...");

            RunBg(() =>
            {
                var code = EngineRunner.InstallRuntimes(ids, ln =>
                    _win.Dispatcher.BeginInvoke(new Action(() => AppendLog(ln))));
                _win.Dispatcher.Invoke(() =>
                {
                    AppendLog(code == 0
                        ? "================ 安装结束，重新检测 ================"
                        : "================ 安装结束 (退出码 " + code + ")，重新检测 ================");
                    SetBusy(false, code == 0 ? "安装结束，正在重新检测 ..." : "安装完成，有失败项 (退出码 " + code + ")，正在重新检测 ...");
                    Reload(_wantFull, _wantWu);
                });
            });
        }

        private void StartInstallDrivers()
        {
            if (_busy) return;
            var n   = _snap != null ? _snap.wuDrivers.Count : 0;
            var fwN = _snap != null ? _snap.wuFirmware.Count : 0;
            var fw  = _chkFirmware.IsChecked == true;

            if (n == 0 && !(fw && fwN > 0))
            {
                MessageBox.Show(_win,
                    "Windows Update 当前没有可安装的驱动更新。\n\n" +
                    (fwN > 0
                        ? string.Format("另有 {0} 项固件更新。固件（BIOS/EC）刷新中途断电会变砖，所以默认不装；确需安装请先勾选「连固件一起安装（有风险）」。", fwN)
                        : "本机驱动均已是最新。"),
                    "无需安装", MessageBoxButton.OK, MessageBoxImage.Information);
                _txtStatus.Text = string.Format("Windows Update 无待装驱动（固件 {0} 项已按策略跳过）。", fwN);
                return;
            }

            var msg = string.Format("即将安装 {0} 个 Windows Update 驱动。", n);
            if (fw) msg += "\n\n注意：已勾选固件更新。刷固件中途断电会变砖，请确保供电稳定。";
            msg += "\n\n此操作需要管理员权限，会弹出 UAC 确认。继续吗？";

            if (MessageBox.Show(_win, msg, "确认安装驱动",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

            ShowPage(5);
            _navLog.IsChecked = true;
            AppendLog("================ 开始安装驱动 ================");
            SetBusy(true, "正在安装驱动 ...");

            RunBg(() =>
            {
                var code = EngineRunner.InstallDrivers(fw, ln =>
                    _win.Dispatcher.BeginInvoke(new Action(() => AppendLog(ln))));
                _win.Dispatcher.Invoke(() =>
                {
                    AppendLog(code == 0
                        ? "================ 驱动安装结束，重新检测 ================"
                        : (code == -1 ? "================ 已取消 UAC 提权 ================"
                                      : "================ 驱动安装结束 (退出码 " + code + ")，重新检测 ================"));
                    SetBusy(false, code == 0 ? "驱动安装结束，正在重新检测 ..."
                                           : code == -1 ? "已取消驱动安装" : "驱动安装失败 (退出码 " + code + ")");
                    if (code != -1) Reload(_wantFull, _wantWu);
                });
            });
        }

        private void StartImportCleanup()
        {
            if (_busy) return;
            var pkg  = _txtPkgDir.Text.Trim();
            var root = _txtRootDir.Text.Trim();
            if (!Directory.Exists(pkg)) { MessageBox.Show(_win, "安装包目录不存在：\n" + pkg, "提示"); return; }
            if (string.IsNullOrEmpty(root)) root = AppPaths.AppsRoot;
            Directory.CreateDirectory(root);

            ShowPage(5);
            _navLog.IsChecked = true;
            AppendLog("==== 导入并安装（安装包：" + pkg + " → 根目录：" + root + "）====");
            SetBusy(true, "正在导入并安装 ...");

            var manifestDir = Path.Combine(AppPaths.Root, "清单");
            RunBg(() =>
            {
                // 各类别各跑一遍, 解压到各自的英文目录 (目录名与 引擎\生成目录.ps1 保持一致)
                foreach (var cfg in new[] {
                    new { m = "安全清理.csv",  d = "security_clear" },
                    new { m = "基础工具.csv",  d = "basic_tools" },
                    new { m = "聊天社交.csv",  d = "chat_social" },
                    new { m = "网盘.csv",      d = "cloud_drive" },
                    new { m = "下载.csv",      d = "download" },
                    new { m = "浏览器.csv",     d = "browser" },
                    new { m = "网络加速.csv",   d = "net_accel" } })
                {
                    _win.Dispatcher.BeginInvoke(new Action(() =>
                        AppendLog("==== 类别: " + cfg.d + " ====")));
                    var code = EngineRunner.Run(AppPaths.CleanupScript,
                        "-Manifest " + Ps.Psq(Path.Combine(manifestDir, cfg.m)) +
                        " -ImportDir " + Ps.Psq(pkg) +
                        " -Root " + Ps.Psq(root) +
                        " -CategoryDir " + Ps.Psq(cfg.d) +
                        " -Install -Yes",
                        ln => _win.Dispatcher.BeginInvoke(new Action(() => AppendLog(ln))));
                    var c = code;
                    _win.Dispatcher.BeginInvoke(new Action(() =>
                        AppendLog("==== " + cfg.d + " 结束，退出码 " + c + " ====")));
                }
                _win.Dispatcher.Invoke(() =>
                {
                    SetBusy(false, "导入安装结束，正在重新检测 ...");
                    Reload(true, false);
                });
            });
        }

        // ------------------------------------------------------------ 日志
        private void AppendLog(string line)
        {
            // AppendText 只追加增量, 不像 _logText += 那样每次全文重设排版 (P1-3)
            _txtLog.AppendText(line + Environment.NewLine);
            // 超过 ~400KB 就丢掉前半段, 防止长日志把内存和 UI 拖垮
            if (_txtLog.Text.Length > 400000)
            {
                var keep = _txtLog.Text.Substring(_txtLog.Text.Length - 200000);
                _txtLog.Text = "……(更早的日志已截断)……\r\n" + keep;
            }
            _logScroll.ScrollToEnd();
        }
    }
}