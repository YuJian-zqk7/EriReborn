# =====================================================================
#  123云盘.ps1  --  123pan 分享下载库 (dot-source this)
#
#  分享链接形如:
#     https://<uid>.share.123pan.cn/123pan/<shareKey>
#  或  https://www.123pan.com/s/<shareKey>
#
#  已实测的接口:
#     列目录  GET  /api/share/get?shareKey=..&ParentFileId=..&Limit=..
#                  &Next=0&Page=1&OrderBy=file_name&OrderDirection=asc
#     取直链  POST /api/share/download/info
#                  body: shareKey, FileId, Password, driveId, S3keyFlag,
#                        type, fileName, etag, fileType, Size
#
#  坑: 下载接口对未登录会话返回
#        {"code":5112,"message":"您需要注册登录或付费后下载"}
#      列目录和取直链都要带浏览器 Referer, 否则 404。
#      下载需要登录态 cookie, 用 -Cookie 传入。
# =====================================================================

$script:Pan123Base    = $null
$script:Pan123Key     = $null
$script:Pan123Cookie  = $null
$script:Pan123Token   = $null

function Set-Pan123Share {
    <#
      解析分享链接并设定会话。
        -ShareUrl  https://1828566527.share.123pan.cn/123pan/2KXljv-YXcuv
        -Cookie    浏览器里复制的整条 cookie 串 (下载时需要)
    #>
    param(
        [Parameter(Mandatory)][string] $ShareUrl,
        [string] $Cookie,
        [string] $Token
    )
    $u = [uri]$ShareUrl
    $seg = $u.AbsolutePath.Trim('/').Split('/')
    # /123pan/<key>  |  /s/<key>  |  /ps/<key>
    $key = if ($seg.Count -ge 2) { $seg[-1] } else { $seg[0] }
    $key = $key -replace '\.html$', ''

    $script:Pan123Base   = "$($u.Scheme)://$($u.Host)"
    $script:Pan123Key    = $key
    $script:Pan123Cookie = $Cookie
    $script:Pan123Token  = $Token

    return [pscustomobject]@{ Base = $script:Pan123Base; ShareKey = $key; HasCookie = [bool]$Cookie; HasToken = [bool]$Token }
}

function _Pan123Headers {
    $h = @{
        'User-Agent' = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36'
        'Referer'    = "$($script:Pan123Base)/123pan/$($script:Pan123Key)"
        'Origin'     = $script:Pan123Base
        'Accept'     = 'application/json, text/plain, */*'
        'Accept-Language' = 'zh-CN,zh;q=0.9'
    }
    if ($script:Pan123Cookie) { $h['Cookie'] = $script:Pan123Cookie }
    if ($script:Pan123Token)  { $h['Authorization'] = "Bearer $script:Pan123Token" }
    return $h
}

function _Pan123Json {
    param([string]$Url, [string]$Method = 'GET', $Body = $null)
    $iwArgs = @{ Uri = $Url; Headers = (_Pan123Headers); UseBasicParsing = $true
               TimeoutSec = 60; Method = $Method }
    if ($Body) {
        $iwArgs.Body        = ($Body | ConvertTo-Json -Compress)
        $iwArgs.ContentType = 'application/json'
    }
    $r = Invoke-WebRequest @iwArgs
    return [Text.Encoding]::UTF8.GetString($r.RawContentStream.ToArray()) | ConvertFrom-Json
}

# 列一层目录
function Get-Pan123List {
    param([long] $ParentFileId = 0)
    $q = "shareKey=$($script:Pan123Key)&ParentFileId=$ParentFileId&Limit=200&Next=0&Page=1" +
         "&OrderBy=file_name&OrderDirection=asc"
    $j = _Pan123Json "$($script:Pan123Base)/api/share/get?$q"
    if ($j.code -ne 0) { throw "123云盘列目录失败: code=$($j.code) $($j.message)" }
    return $j.data.InfoList
}

# 递归列全部文件 (Type=1 是目录)
function Get-Pan123Tree {
    param([long] $ParentFileId = 0, [int] $Depth = 0, [string] $Prefix = '')
    if ($Depth -gt 5) { return }
    foreach ($f in (Get-Pan123List -ParentFileId $ParentFileId)) {
        if ($f.Type -eq 1) {
            Get-Pan123Tree -ParentFileId $f.FileId -Depth ($Depth + 1) -Prefix "$Prefix$($f.FileName)/"
        }
        else {
            [pscustomobject]@{
                FileId   = $f.FileId
                FileName = $f.FileName
                路径     = "$Prefix$($f.FileName)"
                Size     = $f.Size
                Etag     = $f.Etag
                S3KeyFlag= $f.S3KeyFlag
                Type     = $f.Type
                UpdateAt = $f.UpdateAt
            }
        }
    }
}

# 取下载直链; 未登录会抛 5112
function Get-Pan123DownloadUrl {
    param([Parameter(Mandatory)] $File)
    $body = @{
        shareKey   = $script:Pan123Key
        FileId     = $File.FileId
        Password   = ''
        driveId    = 0
        S3keyFlag  = $File.S3KeyFlag
        type       = 0
        fileName   = $File.FileName
        etag       = $File.Etag
        fileType   = $File.Type
        Size       = $File.Size
    }
    $j = _Pan123Json "$($script:Pan123Base)/api/share/download/info" -Method POST -Body $body
    if ($j.code -eq 5112) {
        throw "123云盘需要登录才能下载 (code 5112)。请用 -Pan123Cookie 传入浏览器 cookie。"
    }
    if ($j.code -ne 0) { throw "取直链失败: code=$($j.code) $($j.message)" }
    return $j.data.DownloadUrl
}

# 下载单个文件到目标目录, 返回落地路径
function Save-Pan123File {
    param(
        [Parameter(Mandatory)] $File,
        [Parameter(Mandatory)][string] $OutDir,
        [switch] $Force,
        [switch] $SkipHash   # 云盘不给期望哈希, 算了也只用于打日志; 不需要时跳过省时间
    )
    if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force -Path $OutDir | Out-Null }
    $dest = Join-Path $OutDir $File.FileName
    if ((Test-Path $dest) -and -not $Force) {
        if ((Get-Item $dest).Length -eq $File.Size) {
            return [pscustomobject]@{ Path=$dest; Skipped=$true; Sha256=$null }
        }
    }
    $url = Get-Pan123DownloadUrl -File $File
    $tmp = "$dest.part"
    $prevProgress = $ProgressPreference
    $ok = $false
    for ($try = 1; $try -le 3 -and -not $ok; $try++) {
        try {
            $ProgressPreference = 'SilentlyContinue'
            Invoke-WebRequest -Uri $url -OutFile $tmp -Headers (_Pan123Headers) -UseBasicParsing -TimeoutSec 1800
            $ok = $true
        }
        catch {
            if ($try -ge 3) { throw "下载失败: $($File.FileName)  ($($_.Exception.Message))" }
            Start-Sleep -Seconds (3 * $try)
        }
        finally { $ProgressPreference = $prevProgress }
    }
    if (-not $ok) {
        Remove-Item $tmp -Force -ErrorAction SilentlyContinue
        throw "下载失败: $($File.FileName)"
    }
    Move-Item $tmp $dest -Force
    $sha = if (-not $SkipHash) { (Get-FileHash $dest -Algorithm SHA256).Hash.ToLower() } else { $null }
    return [pscustomobject]@{ Path=$dest; Skipped=$false; Sha256=$sha }
}

# 从文件读 cookie (一行, 或 key=value;key=value 形式)
function Read-Pan123Cookie {
    param([string] $Path = 'E:\安装系统\123云盘cookie.txt')
    if (-not (Test-Path $Path)) { return $null }
    $t = (Get-Content -LiteralPath $Path -Raw -Encoding UTF8).Trim()
    if (-not $t) { return $null }
    # 文件里可以同时放 cookie 和 token, 用 "token=" 前缀标出
    $cookie = ($t -split "`r?`n" | Where-Object { $_ -and $_ -notmatch '^\s*token\s*=' }) -join '; '
    return $cookie.Trim()
}

function Read-Pan123Token {
    param([string] $Path = 'E:\安装系统\123云盘cookie.txt')
    if (-not (Test-Path $Path)) { return $null }
    foreach ($ln in (Get-Content -LiteralPath $Path -Encoding UTF8)) {
        if ($ln -match '^\s*token\s*=\s*(.+)$') { return $Matches[1].Trim() }
        # 整行像一个 JWT 就直接当 token
        if ($ln -match '^[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+$') { return $ln.Trim() }
    }
    return $null
}
