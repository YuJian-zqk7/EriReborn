using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using SetupLauncher.Core;

namespace SetupLauncher.Services
{
    /// <summary>定位安装系统根目录。</summary>
    public static class AppPaths
    {
        public static string Root { get; private set; }

        static AppPaths()
        {
            // 1) 从 exe 所在目录往上找带 引擎\导出状态.ps1 的目录
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            for (var d = dir; d != null; d = d.Parent)
            {
                var probe = Path.Combine(d.FullName, "引擎", "导出状态.ps1");
                if (File.Exists(probe)) { Root = d.FullName; break; }
            }
            // 2) 退回到固定位置
            if (Root == null && File.Exists(@"E:\安装系统\引擎\导出状态.ps1")) Root = @"E:\安装系统";
            if (Root == null) Root = AppDomain.CurrentDomain.BaseDirectory;

            Scripts = Path.Combine(Root, "引擎");
            Manifest = Path.Combine(Root, "清单", "运行库.csv");
            Catalog = Path.Combine(Root, "清单", "软件目录.csv");
            StateJson = Path.Combine(Root, "清单", "状态.json");
            LogDir = Path.Combine(Root, "日志");
            ReportDir = Path.Combine(Root, "报告");
            if (!Directory.Exists(LogDir)) Directory.CreateDirectory(LogDir);
        }

        public static string Scripts { get; private set; }
        public static string Manifest { get; private set; }
        public static string Catalog { get; private set; }
        public static string StateJson { get; private set; }
        public static string LogDir { get; private set; }
        public static string ReportDir { get; private set; }

        public static string ExportScript { get { return Path.Combine(Scripts, "导出状态.ps1"); } }
        public static string RuntimeScript { get { return Path.Combine(Scripts, "系统安装.ps1"); } }
        public static string DriverScript { get { return Path.Combine(Scripts, "驱动安装.ps1"); } }
        public static string CleanupScript { get { return Path.Combine(Scripts, "安全清理.ps1"); } }
        public static string SoftwareScript { get { return Path.Combine(Scripts, "安装软件.ps1"); } }
        public static string CatalogScript { get { return Path.Combine(Scripts, "生成目录.ps1"); } }
        public static string PluginScript { get { return Path.Combine(Scripts, "插件管理.ps1"); } }
        public static string PluginDir { get { return Path.Combine(Root, "插件"); } }
        public static string LoginScript { get { return Path.Combine(Scripts, "网盘登录.ps1"); } }
        public static string CredHelperScript { get { return Path.Combine(Scripts, "网盘凭据.ps1"); } }
        public static string LibDir { get { return Path.Combine(Scripts, "库"); } }
        public static string ModelRoot { get { return @"E:\Work"; } }
        public static string CleanupPkgDir { get { return @"E:\安全软件"; } }
        public static string CloudCache { get { return Path.Combine(Root, "缓存", "安装包"); } }
        public static string CredFile { get { return Path.Combine(Root, "123云盘cookie.txt"); } }
        public static string AppsRoot { get { return @"E:\Apps"; } }
        public static string ShareUrl { get { return "https://1828566527.share.123pan.cn/123pan/2KXljv-OaNUv"; } }
        public static string SafeRoot { get { return @"E:\Apps\security_clear"; } }
    }

    /// <summary>PowerShell 调用封装。</summary>
    public static class Ps
    {
        public static readonly string Exe =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                         @"WindowsPowerShell\v1.0\powershell.exe");

        /// <summary>
        /// PowerShell 单引号字符串转义：'' 是字面单引号。
        /// 所有来自界面/用户的参数都必须经过这里再拼进 -Command，防注入。
        /// </summary>
        public static string Psq(string s)
        {
            return "'" + (s ?? "").Replace("'", "''") + "'";
        }

        /// <summary>
        /// 构造 -Command 参数。前面挂一段把输出编码设成 UTF-8 的前导语句，
        /// 否则 PowerShell 5.1 重定向时会用 OEM 代码页，中文会变乱码。
        /// </summary>
        public static string Command(string scriptPath, string args)
        {
            return string.Format(
                "-NoProfile -ExecutionPolicy Bypass -Command \"[Console]::OutputEncoding=[Text.Encoding]::UTF8; & {0}{1}\"",
                Psq(scriptPath), string.IsNullOrEmpty(args) ? "" : " " + args);
        }
    }

    /// <summary>读取一次完整状态快照。</summary>
    public static class StateProvider
    {
        public static StateSnapshot Load(bool withWu, out string error)
        {
            return Load(withWu, false, out error);
        }

        public static StateSnapshot Load(bool withWu, bool runtimesOnly, out string error)
        {
            error = null;
            try
            {
                var tmp = Path.Combine(Path.GetTempPath(), "setup_state_" + Guid.NewGuid().ToString("N") + ".json");
                var args = "-Out " + Ps.Psq(tmp);
                if (withWu) args += " -WithWu";
                if (runtimesOnly) args += " -RuntimesOnly";

                var psi = new ProcessStartInfo(Ps.Exe, Ps.Command(AppPaths.ExportScript, args));
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;

                using (var p = Process.Start(psi))
                {
                    // 两个管道必须异步并发读: 先同步 ReadToEnd stdout 再读 stderr,
                    // stderr 写满缓冲区 (~4KB) 时子进程会卡在写 stderr 上, 互相死锁 (P0-2)
                    var stdout = new StringBuilder();
                    var stderr = new StringBuilder();
                    p.OutputDataReceived += (s, e) => { if (e.Data != null) { lock (stdout) stdout.AppendLine(e.Data); } };
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) { lock (stderr) stderr.AppendLine(e.Data); } };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(15 * 60 * 1000))
                    {
                        try { p.Kill(); } catch { }
                        error = "状态导出超时 (15 分钟), 已终止";
                        return null;
                    }
                    p.WaitForExit();   // 确保异步输出全部冲刷完
                    if (p.ExitCode != 0 || !File.Exists(tmp))
                    {
                        error = stderr.Length > 0 ? stderr.ToString() : stdout.ToString();
                        return null;
                    }
                }

                var json = File.ReadAllText(tmp, Encoding.UTF8);
                try { File.Delete(tmp); } catch { }

                var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var snap = ser.Deserialize<StateSnapshot>(json);
                if (snap != null) File.WriteAllText(AppPaths.StateJson, json, new UTF8Encoding(false));
                return snap;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }
    }

    /// <summary>调用安装引擎。</summary>
    public static class EngineRunner
    {
        /// <summary>启动一个持续输出的进程。返回退出码，回调逐行收到输出。</summary>
        public static int Run(string scriptPath, string args, Action<string> onLine)
        {
            var psi = new ProcessStartInfo(Ps.Exe, Ps.Command(scriptPath, args));
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;

            using (var p = Process.Start(psi))
            {
                p.OutputDataReceived += (s, e) => { if (e.Data != null && onLine != null) onLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null && onLine != null) onLine(e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                // 长任务 (批量下载) 可能远超 30 分钟, 给 2 小时兜底, 防止界面永久忙碌
                if (!p.WaitForExit(120 * 60 * 1000))
                {
                    try { p.Kill(); } catch { }
                    if (onLine != null) onLine("[错误] 引擎超过 2 小时未结束, 已强制终止");
                    try { p.WaitForExit(5000); } catch { }
                    return -9;
                }
                p.WaitForExit();   // 确保异步输出全部冲刷完
                return p.ExitCode;
            }
        }

        /// <summary>
        /// 提权运行。UseShellExecute=true 时拿不到重定向的输出，
        /// 所以让子进程自己把输出重定向到文件，跑完再读回来。
        /// </summary>
        public static int RunElevated(string scriptPath, string args, string outFile, Action<string> onLine)
        {
            var inner = string.Format("& {0}{1} *> {2}",
                                      Ps.Psq(scriptPath),
                                      string.IsNullOrEmpty(args) ? "" : " " + args,
                                      Ps.Psq(outFile));
            var psi = new ProcessStartInfo(Ps.Exe,
                "-NoProfile -ExecutionPolicy Bypass -Command \"" + inner + "\"");
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            psi.WindowStyle = ProcessWindowStyle.Hidden;

            int exitCode;
            try
            {
                using (var p = Process.Start(psi))
                {
                    // UseShellExecute=true 时拿不到重定向输出，只能让子进程自己写文件。
                    // 但绝不能等它退出再一次性读 —— 装驱动要几十秒，界面上只有一行会让人
                    // 以为"点了没反应"。这里轮询文件，把新增行实时喂给界面。
                    int seen = 0;
                    while (!p.HasExited)
                    {
                        seen = Pump(outFile, seen, onLine);
                        System.Threading.Thread.Sleep(400);
                        try { p.Refresh(); } catch { }
                    }
                    Pump(outFile, seen, onLine);
                    exitCode = p.ExitCode;
                }
            }
            catch (Win32Exception wex)
            {
                // 1223 (ERROR_CANCELLED) = 用户在 UAC 上点了取消; 其它启动失败另给码
                return wex.NativeErrorCode == 1223 ? -1 : -2;
            }
            catch (Exception)
            {
                return -2;
            }
            return exitCode;   // 透传脚本真实退出码, 失败不再被当成 0 (P0-3)
        }

        /// <summary>把输出文件里第 seen 行之后的新行交给回调，返回已读行数。</summary>
        private static int Pump(string outFile, int seen, Action<string> onLine)
        {
            try
            {
                if (!File.Exists(outFile)) return seen;
                string raw;
                using (var fs = new FileStream(outFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                    raw = sr.ReadToEnd();
                if (raw.Length == 0) return seen;

                bool endsNl = raw.EndsWith("\n");
                var lines = raw.Replace("\r\n", "\n").Split('\n');
                int avail = endsNl ? lines.Length - 1 : lines.Length;
                for (int i = seen; i < avail; i++)
                    if (onLine != null && lines[i].Length > 0) onLine(lines[i]);
                return Math.Max(seen, avail);
            }
            catch { return seen; }
        }

        /// <summary>安装指定运行库（winget 会自行处理提权，无需 elevated）。</summary>
        public static int InstallRuntimes(IEnumerable<string> ids, Action<string> onLine)
        {
            var list = string.Join(",", ids);
            if (string.IsNullOrEmpty(list)) return 0;
            return Run(AppPaths.RuntimeScript, "-Only " + Ps.Psq(list) + " -Yes", onLine);
        }

        /// <summary>安装 Windows Update 驱动（必须提权）。</summary>
        public static int InstallDrivers(bool includeFirmware, Action<string> onLine)
        {
            var outFile = Path.Combine(AppPaths.LogDir, "驱动_界面输出.txt");
            var args = "-Yes" + (includeFirmware ? " -IncludeFirmware" : "");
            return RunElevated(AppPaths.DriverScript, args, outFile, onLine);
        }

        /// <summary>
        /// 统一软件引擎：装清单上勾选的软件。
        /// categories = 英文分类目录列表（勾了哪个类别就只处理哪个类别）。
        /// </summary>
        public static int InstallSoftware(IEnumerable<string> categories, IEnumerable<string> ids,
                                          string root, bool patch, bool offline, Action<string> onLine)
        {
            var sb = new StringBuilder();
            sb.Append(" -Root ").Append(Ps.Psq(root));
            if (patch) sb.Append(" -PatchOnly");
            if (offline) sb.Append(" -NoCloudDownload");
            var cats = categories == null ? "" : string.Join(",", categories);
            if (!string.IsNullOrEmpty(cats)) sb.Append(" -Category ").Append(Ps.Psq(cats));
            var idList = ids == null ? "" : string.Join(",", ids);
            if (!string.IsNullOrEmpty(idList)) sb.Append(" -Only ").Append(Ps.Psq(idList));
            sb.Append(" -Install -Yes");
            return Run(AppPaths.SoftwareScript, sb.ToString(), onLine);
        }

        /// <summary>重新汇总 清单\软件目录.csv（清单改了以后跑一次）。</summary>
        public static int RebuildCatalog(Action<string> onLine)
        {
            return Run(AppPaths.CatalogScript, "", onLine);
        }

        /// <summary>
        /// 开一个 Chromium（Edge/Chrome）窗口让用户自己登录 123 云盘，
        /// 登录完把登录态取回来并验证。返回结果 JSON。
        /// </summary>
        public static string StartChromiumLogin(int timeoutSec, Action<string> onLine)
        {
            var outFile = Path.Combine(Path.GetTempPath(), "pan_login_" + Guid.NewGuid().ToString("N") + ".json");
            var args = "-ResultJson " + Ps.Psq(outFile) + " -TimeoutSec " + timeoutSec;
            try { Run(AppPaths.LoginScript, args, onLine); }
            catch (Exception ex) { return "{\"saved\":false,\"message\":\"" + ex.Message.Replace("\"", "'") + "\"}"; }

            try
            {
                if (!File.Exists(outFile)) return "{\"saved\":false,\"message\":\"登录脚本没有输出结果\"}";
                var json = File.ReadAllText(outFile, Encoding.UTF8).Trim();
                File.Delete(outFile);
                return json;
            }
            catch (Exception ex) { return "{\"saved\":false,\"message\":\"" + ex.Message.Replace("\"", "'") + "\"}"; }
        }

        /// <summary>
        /// 保存 123 云盘登录态并立刻验证。
        /// 返回 JSON: { saved, hasLogin, code, shareCount, probeFile, message }
        /// </summary>
        public static string SavePanCredential(string credential)
        {
            var inFile = Path.Combine(Path.GetTempPath(), "pan_in_" + Guid.NewGuid().ToString("N") + ".txt");
            var outFile = Path.Combine(Path.GetTempPath(), "pan_cred_" + Guid.NewGuid().ToString("N") + ".json");
            try { File.WriteAllText(inFile, credential ?? "", new UTF8Encoding(false)); }
            catch (Exception ex) { return "{\"saved\":false,\"message\":\"" + ex.Message.Replace("\"", "'") + "\"}"; }

            var args = "-CredFileIn " + Ps.Psq(inFile) + " -Out " + Ps.Psq(outFile);
            try { Run(AppPaths.CredHelperScript, args, null); }
            catch (Exception ex) { return "{\"saved\":false,\"message\":\"" + ex.Message.Replace("\"", "'") + "\"}"; }

            try
            {
                if (!File.Exists(outFile)) return "{\"saved\":false,\"message\":\"探测脚本没有输出\"}";
                var json = File.ReadAllText(outFile, Encoding.UTF8);
                File.Delete(outFile);
                return json;
            }
            catch (Exception ex) { return "{\"saved\":false,\"message\":\"" + ex.Message.Replace("\"", "'") + "\"}"; }
            finally { try { File.Delete(inFile); } catch { } }
        }

        /// <summary>本地读回登录态状态（不联网）。</summary>
        public static string ReadPanCredentialState()
        {
            try
            {
                if (!File.Exists(AppPaths.CredFile)) return "";
                var text = File.ReadAllText(AppPaths.CredFile, Encoding.UTF8).Trim();
                return text;
            }
            catch { return ""; }
        }

        /// <summary>直接跑一段 PowerShell，不关心脚本文件。</summary>
        public static int RunRaw(string commandBody, Action<string> onLine)
        {
            var psi = new ProcessStartInfo(Ps.Exe,
                "-NoProfile -ExecutionPolicy Bypass -Command \"" + commandBody.Replace("\"", "\\\"") + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            using (var p = Process.Start(psi))
            {
                var o = new StringBuilder();
                var e = new StringBuilder();
                p.OutputDataReceived += (s, ev) => { if (ev.Data != null) { lock (o) o.AppendLine(ev.Data); } };
                p.ErrorDataReceived += (s, ev) => { if (ev.Data != null) { lock (e) e.AppendLine(ev.Data); } };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(5 * 60 * 1000))
                {
                    try { p.Kill(); } catch { }
                    return -9;
                }
                p.WaitForExit();
                if (onLine != null)
                {
                    foreach (var ln in o.ToString().Replace("\r\n", "\n").Split('\n'))
                        if (ln.Trim().Length > 0) onLine(ln);
                    foreach (var ln in e.ToString().Replace("\r\n", "\n").Split('\n'))
                        if (ln.Trim().Length > 0) onLine(ln);
                }
                return p.ExitCode;
            }
        }
    }
}
