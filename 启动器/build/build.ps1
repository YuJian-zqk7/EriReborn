<#
    安装系统启动器 · 构建脚本
    ------------------------------------------------------------------
    与 ModelInstaller 同一套机制：机器上没有 .NET SDK / MSBuild，
    所以用 Roslyn 的 csc.exe 直接编译，XAML 当嵌入资源、运行时用
    XamlReader 解析（因此界面里不能写 Click= 这类特性，事件在 C# 里挂）。
#>
[CmdletBinding()]
param(
    [string]$OutputName = 'SetupLauncher',
    [switch]$Run
)

$ErrorActionPreference = 'Stop'
$BuildDir = $PSScriptRoot
$Root     = Split-Path -Parent $BuildDir

$SrcDir  = Join-Path $Root 'src'
$DistDir = Join-Path $Root 'dist'
$ObjDir  = Join-Path $Root 'build\obj'
$Manifest= Join-Path $BuildDir 'app.manifest'
$Icon    = Join-Path $Root 'assets\app.ico'

# Roslyn: 优先用本工程 tools 下的, 否则用 ModelInstaller 里的
$CscExe = Join-Path $Root 'tools\roslyn\tasks\net472\csc.exe'
if (-not (Test-Path $CscExe)) {
    $CscExe = 'E:\Work\ModelInstaller\tools\roslyn\tasks\net472\csc.exe'
}
if (-not (Test-Path $CscExe)) { throw "找不到 Roslyn 编译器 (csc.exe)" }

New-Item -ItemType Directory -Force -Path $DistDir, $ObjDir | Out-Null

# ---------------------------------------------------------------- 引用
$fwDir  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$wpfDir = Join-Path $fwDir 'WPF'
$refs = New-Object System.Collections.Generic.List[string]
foreach ($n in @('System.dll','System.Core.dll','System.Xml.dll','System.Drawing.dll',
                 'System.Xaml.dll','System.Web.Extensions.dll','System.Management.dll',
                 'System.Runtime.Serialization.dll','Microsoft.CSharp.dll','System.Windows.Forms.dll')) {
    $p = Join-Path $fwDir $n
    if (Test-Path $p) { $refs.Add($p) } else { Write-Warning "缺少引用 $n" }
}
foreach ($n in @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll')) {
    $p = Join-Path $wpfDir $n
    if (Test-Path $p) { $refs.Add($p) } else { throw "缺少 WPF 引用 $n" }
}

# WebView2 (内嵌浏览器绑定窗口): 可选组件, tools\webview2 下有就编进去
$wv2Dir = Join-Path $Root 'tools\webview2'
$hasWv2 = Test-Path (Join-Path $wv2Dir 'Microsoft.Web.WebView2.Wpf.dll')
if ($hasWv2) {
    foreach ($n in @('Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.Wpf.dll')) {
        $refs.Add((Join-Path $wv2Dir $n))
    }
    Write-Host '已启用 WebView2 内嵌浏览器 (云盘绑定窗口)' -ForegroundColor Cyan
} else {
    Write-Warning '未找到 tools\webview2 组件, 云盘绑定窗口将回退为外部浏览器登录'
}

$sources = @(Get-ChildItem -LiteralPath $SrcDir -Recurse -Filter '*.cs' -File |
             Sort-Object FullName | Select-Object -ExpandProperty FullName)
$xamls   = @(Get-ChildItem -LiteralPath $SrcDir -Recurse -Filter '*.xaml' -File | Sort-Object FullName)
$dups = $xamls | Group-Object Name | Where-Object { $_.Count -gt 1 }
if ($dups) { throw ("XAML 文件名重复: " + (($dups | ForEach-Object { $_.Name }) -join ', ')) }

$outExe = Join-Path $DistDir "$OutputName.exe"
$a = New-Object System.Collections.Generic.List[string]
$a.AddRange([string[]]@('/nologo','/target:winexe','/platform:anycpu','/langversion:7.3',
    '/nullable:disable','/optimize+','/utf8output','/codepage:65001','/warn:4','/nowarn:1591',
    ('/out:' + $outExe), ('/pdb:' + (Join-Path $ObjDir "$OutputName.pdb"))))
if (Test-Path $Manifest) { $a.Add('/win32manifest:' + $Manifest) }
if (Test-Path $Icon)     { $a.Add('/win32icon:' + $Icon) }
foreach ($r in $refs)  { $a.Add('/reference:' + $r) }
foreach ($x in $xamls) { $a.Add('/resource:' + $x.FullName + ',' + $x.Name) }
foreach ($s in $sources) { $a.Add($s) }

Write-Host "编译中 ($($sources.Count) 个源文件, $($xamls.Count) 个 XAML) ..." -ForegroundColor Cyan
$out = & $CscExe @a 2>&1
$code = $LASTEXITCODE
$out | Where-Object { $_ -and "$_".Trim() } | ForEach-Object { Write-Host $_ }
if ($code -ne 0) { Write-Host "`n编译失败，退出码 $code" -ForegroundColor Red; exit $code }

$fi = Get-Item $outExe
Write-Host ("`n编译成功: {0}  ({1:N1} KB)" -f $fi.FullName, ($fi.Length / 1KB)) -ForegroundColor Green

# WebView2 组件跟 exe 放一起 (native loader 必须在 exe 旁)
if ($hasWv2) {
    foreach ($n in @('WebView2Loader.dll','Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.Wpf.dll')) {
        $src = Join-Path $wv2Dir $n
        if (Test-Path $src) { Copy-Item $src $DistDir -Force }
    }
}

# 同步到根目录的启动位置 (日常双击的是 E:\安装系统\SetupLauncher.exe)
$rootExe = 'E:\安装系统\SetupLauncher.exe'
if (Test-Path $rootExe) {
    try {
        Copy-Item $outExe $rootExe -Force
        if ($hasWv2) {
            foreach ($n in @('WebView2Loader.dll','Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.Wpf.dll')) {
                $src = Join-Path $wv2Dir $n
                if (Test-Path $src) { Copy-Item $src (Split-Path $rootExe -Parent) -Force }
            }
        }
        Write-Host "已同步到 $rootExe" -ForegroundColor Cyan
    }
    catch { Write-Warning "根目录 exe 被占用, 没有同步 (关掉启动器后重新 build 即可): $($_.Exception.Message)" }
}

if ($Run) { & $outExe }
