# =====================================================================
#  网盘目录.ps1  --  用"自己的 123 云盘账号"把云盘里的安装包批量拉下来
#
#  为什么需要登录: /api/share/download/info 对未登录会话一律返回
#     {"code":5112,"message":"您需要注册登录或付费后下载"}
#  列目录不需要登录, 取下载直链必须有登录态。所以这个文件做三件事:
#     1) 让用户把自己浏览器里的登录态填进去 (Save-Pan123Credential)
#     2) 自检这份登录态到底行不行 (Test-Pan123Credential)
#     3) 按软件目录把云盘里的文件批量下载到缓存 (Get-CatalogDownloadPlan / Save-CatalogDownloads)
#
#  凭据从哪来 (浏览器里, 二选一):
#     A. 已登录 123pan.com 后按 F12 → Network → 任一 /api/ 请求
#        → 请求头里的 Cookie 整条复制 (含 authorToken=... / token=...)
#     B. F12 → Application → Local Storage → 找 PassportToken / token 的值
#  存到 E:\安装系统\123云盘cookie.txt  (token=xxx 或整条 cookie)
#  本文件只做本地读取与调用, 不做任何账号密码代填。
# =====================================================================

$script:Pan123CredPath = 'E:\安装系统\123云盘cookie.txt'

function Get-Pan123CredPath {
    return $script:Pan123CredPath
}

function Save-Pan123Credential {
    <#
      把用户提供的 cookie / token 写进凭据文件。
      传 -Cookie 整条 cookie 串, 或 -Token 单个 PassportToken。
      同一个参数里既有 token= 又有其它 cookie 时按整条 cookie 存。
    #>
    param(
        [string] $Cookie,
        [string] $Token,
        [string] $Path
    )
    if (-not $Path) { $Path = $script:Pan123CredPath }
    $text = ''
    if ($Cookie -and $Cookie.Trim()) { $text = $Cookie.Trim() }
    elseif ($Token -and $Token.Trim()) {
        $t = $Token.Trim()
        if ($t -match '^[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+$') { $text = "token=$t" }
        else { $text = $t }
    }
    if (-not $text) { throw 'cookie / token 都是空的, 没东西可存' }
    [IO.File]::WriteAllText($Path, $text, (New-Object Text.UTF8Encoding $false))
    # 凭据文件断开继承, 只留 当前用户/SYSTEM/Administrators 可读 (SEC-1)
    try {
        $acl = Get-Acl -LiteralPath $Path
        $acl.SetAccessRuleProtection($true, $false)
        foreach ($name in @($env:USERNAME, 'SYSTEM', 'Administrators')) {
            $acl.SetAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($name, 'FullControl', 'Allow')))
        }
        Set-Acl -LiteralPath $Path -AclObject $acl
    } catch { }
    return $text.Length
}

function Get-Pan123Credential {
    <#
      读回凭据, 返回 cookie 串与 token (两者可能只有一个)。
      整条 cookie 里的 authorToken / token 会被单独抽出来当 PassportToken 用。
    #>
    param([string] $Path)
    if (-not $Path) { $Path = $script:Pan123CredPath }
    $r = [pscustomobject]@{ cookie = $null; token = $null; raw = ''; exists = $false; path = $Path }
    if (-not (Test-Path $Path)) { return $r }

    $raw = (Get-Content -LiteralPath $Path -Raw -Encoding UTF8).Trim()
    if (-not $raw) { return $r }
    $r.raw = $raw; $r.exists = $true

    # token= 前缀行 / 纯 JWT 行
    $lines = @($raw -split "`r?`n" | Where-Object { $_.Trim() })
    foreach ($ln in $lines) {
        if ($ln -match '^\s*token\s*=\s*(.+)$') { $r.token = $Matches[1].Trim(); continue }
        if ($ln -match '^[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+$') { $r.token = $ln.Trim() }
    }
    $cookie = ($lines | Where-Object { $_ -notmatch '^\s*token\s*=' -and $_ -notmatch '^[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+$' }) -join '; '
    if ($cookie.Trim()) { $r.cookie = $cookie.Trim() }

    # 整条 cookie 里抽 PassportToken / authorToken / JWT 形态的 token=
    foreach ($pat in @('PassportToken=([^;\s]+)', 'authorToken=([^;\s]+)', '(?:^|;\s*)token=([^;\s]+)')) {
        if (-not $r.token -and $r.cookie -match $pat) { $r.token = $Matches[1].Trim() }
    }
    return $r
}

function Test-Pan123Credential {
    <#
      真的去取一次直链来验证登录态。取到 = 登录态可用。
      返回 hasLogin(bool) / code / message / raw(前 160 字)。
    #>
    param(
        [Parameter(Mandatory)][string] $ShareUrl,
        [string] $Cookie,
        [string] $Token,
        [string] $LibDir = 'E:\安装系统\引擎\库'
    )
    . (Join-Path $LibDir '123云盘.ps1')

    $out = [pscustomobject]@{ hasLogin=$false; code=$null; message=''; shareCount=0; probeFile=''; needCookie=$false }

    $share = Set-Pan123Share -ShareUrl $ShareUrl -Cookie $Cookie -Token $Token
    $lvl = @(Get-Pan123List -ParentFileId 0)
    $out.shareCount = $lvl.Count

    # 找一个最深的第一层子目录, 再进去取一个文件当探针
    $probe = $null; $queue = @($lvl | Where-Object { $_.Type -eq 1 } | Select-Object -First 3)
    foreach ($d in $queue) {
        $sub = @(Get-Pan123List -ParentFileId $d.FileId)
        $probe = $sub | Where-Object { $_.Type -ne 1 } | Select-Object -First 1
        if (-not $probe) {
            $d2 = $sub | Where-Object { $_.Type -eq 1 } | Select-Object -First 1
            if ($d2) { $probe = @(Get-Pan123List -ParentFileId $d2.FileId) | Where-Object { $_.Type -ne 1 } | Select-Object -First 1 }
        }
        if ($probe) { break }
    }
    if (-not $probe) { $probe = @($lvl | Where-Object { $_.Type -ne 1 } | Select-Object -First 1) }
    if (-not $probe) { $out.message = '分享里找不到可用来验证的文件'; return $out }
    $out.probeFile = $probe.FileName

    try {
        [void](Get-Pan123DownloadUrl -File $probe)
        $out.hasLogin = $true; $out.code = 0; $out.message = "登录态可用, 成功取到直链 ($($probe.FileName))"
    }
    catch {
        $out.message = $_.Exception.Message
        if ($out.message -match '5112') { $out.needCookie = $true }
        if ($out.message -match 'code=(\-?\d+)') { $out.code = [int]$Matches[1] }
    }
    return $out
}

function Initialize-Pan123Session {
    <# 用存好的凭据建立会话, 供后面的列目录/下载复用 #>
    param(
        [Parameter(Mandatory)][string] $ShareUrl,
        [string] $LibDir = 'E:\安装系统\引擎\库',
        [string] $CookiePath
    )
    . (Join-Path $LibDir '123云盘.ps1')
    $cred = Get-Pan123Credential -Path $CookiePath
    $s = Set-Pan123Share -ShareUrl $ShareUrl -Cookie $cred.cookie -Token $cred.token
    $s | Add-Member -NotePropertyName Cred -NotePropertyValue $cred -Force
    return $s
}

function Get-RemoteTree {
    <# 把整个分享的所有文件拉成一张 {文件名 → 文件对象} 表 (递归, 最多 6 层) #>
    param($ShareUrl)
    $all = @(Get-Pan123Tree)
    $script:LastRemoteFiles = $all     # 供目录型条目匹配用
    $map = @{}
    foreach ($f in $all) {
        $key = ($f.FileName -replace '[^\p{L}\p{N}]','').ToLower()
        if ($key -and -not $map.ContainsKey($key)) { $map[$key] = $f }
    }
    return $map
}

function Get-PanNorm([string]$s) {
    if (-not $s) { return '' }
    return ($s -replace '[^\p{L}\p{N}]','').ToLower()
}

function Match-RemoteFolder {
    <#
      "云盘目录"型条目: 归档字段是目录名 (如 弹丸论破 1 / 16_游戏软件\弹丸1),
      在文件树里按路径包含关系找出该目录下的全部文件。
    #>
    param([string] $Archive, $Files)
    if (-not $Archive -or -not $Files) { return @() }
    $key = Get-PanNorm $Archive
    if (-not $key -or $key.Length -lt 2) { return @() }
    return @($Files | Where-Object {
        $pn = Get-PanNorm ([string]$_.路径)
        $pn -and $pn.Contains($key)
    })
}

function Get-CommonPathPrefix {
    <# 命中的文件们的公共路径前缀 (按 \ 分段), 用于下载时保留相对结构 #>
    param($Files)
    if (-not $Files -or @($Files).Count -eq 0) { return '' }
    $segs = @(@($Files) | ForEach-Object { ,([string]$_.路径 -split '\\') })
    $min = ($segs | ForEach-Object { $_.Count } | Measure-Object -Minimum).Minimum
    $prefix = @()
    for ($i = 0; $i -lt ($min - 1); $i++) {     # 最后一段是文件名, 不进前缀
        $seg = $segs[0][$i]
        $same = $true
        foreach ($s in $segs) { if ($s[$i] -cne $seg) { $same = $false; break } }
        if (-not $same) { break }
        $prefix += $seg
    }
    return ($prefix -join '\')
}

function Match-RemoteFile {
    <# 把清单里的"包文件名"对上云盘里的文件: 完全相同 → 归一化相同 → 互含 #>
    param([string] $Archive, $Map)
    if (-not $Archive -or -not $Map) { return $null }
    if ($Map.ContainsKey(($Archive -replace '[^\p{L}\p{N}]','').ToLower())) {
        return $Map[($Archive -replace '[^\p{L}\p{N}]','').ToLower()]
    }
    $key = ($Archive -replace '[^\p{L}\p{N}]','').ToLower()
    if ($key.Length -lt 4) { return $null }
    foreach ($k in $Map.Keys) {
        if ($k.Length -ge 4 -and ($k -like "*$key*" -or $key -like "*$k*")) { return $Map[$k] }
    }
    return $null
}

function Get-CatalogDownloadPlan {
    <#
      按软件目录算出"要从云盘下载哪些文件"。
      返回项: id name category archive file(云盘对象) cached(bool) outFile
    #>
    param(
        $Items,
        $Map,
        [string] $OutDir
    )
    $plan = New-Object System.Collections.ArrayList
    foreach ($it in $Items) {
        if (-not $it.archive) { continue }
        $f = Match-RemoteFile -Archive $it.archive -Map $Map
        if (-not $f) { continue }
        $outFile = Join-Path $OutDir $f.FileName
        $cached = $false
        if (Test-Path $outFile) { $cached = ((Get-Item $outFile).Length -eq $f.Size) }
        [void]$plan.Add([pscustomobject]@{
            id=$it.id; name=$it.name; category=$it.dir; archive=$it.archive
            file=$f; cached=$cached; outFile=$outFile
        })
    }
    return $plan
}

function Save-CatalogDownloads {
    <# 下载计划里的文件; 已有的按大小判断跳过 #>
    param(
        $Plan,
        [string] $OutDir,
        [scriptblock] $Log
    )
    if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force -Path $OutDir | Out-Null }
    $done = New-Object System.Collections.ArrayList
    $n = 0
    foreach ($p in $Plan) {
        $n++
        if ($p.cached) {
            & $Log ("  [{0}/{1}] 已有 {2}" -f $n, $Plan.Count, $p.file.FileName)
            [void]$done.Add($p); continue
        }
        & $Log ("  [{0}/{1}] 下载 {2}  ({3:N1} MB)" -f $n, $Plan.Count, $p.file.FileName, ($p.file.Size/1MB))
        try {
            $r = Save-Pan123File -File $p.file -OutDir $OutDir -SkipHash
            & $Log ("      完成 → {0}" -f $r.Path)
            [void]$done.Add($p)
        }
        catch { & $Log ("      失败: {0}" -f $_.Exception.Message) }
    }
    return $done
}
