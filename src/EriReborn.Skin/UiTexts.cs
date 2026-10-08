namespace EriReborn.Skin;

/// <summary>
/// The interface's own words, and the skin's chance to change them (spec 5/6).
///
/// <para>
/// Every label the interface shows is looked up here under a stable key. A view never carries the
/// words itself, which is what makes "this skin calls that button 立即安装" possible without a
/// second build of the app.
/// </para>
///
/// <para>
/// A key is stable and dotted by area (<c>nav.home</c>, <c>common.save</c>). Renaming a label means
/// editing one value here, not hunting for the string in a view.
/// </para>
/// </summary>
public static class UiTexts
{
    /// <summary>
    /// The shipped wording. This is what a skin inherits for every key it does not declare, and
    /// what the workshop shows as "默认" next to an edited one.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Defaults { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // ---- navigation (spec 55) ----
            ["nav.home"] = "概览",
            ["nav.software"] = "软件",
            ["nav.software.All"] = "全部",
            ["nav.software.System"] = "系统基础",
            ["nav.software.Runtime"] = "运行环境",
            ["nav.software.Development"] = "开发工具",
            ["nav.software.Network"] = "网络工具",
            ["nav.software.Games"] = "游戏",
            ["nav.software.Graphics"] = "图形设计",
            ["nav.software.Media"] = "音视频",
            ["nav.software.Office"] = "办公阅读",
            ["nav.software.Utility"] = "实用工具",
            ["nav.environment"] = "环境",
            ["nav.environment.Runtime"] = "运行环境",
            ["nav.cloud"] = "来源",
            ["nav.ai"] = "AI 接口",
            ["nav.extensions"] = "扩展",
            ["nav.marketplace"] = "商城",
            ["nav.plugins"] = "插件",
            ["nav.tutorial"] = "使用教程",
            ["nav.jobs"] = "任务",
            ["nav.workshop"] = "创意工坊",
            ["nav.settings"] = "设置",
            ["nav.update"] = "更新公告",
            ["nav.blog"] = "博客",
            ["nav.sidebar"] = "导航",

            // ---- page titles (spec 5/6) ----
            // The shell carried its own copy of these words, so a skin could rename the rail beside
            // a page but not the page's own title.
            ["page.home"] = "概览",
            ["page.software"] = "软件",
            ["page.environment"] = "环境",
            ["page.cloud"] = "来源",
            ["page.ai"] = "AI 接口",
            ["page.extensions"] = "扩展",
            ["page.marketplace"] = "商城",
            ["page.plugins"] = "插件",
            ["page.tutorial"] = "使用教程",
            ["page.jobs"] = "任务",
            ["page.workshop"] = "创意工坊",
            ["page.settings"] = "设置",
            ["page.update"] = "更新公告",
            ["page.blog"] = "博客",

            // ---- shell window chrome ----
            ["shell.minimize"] = "最小化",
            ["shell.maximize"] = "最大化",
            ["shell.restore"] = "还原",
            ["shell.close"] = "关闭",

            // ---- page headings the page itself draws (the shell's title bar has its own copy) ----
            ["cloud.heading"] = "资源从哪儿来",
            ["cloud.my_drive"] = "我的网盘",
            ["cloud.read_share"] = "读这个分享",
            ["cloud.sign_in"] = "登录 / 绑定",
            ["cloud.unbind"] = "解除绑定",
            ["cloud.browser_login"] = "用内置浏览器登录",
            ["jobs.cancel_all"] = "全部取消",
            ["empty.sources"] = "这个资源还没有登记来源。",
            ["software.empty_state"] = "没有符合当前筛选的软件条目。",
            ["jobs.empty_state"] = "目前没有任务。扫描、下载与安装都会出现在这里。",
            ["home.empty_state.scanning"] = "正在扫描软件，结果会写进下面的卡片。",
            ["home.empty_state.empty"] = "软件目录里现在还没有条目。放入目录数据后，清单会出现在下面。",
            ["software.prepare"] = "安装 / 准备",
            ["software.heading"] = "软件清单",
            ["software.hint"] = "这里放着这台机器能装的软件。先选分类或直接搜名字，点一条看它的状态，然后按右边的「安装 / 准备」。",
            ["software.categories"] = "分类目录",
            ["software.detect_selected"] = "检测所选项",
            ["software.detect_visible"] = "扫描当前列表",
            ["software.plan_title"] = "安装计划（确认后执行）",
            ["software.batch_title"] = "批量安装计划（确认后逐项执行）",
            ["software.undeclared_title"] = "未声明检测方式的条目",
            ["software.suggest"] = "从已安装程序推荐",
            ["software.select_all"] = "全选",
            ["software.select_none"] = "全不选",
            ["software.apply_selected"] = "应用勾选项",
            ["software.clear_list"] = "清空列表",
            ["software.detect_paths_title"] = "检测路径（用于清单未声明检测方式的条目）",
            ["software.save_and_detect"] = "保存并检测",
            ["software.clear_hint"] = "清除",
            ["plugins.import_folder"] = "导入插件",
            ["plugins.open_folder"] = "打开插件文件夹",
            ["plugins.refresh_marketplace"] = "加载 / 刷新商城",
            ["plugins.hint"] = "插件是数据：它说明某个资源可以从哪些网盘下载。导入插件不会执行任何代码。",
            ["plugins.installed_title"] = "插件列表",
            ["plugins.marketplace_title"] = "插件商城",
            ["plugins.marketplace_hint"] = "发现别人做好的插件。安装只新增资源，不会改官方目录，也不会动到别的插件。",
            ["plugins.resources_title"] = "已导入资源",
            ["plugins.resources_hint"] = "这些是插件带进来的资源。一个资源即使有多个网盘来源，也仍然只是一个资源；若某个 id 已存在，该条会被拒绝（插件只能新增，不能覆盖既有数据）。",
            ["plugins.file_label"] = "插件文件",
            ["plugins.signature_label"] = "签名文件（可选：省略即视为未签名）",
            ["plugins.import_file"] = "导入该文件",
            ["plugins.index_label"] = "插件商城索引地址",
            ["plugins.empty_installed"] = "插件文件夹里还没有插件。",
            ["plugins.empty_marketplace"] = "插件商城还没有内容。",
            ["plugins.empty_resources"] = "还没有导入任何资源插件。",
            ["extensions.refresh"] = "刷新扩展 / Reload Extensions",
            ["extensions.open_folder"] = "打开扩展文件夹",
            ["extensions.empty_state"] = "还没有安装任何扩展。",

            // ---- words that repeat across pages ----
            // Only the words a control actually shows. A key nobody reads is worse than no key: the
            // editor offers an edit that changes nothing on screen, which reads as the whole feature
            // being broken. A word is added here together with the control that shows it.
            ["common.cancel"] = "取消",
            ["common.refresh"] = "刷新",
            ["common.apply"] = "应用",
            ["common.download"] = "下载",
            ["common.install"] = "安装",
            ["common.reset"] = "重置",
            ["common.next"] = "下一步",

            // ---- the install plan's own buttons ----
            ["install.run"] = "执行安装",
            ["install.runBatch"] = "执行批量安装",
        };

    /// <summary>
    /// The wording for a key: the skin's when it declared one, the shipped wording otherwise.
    ///
    /// <para>
    /// An unknown key resolves to the key itself rather than to blank. A typo then leaves a visible
    /// marker on screen instead of a silently missing label — the failure a translator can act on.
    /// </para>
    /// </summary>
    public static string Resolve(SkinManifest? skin, string key)
    {
        if (skin is not null
            && skin.Texts.TryGetValue(key, out var declared)
            && !string.IsNullOrWhiteSpace(declared))
        {
            return declared;
        }

        return Defaults.TryGetValue(key, out var text) ? text : key;
    }

    /// <summary>Every key a skin may replace, in the order the workshop should list them.</summary>
    public static IReadOnlyList<string> Keys { get; } = Defaults.Keys.ToList();
}
