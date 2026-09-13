---
name: install-system
description: 安装系统项目（E:\安装系统 / 本仓库）的架构与原理说明。当需要理解本项目的工作方式、修改引擎脚本或启动器、排查数据流问题、重新生成插件/清单时使用此 skill。This skill should be used whenever working on the SetupLauncher WPF app, the PowerShell engine scripts, the plugin/manifest data pipeline, or the 123pan download integration in this repository.
---

# 安装系统 · 项目原理

三层结构：**启动器 (C# WPF 壳)** → **引擎 (PowerShell)** → **数据 (清单 CSV / 插件 JSON / 123 云盘)**。
界面只做壳；所有检测/下载/安装逻辑都在引擎脚本里，保证 GUI 与命令行结论一致。

## 数据流水线（改数据必看）

```
清单\完整名单.txt ─┐
清单\*.csv ────────┤
                   ▼
引擎\生成基础插件.ps1  →  插件\00-官方基础软件.json      (官方插件, 名单驱动, [有]映射+winget/官网补[补])
插件\*.json ────────►  引擎\插件管理.ps1 -RefreshOnly  →  清单\插件目录.csv   (插件应用索引)
清单\运行库.csv 等 ─►  引擎\生成目录.ps1               →  清单\软件目录.csv  (唯一软件来源)
                            │  全插件模式: 基础插件存在时, 除 运行库/组件 外的 CSV 分类全部跳过
                            ▼
引擎\导出状态.ps1  →  清单\状态.json (英文键 JSON)  →  启动器 StateProvider 加载显示
```

安装入口分四条，互不重叠：
- `引擎\安装软件.ps1` —— 软件目录.csv 的统一入口（本地包/winget/官网直链/云盘文件/云盘整目录）
- `引擎\系统安装.ps1` —— 运行库.csv 专属流水线（winget + 直链 SHA256 校验 + 静默参数）
- `引擎\驱动安装.ps1` —— Windows Update 驱动 + 外设软件（自行 UAC 提权）
- `引擎\安全清理.ps1` —— 本地导入模式（`-ImportDir`），启动器"导入并安装"按钮调用

## 引擎库（引擎\库\，全部 dot-source 使用）

| 文件 | 职责 |
|---|---|
| 检测.ps1 | `Test-RuntimeItem` 探针 (arp/reg/dotnet/video/sound/pnp/msix/file/webview2)，带缓存；`_MatchParam` 保证空参数不命中、坏正则降级字面匹配 |
| SoftwareLibrary.ps1 | 软件目录解释器 (Get-SoftwareCatalog)、落地决策 (Resolve-SoftwareAction: package/winget/download/dist/manual)、安装 (Install-SoftwarePackage)、补丁 (Invoke-SoftwarePatch)、直链下载 (Save-RemoteFile)、7z 解压 |
| 123云盘.ps1 | 123pan 分享 API：列目录 (Get-Pan123List/Tree)、取直链 (需登录态, 5112)、下载 (Save-Pan123File, 重试+.part+可选 SkipHash) |
| 网盘目录.ps1 | 凭据存取 (cookie.txt, ACL 限制)、登录态验证、批量下载计划 (Get-CatalogDownloadPlan)、**目录型条目** (Match-RemoteFolder + Get-CommonPathPrefix, 整目录按相对结构下载) |
| 硬件.ps1 | PCI/USB 厂商号识别, 跨设备无硬编码; 驱动源/OEM 页映射 |
| 插件解析.ps1 | 插件 JSON 读取/校验/归一化 (Convert-PluginApp), Get-EnSlug 中文名→英文目录 |
| 日志.ps1 | Limit-LogRetention: 日志/报告 30 天+数量截断 |

## 启动器（启动器\src\）

- **构建**：`build\build.ps1` 用 Roslyn csc 直编 (无 MSBuild/SDK)，XAML 作为嵌入资源、运行时 `XamlReader.Parse`（`Xaml.cs`，`__ASM__` 占位符替换程序集名）。**因此 XAML 里不能写 `Click=`，所有事件在 C# `Wire()` 里手工挂接**。构建成功自动同步 exe 到根目录。
- `Launcher.cs` (MainWindowController)：页面导航、检测/安装任务统一走 `RunBg()`（含 try/catch，异常必须落到 SetBusy(false)，否则界面永久"忙碌"）、`SetBusy` 禁用全部按钮 + 置顶提示小窗、`AskCloudBind`（每次启动询问绑定云盘，勾"不再提醒"写 `缓存\云盘不再提醒.flag`）、`OpenBindWindow`（WebView2 内嵌 123pan 登录页，DevTools 协议取 123pan 域 cookie 自动验证；WebView2 缺失回退 `StartChromiumLogin` 外部浏览器 --app + CDP 轮询）。
- `Engine.cs`：`Ps.Psq()` 单引号转义（所有用户输入拼 PowerShell 参数必须过它）、`StateProvider.Load`（异步读双管道防死锁）、`RunElevated`（提权, 透传退出码, 1223=UAC 取消）。
- 分类导航数据驱动：`BuildCategoryNav` 按 `软件目录.csv` 的 dir 生成中文子项（DirCn 优先取数据 category），`__plugins__` 聚合页 = 第三方插件（plugin != "官方基础软件"）。
- 日志：`AppendLog` 用 `AppendText`，超 400KB 截断；行回调一律 `Dispatcher.BeginInvoke`。

## 铁律与坑（违反必出问题）

1. **编码**：所有 .ps1 必须 UTF-8 **with BOM**（PowerShell 5.1 无 BOM 按 GBK 读，中文注释会直接炸语法）。新写/重写脚本后跑一次 BOM 检查补 BOM。
2. **PowerShell 5.1**：`[ordered]@{}` 是 OrderedDictionary —— 用 `.Contains()`，**没有 `.ContainsKey()`**。
3. **传参**：C# 拼引擎命令行必须 `Ps.Psq()` 转义；引擎侧 `Start-Process -ArgumentList` 用参数数组 + `"`"` 引号，禁止 `-Command` 字符串拼接用户输入。
4. **云盘 429**：接口限频极严。列目录/下载都要带登录态；批量下载文件间 `Start-Sleep`；`Save-Pan123File` 已带 3 次重试；不需要哈希就传 `-SkipHash`（云盘不给期望值，算了也没用）。
5. **静默参数**：`mode=安装` 的 exe 若清单没写 `silent`，`Install-SoftwarePackage` 会 WARN 并直跑（可能弹 UI 卡住 `-Wait`）。新加安装类条目尽量带 `"silent": "/S"` 或走 winget。
6. **.part 半成品**：下载临时文件名 `*.part`，失败必须清理，成功才 Move——不要绕过 Save-RemoteFile/Save-Pan123File 自己写下载。
7. **检测探针**：检测参数为空必须"不命中"（防 `-match ''` 恒真误判已装），非法正则降级字面匹配；新探针照 `_MatchParam` 模式写。
8. **目录名**：英文目录是数据（`软件目录.csv` 英文目录列 / 插件 categories.dir），界面一律用中文别名显示（数据 category 列）；历史遗留 `security_clear` 与旧 `Security` 并存只做兼容。
9. **凭据**：`123云盘cookie.txt` 明文本机存（ACL 已限当前用户），永远不进仓库/云盘/日志。
10. **自检**：`SetupLauncher.exe --selftest` 全绿才叫构建成功；改启动器必跑。

## 常用操作

```powershell
# 数据再生成（改名单/清单/插件后）
引擎\生成基础插件.ps1 ; 引擎\插件管理.ps1 -RefreshOnly ; 引擎\生成目录.ps1
# 构建启动器（自动同步根目录 exe）
启动器\build\build.ps1
# 自检
SetupLauncher.exe --selftest
```

新增一个软件：优先在启动器「插件工作区」做（可多分区、自动生成 JSON）；批量改动改 `清单\完整名单.txt`（[有]=云盘已有带 `→ 文件名` 映射；[补]=官方来源，在 `生成基础插件.ps1` 的 `$extraDefs` 表里配 wingetId 或官网链接）后重跑数据流水线。
