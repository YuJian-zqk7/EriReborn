namespace EriReborn.Skin;

/// <summary>What kind of thing an editable interface element is (spec 30).</summary>
public enum SkinElementKind
{
    /// <summary>A word the interface draws, which a skin may replace.</summary>
    Text,

    /// <summary>A picture: a character, a slot, an illustration.</summary>
    Image,

    /// <summary>A small picture used as a glyph — a nav entry, a status mark.</summary>
    Icon,

    /// <summary>A surface painted with one of the skin's colours.</summary>
    ColorSurface,

    /// <summary>A control with a label, treated as the word it shows.</summary>
    Button,

    /// <summary>Purely decorative art: a frame, a ribbon, a badge.</summary>
    Decoration,

    /// <summary>A container that only arranges things. It has nothing to edit.</summary>
    Layout,
}

/// <summary>
/// One thing in the real interface a skin may change.
///
/// <para>
/// The id is the stable name of the slot — <c>nav.settings</c>, <c>home.character</c> — and it is
/// what a skin's override is filed under. The user never sees it: the panel shows
/// <see cref="DisplayName"/>. It is declared by the control that draws the element, which is what
/// makes a click on any real element selectable instead of only the few controls an editor was
/// told about.
/// </para>
/// </summary>
public sealed record SkinElementDescriptor(
    string Id,
    SkinElementKind Kind,
    string DisplayName,
    string? TextKey = null,
    string? AssetId = null,
    string? ColorKey = null)
{
    /// <summary>True when this element is a word (a plain label or a button's text).</summary>
    public bool IsText => Kind is SkinElementKind.Text or SkinElementKind.Button;

    /// <summary>True when this element is art that can be replaced with a file.</summary>
    public bool IsArt => Kind is SkinElementKind.Image or SkinElementKind.Icon or SkinElementKind.Decoration;

    /// <summary>True when this element is a colour surface.</summary>
    public bool IsColor => Kind == SkinElementKind.ColorSurface;

    /// <summary>True when this element is only a container, so there is nothing to change.</summary>
    public bool IsLayout => Kind == SkinElementKind.Layout;

    /// <summary>What the panel writes next to 「类型」.</summary>
    public string KindText => Kind switch
    {
        SkinElementKind.Text => "文字",
        SkinElementKind.Image => "图片",
        SkinElementKind.Icon => "图标",
        SkinElementKind.ColorSurface => "颜色",
        SkinElementKind.Button => "按钮",
        SkinElementKind.Decoration => "装饰",
        _ => "布局容器",
    };
}

/// <summary>
/// The names of the interface elements a skin may change, and what each one is.
///
/// <para>
/// One table so the ids cannot drift apart between the views that declare them and any page that
/// needs to name them: a view sets the id, and <see cref="Resolve"/> fills in the kind and the
/// friendly name. An id that is not listed still works — a page may invent one — but the well-known
/// ids get a human name for free instead of showing a raw identifier to the user.
/// </para>
/// </summary>
public static class SkinElementCatalog
{
    private sealed record Entry(SkinElementKind Kind, string DisplayName, string? TextKey = null, string? AssetId = null, string? ColorKey = null);

    private static readonly Dictionary<string, Entry> Known = new(StringComparer.Ordinal)
    {
        // Navigation entries. The text key is the same key the rail resolves its word from.
        ["nav.home"] = new(SkinElementKind.Text, "导航：概览", "nav.home"),
        ["nav.software"] = new(SkinElementKind.Text, "导航：软件", "nav.software"),
        ["nav.environment"] = new(SkinElementKind.Text, "导航：环境", "nav.environment"),
        ["nav.cloud"] = new(SkinElementKind.Text, "导航：来源", "nav.cloud"),
        ["nav.ai"] = new(SkinElementKind.Text, "导航：AI", "nav.ai"),
        ["nav.extensions"] = new(SkinElementKind.Text, "导航：扩展", "nav.extensions"),
        ["nav.marketplace"] = new(SkinElementKind.Text, "导航：商城", "nav.marketplace"),
        ["nav.plugins"] = new(SkinElementKind.Text, "导航：插件", "nav.plugins"),
        ["nav.tutorial"] = new(SkinElementKind.Text, "导航：教程", "nav.tutorial"),
        ["nav.jobs"] = new(SkinElementKind.Text, "导航：任务", "nav.jobs"),
        ["nav.workshop"] = new(SkinElementKind.Text, "导航：创意工坊", "nav.workshop"),
        ["nav.settings"] = new(SkinElementKind.Text, "导航：设置", "nav.settings"),
        ["nav.update"] = new(SkinElementKind.Text, "导航：更新", "nav.update"),
        ["nav.blog"] = new(SkinElementKind.Text, "导航：博客", "nav.blog"),
        ["nav.sidebar"] = new(SkinElementKind.Text, "导航栏标题", "nav.sidebar"),

        // Shell chrome.
        ["shell.brand_logo"] = new(SkinElementKind.Image, "标题栏标志"),
        ["shell.page_title"] = new(SkinElementKind.Text, "页面标题"),
        ["shell.page_subtitle"] = new(SkinElementKind.Text, "页面副标题"),
        ["shell.skin_picker"] = new(SkinElementKind.Layout, "皮肤选择器"),

        // Home page.
        ["home.character_card"] = new(SkinElementKind.ColorSurface, "概览：角色卡", ColorKey: "surface"),
        ["home.character"] = new(SkinElementKind.Image, "概览：角色立绘"),
        ["home.logo"] = new(SkinElementKind.Icon, "概览：标志"),
        ["home.title"] = new(SkinElementKind.Text, "概览：皮肤名"),
        ["home.subtitle"] = new(SkinElementKind.Text, "概览：皮肤说明"),
        ["home.companion"] = new(SkinElementKind.Image, "概览：小黑（搭档）"),
        ["home.empty_state_art"] = new(SkinElementKind.Image, "概览：空态插图"),
        ["home.empty_state_text"] = new(SkinElementKind.Text, "概览：空态说明"),

        // Shared empty state.
        ["empty.art"] = new(SkinElementKind.Image, "空态插图"),
        ["empty.caption"] = new(SkinElementKind.Text, "空态说明"),

        // Sources page.
        ["cloud.title"] = new(SkinElementKind.Text, "来源：页面标题", TextKey: "cloud.heading"),
        ["cloud.download_button"] = new(SkinElementKind.Button, "来源：下载按钮", TextKey: "common.download"),
        ["cloud.drive_button"] = new(SkinElementKind.Button, "来源：我的网盘按钮", TextKey: "cloud.my_drive"),
        // These four were declared by the view alone: kind and word travelled with the id, but the panel
        // had no human name for them and showed the raw id. A name is part of the element, not of the
        // page that happens to be first to declare it.
        ["cloud.read_share"] = new(SkinElementKind.Button, "来源：读这个分享按钮", TextKey: "cloud.read_share"),
        ["cloud.sign_in"] = new(SkinElementKind.Button, "来源：登录 / 绑定按钮", TextKey: "cloud.sign_in"),
        ["cloud.unbind"] = new(SkinElementKind.Button, "来源：解除绑定按钮", TextKey: "cloud.unbind"),
        ["cloud.browser_login"] = new(SkinElementKind.Button, "来源：内置浏览器登录按钮", TextKey: "cloud.browser_login"),

        // Software page.
        ["software.search"] = new(SkinElementKind.Layout, "软件：搜索框"),
        ["software.install_button"] = new(SkinElementKind.Button, "软件：安装按钮", TextKey: "software.prepare"),
        ["software.status_icon"] = new(SkinElementKind.Icon, "软件：状态图标"),
        ["software.empty_state"] = new(SkinElementKind.Text, "软件：空态说明"),

        // The words below were written into the page itself, which is why clicking them in the editor
        // selected nothing: the picker recognises a control that draws skin text (UiText) or skin art
        // (AssetImage), and a plain TextBlock is neither. Each one is an element now, and the word it
        // shows belongs to the skin.
        ["software.heading"] = new(SkinElementKind.Text, "软件：页面标题", TextKey: "software.heading"),
        ["software.hint"] = new(SkinElementKind.Text, "软件：页面说明", TextKey: "software.hint"),
        ["software.categories"] = new(SkinElementKind.Text, "软件：分类目录标题", TextKey: "software.categories"),
        ["software.detect_button"] = new(SkinElementKind.Button, "软件：检测所选项按钮", TextKey: "software.detect_selected"),
        ["software.scan_button"] = new(SkinElementKind.Button, "软件：扫描当前列表按钮", TextKey: "software.detect_visible"),
        ["software.warning_icon"] = new(SkinElementKind.Icon, "软件：目录完整性警告图标", AssetId: "icon_warning"),
        ["software.plan_title"] = new(SkinElementKind.Text, "软件：安装计划标题", TextKey: "software.plan_title"),
        ["software.batch_title"] = new(SkinElementKind.Text, "软件：批量安装计划标题", TextKey: "software.batch_title"),
        ["software.undeclared_title"] = new(SkinElementKind.Text, "软件：未声明检测标题", TextKey: "software.undeclared_title"),
        ["software.suggest_button"] = new(SkinElementKind.Button, "软件：推荐检测按钮", TextKey: "software.suggest"),
        ["software.select_all_button"] = new(SkinElementKind.Button, "软件：全选按钮", TextKey: "software.select_all"),
        ["software.select_none_button"] = new(SkinElementKind.Button, "软件：全不选按钮", TextKey: "software.select_none"),
        ["software.apply_button"] = new(SkinElementKind.Button, "软件：应用勾选项按钮", TextKey: "software.apply_selected"),
        ["software.clear_list_button"] = new(SkinElementKind.Button, "软件：清空列表按钮", TextKey: "software.clear_list"),
        ["software.detect_paths_title"] = new(SkinElementKind.Text, "软件：检测路径标题", TextKey: "software.detect_paths_title"),
        ["software.save_hint_button"] = new(SkinElementKind.Button, "软件：保存并检测按钮", TextKey: "software.save_and_detect"),
        ["software.clear_hint_button"] = new(SkinElementKind.Button, "软件：清除检测路径按钮", TextKey: "software.clear_hint"),

        // Jobs page.
        ["jobs.title"] = new(SkinElementKind.Text, "任务：页面标题", TextKey: "page.jobs"),
        ["jobs.cancel_all"] = new(SkinElementKind.Button, "任务：全部取消按钮", TextKey: "jobs.cancel_all"),

        // Plugins page:这些字以前写死在页面里，编辑器既选不中也改不掉。
        ["plugins.import_button"] = new(SkinElementKind.Button, "插件：导入按钮", TextKey: "plugins.import_folder"),
        ["plugins.open_folder_button"] = new(SkinElementKind.Button, "插件：打开文件夹按钮", TextKey: "plugins.open_folder"),
        ["plugins.refresh_marketplace_button"] = new(SkinElementKind.Button, "插件：刷新商城按钮", TextKey: "plugins.refresh_marketplace"),
        ["plugins.hint"] = new(SkinElementKind.Text, "插件：页面说明", TextKey: "plugins.hint"),
        ["plugins.installed_title"] = new(SkinElementKind.Text, "插件：已安装列表标题", TextKey: "plugins.installed_title"),
        ["plugins.marketplace_title"] = new(SkinElementKind.Text, "插件：商城标题", TextKey: "plugins.marketplace_title"),
        ["plugins.marketplace_hint"] = new(SkinElementKind.Text, "插件：商城说明", TextKey: "plugins.marketplace_hint"),
        ["plugins.resources_title"] = new(SkinElementKind.Text, "插件：已导入资源标题", TextKey: "plugins.resources_title"),
        ["plugins.resources_hint"] = new(SkinElementKind.Text, "插件：已导入资源说明", TextKey: "plugins.resources_hint"),
        ["plugins.file_label"] = new(SkinElementKind.Text, "插件：插件文件标签", TextKey: "plugins.file_label"),
        ["plugins.signature_label"] = new(SkinElementKind.Text, "插件：签名文件标签", TextKey: "plugins.signature_label"),
        ["plugins.index_label"] = new(SkinElementKind.Text, "插件：商城索引地址标签", TextKey: "plugins.index_label"),
        ["plugins.import_file_button"] = new(SkinElementKind.Button, "插件：导入该文件按钮", TextKey: "plugins.import_file"),
        ["extensions.refresh_button"] = new(SkinElementKind.Button, "扩展：刷新按钮", TextKey: "extensions.refresh"),
        ["extensions.open_folder_button"] = new(SkinElementKind.Button, "扩展：打开文件夹按钮", TextKey: "extensions.open_folder"),
    };

    /// <summary>Every id this build knows, for a page that wants to enumerate what is editable.</summary>
    public static IReadOnlyCollection<string> Ids => Known.Keys;

    /// <summary>True when the id is one of the well-known ones.</summary>
    public static bool IsKnown(string id) => !string.IsNullOrWhiteSpace(id) && Known.ContainsKey(id);

    /// <summary>
    /// Fills in what was not declared beside the element. A declared kind, name, key or colour wins;
    /// anything left blank comes from the table, so a view only has to write the id for a well-known
    /// element but may override any part of it for a one-off.
    /// </summary>
    public static SkinElementDescriptor Resolve(
        string id,
        SkinElementKind? kind = null,
        string? displayName = null,
        string? textKey = null,
        string? assetId = null,
        string? colorKey = null)
    {
        Known.TryGetValue(id, out var known);

        return new SkinElementDescriptor(
            id,
            kind ?? known?.Kind ?? SkinElementKind.Layout,
            string.IsNullOrWhiteSpace(displayName) ? known?.DisplayName ?? id : displayName!,
            textKey ?? known?.TextKey,
            assetId ?? known?.AssetId,
            colorKey ?? known?.ColorKey);
    }
}
