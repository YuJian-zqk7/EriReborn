# Packs the repository for handoff / external review.
#
# Two artefacts, both excluding build output (bin/obj/publish) and runtime scratch:
#   EriReborn_Handoff_<ts>.zip   full tree incl. assets — the next developer can build it
#   EriReborn_AIReview_<ts>.zip  source + docs + data, no binaries/images — small enough
#                                to hand to another model for diagnosis
#
# Launch through tools\pack.cmd: the machine policy is Restricted, so a bare .ps1
# silently does nothing.

param(
    [switch]$FullOnly,
    [switch]$SlimOnly
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$outDir = Join-Path $root 'packages'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'

$skipDirs = @('bin', 'obj', 'publish', '.git', '.vs', 'TestResults', 'out',
              '.test-temp', '.smoke-data', 'acl-recovery', 'logs', 'packages')
$skipExt = @('.zip', '.user', '.suo', '.cache', '.dbmdl')

# Binaries and artwork are noise for a reviewer reading code: they are large and
# carry no reasoning value, while the JSON manifests that describe them are kept.
$slimExt = @('.png', '.jpg', '.jpeg', '.gif', '.bmp', '.ico', '.cur', '.webp',
             '.dll', '.pdb', '.exe', '.class', '.jar', '.so', '.dylib', '.sig',
             '.wav', '.mp3', '.ttf', '.otf', '.msi', '.apk', '.aab')

function Get-Files([string[]]$extraSkipExt) {
    Get-ChildItem -Path $root -Recurse -File -Force -ErrorAction SilentlyContinue |
        Where-Object {
            $rel = $_.FullName.Substring($root.Length).TrimStart('\', '/')
            $parts = $rel -split '[\\/]'
            -not ($parts | Where-Object { $skipDirs -contains $_ }) -and
            -not ($extraSkipExt -contains $_.Extension.ToLowerInvariant()) -and
            -not ($skipExt -contains $_.Extension.ToLowerInvariant())
        }
}

function New-Zip([string]$name, [string[]]$extraSkipExt, [string]$banner) {
    $dest = Join-Path $outDir "$name`_$stamp.zip"
    if (Test-Path $dest) { Remove-Item $dest -Force }

    $readme = Join-Path $env:TEMP "erireborn_START_HERE_$name.md"
    [System.IO.File]::WriteAllText($readme, $banner, [System.Text.UTF8Encoding]::new($true))

    $files = @(Get-Files $extraSkipExt)
    $zip = [System.IO.Compression.ZipFile]::Open($dest, 'Create')
    try {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $zip, $readme, 'START_HERE.md', 'Optimal') | Out-Null
        foreach ($f in $files) {
            # Entry names must use '/'. A backslash is a legal character in a Unix
            # filename, so an extractor there would flatten the tree into one folder
            # of files literally named "src\EriReborn.Core\...".
            $rel = ($f.FullName.Substring($root.Length).TrimStart('\', '/')) -replace '\\', '/'
            try {
                [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $zip, $f.FullName, $rel, 'Optimal') | Out-Null
            } catch {
                Write-Warning "跳过无法读取的文件: $rel"
            }
        }
    } finally {
        $zip.Dispose()
        Remove-Item $readme -Force -ErrorAction SilentlyContinue
    }

    $size = [math]::Round((Get-Item $dest).Length / 1MB, 1)
    Write-Host ("  {0}  {1} 个文件  {2} MB" -f (Split-Path $dest -Leaf), $files.Count, $size)
    return $dest
}

$banner = @"
# EriReborn — 从这里开始

打包时间：$stamp
仓库根目录：$root
本包已剔除 bin/obj/publish 与运行时临时目录，不含任何构建产物。

## 先读这两份

| 文档 | 内容 |
|---|---|
| docs/HANDOFF.md | **最重要。** 做完了什么、卡在哪、卡住需要什么输入、这个仓库反复出现的 bug 模式 |
| docs/REQUIREMENTS-GAP.md | 规格逐条对照（124 行），状态词只有五个 |
| docs/STATUS.md | 逐轮开发记录，含每轮发现的真问题 |
| README.md | 结构地图与硬规矩 |

## 验证入口（必须用 .cmd，不要用 .ps1）

本机 PowerShell 执行策略是 Restricted：直接调 .ps1 会**静默什么都不做**，
看起来就像"跑完了但没输出"。所以一律走 .cmd 启动器。

    tools\regression.cmd    -> 必须打印 REGRESSION OK
    tools\mutate.cmd        -> 必须打印 MUTATION RUN OK
    tools\pack.cmd          -> 生成本包

最新一次回归结果见 docs/regression-last.txt，变异结果见 docs/mutation-last.txt。

## 结构

    src/EriReborn.Core                领域模型 / 目录 / 状态存储（零项目引用）
    src/EriReborn.Platform.Abstractions 平台契约
    src/EriReborn.Cloud               五家网盘 Provider，完全平级
    src/EriReborn.Engine              软件引擎 / 下载 / 插件 / AI
    src/EriReborn.Skin|Asset|Layout|Persona  表现层数据
    src/EriReborn.Extension           扩展与市场（隔离加载上下文）
    src/EriReborn.Platform.Windows|Android   平台真实实现
    src/EriReborn.App.Shared          共享服务装配
    src/EriReborn.UI.Avalonia         界面
    src/EriReborn.Desktop|Mobile      两个可执行入口
    tests/  单元测试（Core + Windows 两个套件）
    tools/  回归 / 变异 / 烟雾 / 测试运行器 / 扩展宿主 / 打包
    assets/ 皮肤与素材（asset_manifest.json 是唯一权威清单）
    docs/   见上

分层由 ArchitectureInvariantTests 读 csproj 与源码守住，越界引用会被拦下。

## 本包不含

- 构建产物（bin/obj/publish）、运行时临时目录
- 凭据、Token、密钥（只走 SecureCredentialStore / DPAPI，不入库）
- 官方目录签名私钥（已有意丢弃，见 HANDOFF 2.5）

## 最近的改动（2026-10-05）

- 修复侧栏横向滚动条丢失（TreeView 显式声明 ScrollViewer.HorizontalScrollBarVisibility，
  nav 样式并不提供它，注释曾承诺但 XAML 未兑现）
- 插件页改为与拓展页一致的布局：顶部按钮行 + 单列滚动，去掉挤压内容的左侧导航
- 官方插件重写：新增 tools/generate-official-plugin.mjs，从 assets/catalog 真实数据
  生成 263 条资源（原随包插件只有 8 条）
- 修复 PluginReader 不读 wingetId，导致插件里的 Winget 源无法安装
- 清理死代码：移除 6 个从未被任何解决方案或脚本构建的扩展壳工程
  （25 行的 IExtension 薄壳，功能已由 src/EriReborn.Cloud 的 Provider 覆盖）、
  临时草稿目录 .test-temp、无人引用的 probe.html
"@

Write-Host "打包中（输出到 packages\）:"
if (-not $SlimOnly) { $full = New-Zip 'EriReborn_Handoff' @() $banner }
if (-not $FullOnly) { $slim = New-Zip 'EriReborn_AIReview' $slimExt $banner }

Write-Host ""
Write-Host "完成： $outDir"
