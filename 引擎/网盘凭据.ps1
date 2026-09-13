#Requires -Version 5.1
<#
  网盘凭据.ps1  --  保存 123 云盘登录态并当场验证

  启动器「网盘下载」页用这个:
    把你在浏览器里复制的 cookie / token 存下来, 然后真的去取一次下载直链。
    取到 = 登录态可用; 报 5112 = 没登录或已过期。

  用法:
    网盘凭据.ps1 -CredFileIn <文件> -Out <结果.json>
    网盘凭据.ps1 -Cookie '<整条 cookie>' -Out <结果.json>

  输出 JSON: { saved, hasLogin, code, shareCount, probeFile, message }
#>
[CmdletBinding()]
param(
    [string] $CredFileIn,
    [string] $Cookie,
    [string] $ShareUrl = 'https://1828566527.share.123pan.cn/123pan/2KXljv-OaNUv',
    [string] $LibDir   = 'E:\安装系统\引擎\库',
    [string] $Out
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

. (Join-Path $LibDir '网盘目录.ps1')

$r = [ordered]@{
    saved = $false; hasLogin = $false; code = $null
    shareCount = 0; probeFile = ''; message = ''
}

try {
    $cred = $null
    if ($CredFileIn) {
        if (-not (Test-Path $CredFileIn)) { throw "凭据输入文件不存在: $CredFileIn" }
        $cred = (Get-Content -LiteralPath $CredFileIn -Raw -Encoding UTF8)
    }
    elseif ($Cookie) { $cred = $Cookie }
    else { throw '没有给凭据 (-CredFileIn 或 -Cookie)' }

    if (-not $cred.Trim()) { throw '凭据是空的' }

    $n = Save-Pan123Credential -Cookie $cred
    $r.saved = $true
    $r.message = "已保存并开始验证 ($n 字符)"

    $c = Get-Pan123Credential
    $t = Test-Pan123Credential -ShareUrl $ShareUrl -Cookie $c.cookie -Token $c.token -LibDir $LibDir
    $r.hasLogin   = $t.hasLogin
    $r.code       = $t.code
    $r.shareCount = $t.shareCount
    $r.probeFile  = $t.probeFile
    $r.message    = $t.message
}
catch {
    $r.message = $_.Exception.Message
}

$json = $r | ConvertTo-Json -Compress
if ($Out) { [IO.File]::WriteAllText($Out, $json, (New-Object Text.UTF8Encoding $false)) }
Write-Host $json
