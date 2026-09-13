#Requires -Version 5.1
<#
  网盘登录.ps1  --  用 Chromium 内核开一个窗口让用户自己登录, 登录完把登录态取回来

  为什么这么做: 手动从 F12 里抠 cookie/token 太反人类。这里直接:
    1) 用本机的 Edge/Chrome 以 --app 模式打开 123 云盘 (Chromium 内核)
    2) 同时开 CDP 调试端口 (--remote-debugging-port)
    3) 轮询调试端口, 读 localStorage + 该站 cookie
    4) 拿到的候选凭据逐个去真的取一次 123 云盘直链验证
    5) 把能用的那份写进 123云盘cookie.txt

  用户只做一件事: 在弹出的窗口里登录。登录完关掉窗口, 脚本自己收尾。

  关键点: --user-data-dir 指向独立目录, 不碰日常浏览器配置, 也不会因为
  浏览器已经在跑而开不了调试端口。

  用法:
    网盘登录.ps1                       开窗口, 登录完自动取凭据并验证
    网盘登录.ps1 -ResultJson <文件>     结果写到指定 JSON
    网盘登录.ps1 -TimeoutSec 600       最长等多久 (默认 10 分钟)
#>
[CmdletBinding()]
param(
    [string] $ShareUrl   = 'https://1828566527.share.123pan.cn/123pan/2KXljv-OaNUv',
    [string] $LoginUrl   = 'https://www.123pan.com/',
    [string] $LibDir     = 'E:\安装系统\引擎\库',
    [string] $LogDir     = 'E:\安装系统\日志',
    [string] $ProfileDir = 'E:\安装系统\缓存\浏览器配置',
    [string] $CredFile   = 'E:\安装系统\123云盘cookie.txt',
    [string] $ResultJson,
    [int]    $DebugPort  = 9223,
    [int]    $TimeoutSec = 600,
    [string] $BrowserPath
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
if (-not (Test-Path $LogDir)) { New-Item -ItemType Directory -Force -Path $LogDir | Out-Null }
$script:LogFile = Join-Path $LogDir ("网盘登录_{0:yyyyMMdd_HHmmss}.log" -f (Get-Date))
. (Join-Path $LibDir '日志.ps1')
Limit-LogRetention -Dir $LogDir

function Write-Log {
    param([string]$Message, [string]$Level = 'INFO')
    $line = '[{0:HH:mm:ss}][{1}] {2}' -f (Get-Date), $Level, $Message
    switch ($Level) {
        'OK'   { Write-Host $line -ForegroundColor Green }
        'WARN' { Write-Host $line -ForegroundColor Yellow }
        'ERR'  { Write-Host $line -ForegroundColor Red }
        'HEAD' { Write-Host ''; Write-Host $line -ForegroundColor Cyan }
        default { Write-Host $line }
    }
    try { Add-Content -LiteralPath $script:LogFile -Value $line -Encoding UTF8 } catch { }
}

function Write-Result {
    param($Obj)
    $json = $Obj | ConvertTo-Json -Compress
    if ($ResultJson) { [IO.File]::WriteAllText($ResultJson, $json, (New-Object Text.UTF8Encoding $false)) }
    Write-Host ("RESULT " + $json)
}

# ------------------------------------------------------------------ 找浏览器
function Find-Chromium {
    if ($BrowserPath -and (Test-Path $BrowserPath)) { return $BrowserPath }
    $cands = @(
        "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
        "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
        "$env:LOCALAPPDATA\Microsoft\Edge\Application\msedge.exe",
        "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
        "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
        "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe"
    )
    foreach ($c in $cands) { if (Test-Path $c) { return $c } }
    return $null
}

# ------------------------------------------------------------------ CDP
function Get-CdpTargets {
    param([int]$Port)
    foreach ($u in @("http://127.0.0.1:$Port/json/list", "http://127.0.0.1:$Port/json")) {
        try {
            $r = Invoke-WebRequest -Uri $u -UseBasicParsing -TimeoutSec 3
            $txt = [Text.Encoding]::UTF8.GetString($r.RawContentStream.ToArray())
            if ($txt) { return ($txt | ConvertFrom-Json) }
        } catch { }
    }
    return $null
}

function Invoke-Cdp {
    <# 连一次 WebSocket, 发一条 CDP 命令, 取回整条响应。失败返回 $null。 #>
    param([string]$WsUrl, [string]$Method, $Params = $null)
    $ws = $null
    try {
        $ws = New-Object System.Net.WebSockets.ClientWebSocket
        $cts = New-Object System.Threading.CancellationTokenSource
        $cts.CancelAfter(20000)
        $ws.ConnectAsync([Uri]$WsUrl, $cts.Token).Wait(15000) | Out-Null
        if ($ws.State -ne 'Open') { return $null }

        $payload = @{ id = 1; method = $Method }
        if ($Params) { $payload.params = $Params }
        $bytes = [Text.Encoding]::UTF8.GetBytes(($payload | ConvertTo-Json -Compress -Depth 6))
        $seg = New-Object System.ArraySegment[byte] -ArgumentList @(,$bytes)
        $ws.SendAsync($seg, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, $cts.Token).Wait(15000) | Out-Null

        $buf = New-Object byte[] 2097152
        while ($true) {
            $segIn = New-Object System.ArraySegment[byte] -ArgumentList @(,$buf)
            $sb = New-Object System.Text.StringBuilder
            while ($true) {
                $res = $ws.ReceiveAsync($segIn, $cts.Token)
                if (-not $res.Wait(20000)) { return $null }
                [void]$sb.Append([Text.Encoding]::UTF8.GetString($buf, 0, $res.Result.Count))
                if ($res.Result.EndOfMessage) { break }
            }
            $line = $sb.ToString()
            if (-not $line) { continue }
            $obj = $null
            try { $obj = $line | ConvertFrom-Json } catch { continue }
            if ($obj -and $obj.PSObject.Properties['id'] -and $obj.id -eq 1) { return $obj }
        }
    }
    catch { return $null }
    finally { try { if ($ws) { $ws.Dispose() } } catch { } }
}

function Get-CdpEval {
    param([string]$WsUrl, [string]$Expression)
    $r = Invoke-Cdp -WsUrl $WsUrl -Method 'Runtime.evaluate' -Params @{
        expression = $Expression; returnByValue = $true; awaitPromise = $false
    }
    if ($r -and $r.result -and $r.result.result) { return $r.result.result.value }
    return $null
}

function Get-SiteCookies {
    param([string]$WsUrl, [string]$Domain)
    if ($Domain) {
        $r = Invoke-Cdp -WsUrl $WsUrl -Method 'Network.getCookies' -Params @{ urls = @("https://$Domain") }
    }
    else {
        # 不分域, 把所有 cookie 都拿回来 —— 登录态可能开在 passport 等其它子域上
        $r = Invoke-Cdp -WsUrl $WsUrl -Method 'Network.getAllCookies'
    }
    if ($r -and $r.result -and $r.result.cookies) { return @($r.result.cookies) }
    return @()
}

# ------------------------------------------------------------------ 候选凭据
function Get-CredentialCandidates {
    param([string]$WsUrl)
    $cands = New-Object System.Collections.ArrayList

    # 1) localStorage 里的 token / auth 类键
    $ls = Get-CdpEval -WsUrl $WsUrl -Expression @'
(function(){
  try {
    var out = {};
    for (var i = 0; i < localStorage.length; i++) {
      var k = localStorage.key(i);
      var v = localStorage.getItem(k);
      if (v && v.length > 20) out[k] = String(v).replace(/^"|"$/g, '');
    }
    return JSON.stringify(out);
  } catch (e) { return '{}'; }
})()
'@
    if ($ls -and $ls -ne '{}') {
        try {
            foreach ($p in ($ls | ConvertFrom-Json).PSObject.Properties) {
                if ($p.Name -match 'token|Token|auth|Authorization|jwt|JWT') {
                    [void]$cands.Add([pscustomobject]@{ kind='ls'; name=$p.Name; value=[string]$p.Value })
                }
            }
        } catch { }
    }

    # 2) cookie: 只取 123pan 相关域 (getAllCookies 的结果按域过滤),
    #    绝不把 profile 里其它站点的 cookie 发给 123 云盘 (SEC-2)
    $panCookies = @(Get-SiteCookies -WsUrl $WsUrl -Domain '123pan.com')
    $panAll = @((Get-SiteCookies -WsUrl $WsUrl -Domain $null) |
                Where-Object { "$($_.domain)" -match '123pan' })

    foreach ($set in @(
        @{ tag='pan'; list=$panCookies }
        @{ tag='panAll'; list=$panAll }
    )) {
        if ($set.list.Count -eq 0) { continue }
        [void]$cands.Add([pscustomobject]@{
            kind='cookie'; name=$set.tag
            value=(($set.list | ForEach-Object { "$($_.name)=$($_.value)" }) -join '; ')
        })
    }

    foreach ($c in (@($panCookies) + @($panAll))) {
        if ($c.name -match 'token|Token|auth' -and "$($c.value)".Length -gt 20) {
            [void]$cands.Add([pscustomobject]@{ kind='cookie'; name=$c.name; value="$($c.value)" })
        }
    }

    # 3) 页面里所有 40 字符以上的长串, 兜底当 token 试一遍
    $lsAll = Get-CdpEval -WsUrl $WsUrl -Expression @'
(function(){
  try {
    var out = [];
    for (var i = 0; i < localStorage.length; i++) {
      var k = localStorage.key(i); var v = localStorage.getItem(k);
      if (v && String(v).length > 40) out.push(k + '\u0001' + String(v));
    }
    return out.join('\u0002');
  } catch (e) { return ''; }
})()
'@
    if ($lsAll) {
        foreach ($pair in ($lsAll -split [char]2)) {
            $kv = $pair -split [char]1
            if ($kv.Count -eq 2 -and $kv[1].Length -gt 40 -and $kv[0] -match 'token|Token|auth|Auth|jwt|JWT') {
                [void]$cands.Add([pscustomobject]@{ kind='ls'; name=$kv[0]; value=$kv[1] })
            }
        }
    }
    return @($cands)
}

function Test-Candidate {
    param($Cand, [string]$ShareUrl, [string]$LibDir)
    $cookie = $null; $token = $null
    # 'pan' / 'panAll' 的 value 本身就是整条 "k=v; k=v" cookie 串, 直接用
    if ($Cand.kind -eq 'cookie' -and $Cand.name -in @('pan','panAll')) { $cookie = $Cand.value }
    elseif ($Cand.kind -eq 'cookie') { $cookie = "$($Cand.name)=$($Cand.value)" }
    else { $token = $Cand.value }
    try { return (Test-Pan123Credential -ShareUrl $ShareUrl -Cookie $cookie -Token $token -LibDir $LibDir) }
    catch { return [pscustomobject]@{ hasLogin=$false; code=$null; message=$_.Exception.Message; probeFile=''; shareCount=0 } }
}

# ------------------------------------------------------------------ 主流程
. (Join-Path $LibDir '网盘目录.ps1')

$exe = Find-Chromium
if (-not $exe) {
    Write-Result ([ordered]@{
        saved=$false; hasLogin=$false; browser=$null
        message='没找到 Edge / Chrome（Chromium 内核浏览器）。装了任一之后再点一次。'
    })
    return
}
Write-Log ("浏览器: {0}" -f $exe)

if (-not (Test-Path $ProfileDir)) { New-Item -ItemType Directory -Force -Path $ProfileDir | Out-Null }
$lock = Join-Path $ProfileDir 'SingletonLock'
if (Test-Path $lock) {
    # SingletonLock 指向 "主机名-PID": 只有锁里记录的进程已不在了才删, 否则强删会损坏 profile
    $lockAlive = $false
    try {
        $target = [string](Get-Item $lock -Force -ErrorAction SilentlyContinue).Target
        if ($target -match '-(\d+)\s*$') {
            $lockPid = [int]$Matches[1]
            if ($lockPid -gt 0 -and $lockPid -ne $PID) { $lockAlive = [bool](Get-Process -Id $lockPid -ErrorAction SilentlyContinue) }
        }
    } catch { }
    if ($lockAlive) { Write-Log '浏览器配置目录正被其它进程使用, 不删除锁文件 (可能无法启动调试端口)' 'WARN' }
    else { Remove-Item $lock -Force -ErrorAction SilentlyContinue }
}

$bargs = @(
    "--app=$LoginUrl",
    "--user-data-dir=$ProfileDir",
    "--remote-debugging-port=$DebugPort",
    "--remote-allow-origins=http://127.0.0.1:$DebugPort",
    '--no-first-run', '--no-default-browser-check'
)
Write-Log '正在打开登录窗口 ...' 'HEAD'
Write-Log '请在窗口里登录 123 云盘（自己的账号）。登录好之后关掉窗口即可，脚本会把登录态取回来。'
$proc = Start-Process -FilePath $exe -ArgumentList $bargs -PassThru

$deadline = (Get-Date).AddSeconds($TimeoutSec)
$found = $null
$lastMsg = '还没登录或没读到 token'
$sawDebugPort = $false
$script:lastSig = ''
$script:cooldownUntil = (Get-Date).AddSeconds(-1)
try {
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
        $targets = Get-CdpTargets -Port $DebugPort
        if (-not $targets) {
            if ($proc.HasExited) { $lastMsg = '登录窗口已关闭，没读到登录态'; break }
            continue
        }
        $sawDebugPort = $true
        $page = $targets | Where-Object { $_.type -eq 'page' -and $_.webSocketDebuggerUrl } | Select-Object -First 1
        if (-not $page) { continue }

        $cands = @(Get-CredentialCandidates -WsUrl $page.webSocketDebuggerUrl)
        if ($cands.Count -eq 0) { continue }

        # 候选变了才重试, 免得每 3 秒都去撞一次接口
        $sig = (($cands | ForEach-Object { "$($_.kind)/$($_.name)" }) -join ',')
        if ($sig -eq $script:lastSig) { continue }
        if ((Get-Date) -lt $script:cooldownUntil) { continue }
        $script:lastSig = $sig
        Write-Log ("读到 {0} 个候选凭据: {1}" -f $cands.Count, $sig)

        $hit429 = $false
        foreach ($c in $cands) {
            $t = Test-Candidate -Cand $c -ShareUrl $ShareUrl -LibDir $LibDir
            if ($t.hasLogin) { $found = [pscustomobject]@{ cand = $c; test = $t }; break }
            $lastMsg = "试了 $($c.kind)/$($c.name)：$($t.message)"
            # 429 = 列目录被限频: 这个会话还没登录, 别再连着问, 等一会儿
            if ("$($t.message)" -match '429') { $hit429 = $true; break }
        }
        if ($hit429) {
            $script:cooldownUntil = (Get-Date).AddSeconds(60)
            $script:lastSig = ''
            Write-Log '分享接口限频 (429), 60 秒后再试。' 'WARN'
        }
        if ($found) { break }
        if (-not $proc.HasExited -and -not $targets) { break }
    }
}
finally {
    try { if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } } catch { }
}

if ($found) {
    $c = $found.cand
    $n = Save-Pan123Credential -Cookie $c.value -Path $CredFile
    Write-Log ("登录态可用, 已保存 ({0} 字符, 来源 {1}/{2})" -f $n, $c.kind, $c.name) 'OK'
    Write-Result ([ordered]@{
        saved=$true; hasLogin=$true; browser=[IO.Path]::GetFileName($exe)
        source=("{0}/{1}" -f $c.kind, $c.name)
        message=("登录成功，已保存登录态（来源 {0}/{1}）" -f $c.kind, $c.name)
        shareCount=$found.test.shareCount; probeFile=$found.test.probeFile
    })
}
else {
    if (-not $sawDebugPort) { $lastMsg = '调试端口没起来，浏览器可能不支持或启动失败' }
    Write-Log $lastMsg 'WARN'
    $hasOld = (Get-Pan123Credential -Path $CredFile).exists
    Write-Result ([ordered]@{
        saved=$false; hasLogin=$false; browser=[IO.Path]::GetFileName($exe)
        debugPort=$sawDebugPort
        message=("没能自动取到可用的登录态：{0}" -f $lastMsg)
        oldCredentialKept=$hasOld
    })
}
