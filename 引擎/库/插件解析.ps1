# =====================================================================
#  插件解析.ps1  --  插件文件的读取 / 校验 / 归一化
#
#  插件.ps1 里所有"格式"的判定都收在这里，引擎与工作区共用同一套规则，
#  免得写入的格式和读取的格式对不上。
#
#  公开:
#    Get-EnSlug             显示名 → 纯英文目录名
#    Read-PluginFile        读一个插件 json
#    Test-Plugin            校验, 返回问题列表 (空数组 = 通过)
#    Get-InstalledPlugins   读 插件\*.json 全部
#    Convert-PluginApp      归一化一个 app 条目 (补默认值、算 dir/sub)
# =====================================================================

$script:BuiltinCategoryMap = [ordered]@{
    '安全清理' = 'security_clear'
    '基础工具' = 'BasicTools'
    '聊天社交' = 'ChatSocial'
    '网盘'     = 'CloudDrive'
    '下载'     = 'Download'
    '浏览器'   = 'Browser'
    '网络加速' = 'NetAccel'
    '3D建模'   = 'Modeling3D'
    '外设软件' = 'Peripheral'
    '组件'     = 'Runtime'
    '运行库'   = 'Runtime'
}

function Get-BuiltinCategoryMap { return $script:BuiltinCategoryMap }

function Get-EnSlug {
    <# 显示名 → 纯英文名（目录里不允许中文） #>
    param([string] $Text, [string] $Id)
    $s = ($Text -replace '[^A-Za-z0-9\.\+\-_]', '_') -replace '_+', '_'
    $s = $s.Trim('_', '.', '-')
    $generic = @('Windows', 'Install', 'Setup', 'Pro', 'Portable', 'Green', 'Tool', 'Tools', 'Edition')
    $needId = ($s.Length -lt 3) -or ($generic -contains $s) -or ($s -match '^[Vv]?[\d\.]+$')
    if ($needId -and $Id) { $s = ($s + '_' + $Id).Trim('_') }
    if ($s.Length -lt 2) { $s = $Id }
    return $s
}

function Resolve-CategoryDir {
    <# 中文分类名 → 英文目录名。内置表优先, 其次自定义分类, 最后用 slug #>
    param([string] $Category, [string] $ExplicitDir, $Plugin)

    if ($ExplicitDir) { return (Get-EnSlug -Text $ExplicitDir -Id 'Cat') }

    if ($Category) {
        if ($script:BuiltinCategoryMap.Contains($Category)) { return $script:BuiltinCategoryMap[$Category] }
        if ($Plugin) {
            $hit = $Plugin.categories | Where-Object { $_.name -eq $Category } | Select-Object -First 1
            if ($hit -and $hit.dir) { return (Get-EnSlug -Text $hit.dir -Id 'Cat') }
        }
    }
    if ($Category) { return (Get-EnSlug -Text $Category -Id 'Cat') }
    return 'MyApps'
}

function Convert-PluginApp {
    <# 把一个原始 app 条目补全成引擎认识的形状 #>
    param($Raw, $Plugin)

    $name = [string]$Raw.name
    if (-not $name) { $name = [string]$Raw.id }
    $id = [string]$Raw.id
    if (-not $id) { $id = Get-EnSlug -Text $name -Id 'app' }
    # id 全局唯一: 插件内前缀, 避免和别人的插件撞
    if ($Plugin -and $Plugin.id -and $id -notlike "$($Plugin.id).*") { $id = "$($Plugin.id).$id" }

    $category = [string]$Raw.category
    if (-not $category -and $Plugin -and $Plugin.categories.Count) { $category = $Plugin.categories[0].name }
    if (-not $category) { $category = '我的应用' }

    $dir = Resolve-CategoryDir -Category $category -ExplicitDir ([string]$Raw.dir) -Plugin $Plugin
    $sub = [string]$Raw.sub
    if ($sub) {
        # sub 必须过 slug: 外部插件 JSON 里写 "..\..\Windows" 这类值时, 拼接目标路径会跳出根目录
        $sub = Get-EnSlug -Text $sub -Id ($id -replace '^.*\.','')
    }
    else { $sub = Get-EnSlug -Text $name -Id ($id -replace '^.*\.','') }

    $tier = [string]$Raw.tier
    if ($tier -notin @('核心', '推荐', '按需')) { $tier = '推荐' }

    $mode = [string]$Raw.mode
    if (-not $mode) { $mode = '便携' }
    if ($mode -eq 'none') { $mode = '手动' }

    $source = [string]$Raw.source
    $wingetId = [string]$Raw.wingetId
    $shareUrl = [string]$Raw.shareUrl
    $url = [string]$Raw.url
    $file = [string]$Raw.file
    if (-not $source) {
        if ($shareUrl) { $source = 'pan123' }
        elseif ($wingetId) { $source = 'winget' }
        elseif ($url) { $source = 'official' }
        elseif ($file) { $source = 'local' }
        else { $source = 'manual' }
    }

    $dm = 'none'; $dp = ''
    if ($Raw.detect) {
        $dm = [string]$Raw.detect.method
        $dp = [string]$Raw.detect.param
    }
    if ($dm -and $dm -ne 'none' -and -not $dp) { $dm = 'none' }

    return [pscustomobject]@{
        id = $id
        name = $name
        category = $category
        dir = $dir
        sub = $sub
        tier = $tier
        source = $source
        mode = $mode
        wingetId = $wingetId
        shareUrl = $shareUrl
        url = $url
        file = $file
        silent = [string]$Raw.silent
        patch = [string]$Raw.patch
        detectMethod = $dm
        detectParam = $dp
        note = [string]$Raw.note
        pluginId = $(if ($Plugin) { $Plugin.id } else { '' })
        pluginName = $(if ($Plugin) { $Plugin.name } else { '' })
    }
}

function Read-PluginFile {
    <# 读一个插件 json, 补默认值; 解析失败时抛错 #>
    param([Parameter(Mandatory)][string] $Path)
    if (-not (Test-Path $Path)) { throw "插件文件不存在: $Path" }
    $raw = [IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8)
    if (-not $raw.Trim()) { throw "插件文件是空的: $Path" }

    $obj = $null
    try { $obj = $raw | ConvertFrom-Json }
    catch { throw "不是合法 JSON: $Path  ($($_.Exception.Message))" }

    $p = [pscustomobject]@{
        file = $Path
        schema = $(if ($obj.schema) { [int]$obj.schema } else { 1 })
        id = [string]$obj.id
        name = [string]$obj.name
        author = [string]$obj.author
        version = [string]$obj.version
        description = [string]$obj.description
        categories = @()
        apps = @()
        invalid = $false
        errors = @()
    }
    if (-not $p.id) { $p.id = Get-EnSlug -Text (Split-Path $Path -LeafBase) -Id 'plugin' }
    if (-not $p.name) { $p.name = $p.id }

    $cats = New-Object System.Collections.ArrayList
    foreach ($c in @($obj.categories)) {
        if (-not $c) { continue }
        $cn = [string]$c.name; if (-not $cn) { continue }
        $cd = [string]$c.dir
        if (-not $cd) { $cd = Get-EnSlug -Text $cn -Id 'Cat' }
        [void]$cats.Add([pscustomobject]@{ name = $cn; dir = (Get-EnSlug -Text $cd -Id 'Cat') })
    }
    $p.categories = @($cats)

    $apps = New-Object System.Collections.ArrayList
    foreach ($a in @($obj.apps)) {
        if (-not $a) { continue }
        if (-not ($a.name -or $a.id)) { continue }
        [void]$apps.Add((Convert-PluginApp -Raw $a -Plugin $p))
    }
    $p.apps = @($apps)

    $errs = @(Test-Plugin -Plugin $p)
    $p.errors = $errs
    $p.invalid = ($errs.Count -gt 0)
    return $p
}

function Test-Plugin {
    <# 校验插件, 返回问题字符串数组 #>
    param([Parameter(Mandatory)] $Plugin)
    $errs = New-Object System.Collections.ArrayList

    if ($Plugin.schema -and $Plugin.schema -gt 1) {
        [void]$errs.Add("schema=$($Plugin.schema) 比本程序支持的新 (最高 1)")
    }
    if (-not $Plugin.name) { [void]$errs.Add('缺 name') }
    if (-not $Plugin.apps.Count) { [void]$errs.Add('一个应用都没有') }

    $seen = @{}
    foreach ($a in $Plugin.apps) {
        $tag = "$($a.name)"
        if ($seen.ContainsKey($a.id)) { [void]$errs.Add("应用 id 重复: $($a.id)") }
        $seen[$a.id] = $true

        switch ($a.source) {
            'pan123' {
                if (-not $a.shareUrl) { [void]$errs.Add("$tag 是 pan123 来源但没写 shareUrl") }
                if (-not $a.file)     { [void]$errs.Add("$tag 是 pan123 来源但没写 file (要下载的文件名)") }
            }
            'official' {
                if (-not $a.url) { [void]$errs.Add("$tag 是 official 来源但没写 url") }
            }
            'winget' {
                if (-not $a.wingetId) { [void]$errs.Add("$tag 是 winget 来源但没写 wingetId") }
            }
            'local' {
                if (-not $a.file) { [void]$errs.Add("$tag 是 local 来源但没写 file") }
            }
            'manual' { }
            default { [void]$errs.Add("$tag 的 source 不认识: $($a.source)") }
        }

        if ($a.dir -match '[^\x00-\x7F]') { [void]$errs.Add("$tag 的英文目录里出现了非 ASCII 字符: $($a.dir)") }
        if ($a.sub -match '[^\x00-\x7F]') { [void]$errs.Add("$tag 的子目录里出现了非 ASCII 字符: $($a.sub)") }
    }
    return $errs
}

function Get-InstalledPlugins {
    <# 读插件目录下所有 *.json。坏文件不抛错, 标成 invalid 交给上层展示 #>
    param([string] $PluginDir = 'E:\安装系统\插件')
    $out = New-Object System.Collections.ArrayList
    if (-not (Test-Path $PluginDir)) { return @() }
    foreach ($f in @(Get-ChildItem $PluginDir -Filter *.json -File -ErrorAction SilentlyContinue | Sort-Object Name)) {
        try { [void]$out.Add((Read-PluginFile -Path $f.FullName)) }
        catch {
            [void]$out.Add([pscustomobject]@{
                file = $f.FullName; id = $f.BaseName; name = $f.BaseName; author = ''
                version = ''; description = ''; categories = @(); apps = @()
                invalid = $true; errors = @($_.Exception.Message); schema = 0
            })
        }
    }
    return @($out)
}
