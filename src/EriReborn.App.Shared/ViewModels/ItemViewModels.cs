using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EriReborn.Cloud;
using EriReborn.Cloud.Providers;
using EriReborn.Core.Domain;
using EriReborn.Extension;

namespace EriReborn.App.Shared.ViewModels;

/// <summary>One row in the software list, carrying the real status (spec 11).</summary>
public sealed partial class SoftwareItemViewModel(SoftwareDefinition definition) : ObservableObject
{
    public SoftwareDefinition Definition { get; } = definition;

    public string Id => Definition.Id;

    public string Name => Definition.Name;

    public string CategoryId => Definition.CategoryId;

    public string DirectoryName => Definition.DirectoryName;

    public string Mode => Definition.Mode.ToString();

    public string Trust => Definition.Trust.ToString();

    public string Tier => Definition.Tier.ToString();

    /// <summary>
    /// One short line about the sources. The record's own ToString prints every
    /// field — provider, full share URL, locator path — and that wall of text was
    /// laid out unwrapped in the row's right column, stretching the whole page
    /// sideways as soon as an entry with a located share was selected.
    /// </summary>
    public string SourceSummary => Definition.Sources.Count == 0
        ? "无来源"
        : Definition.Sources.Count == 1
            ? DescribeSourceKind(Definition.Sources[0])
            : $"{Definition.Sources.Count} 个来源（{string.Join("、", Definition.Sources.Select(DescribeSourceKind).Distinct())}）";

    private static string DescribeSourceKind(SoftwareSource source) => source.Kind switch
    {
        SourceKind.CloudShare => "网盘分享",
        SourceKind.HttpUrl => "直链",
        SourceKind.Winget => "winget",
        SourceKind.Local => "本地文件",
        SourceKind.Official => "官方",
        _ => "其他",
    };

    /// <summary>
    /// What the software is, in the catalog's own words (spec 13). A row that only named a package
    /// left the reader guessing what it was for, which is why the list explains itself now.
    /// </summary>
    public string Description => Definition.Description ?? string.Empty;

    public bool HasDescription => Description.Length > 0;

    /// <summary>The status word shown in the row's status pill.</summary>
    public string StatusText => Status switch
    {
        SoftwareStatus.Installed => "已安装",
        SoftwareStatus.Missing => "未安装",
        SoftwareStatus.Broken => "异常",
        SoftwareStatus.Installing => "安装中",
        SoftwareStatus.Updating => "更新中",
        SoftwareStatus.Failed => "失败",
        SoftwareStatus.Unsupported => "不支持",
        _ => "未知",
    };

    /// <summary>
    /// The pill colour for the status. The reference launcher paints a solid pill with
    /// white text; a status that is only a word on the page is easy to miss in a list of
    /// two hundred rows.
    /// </summary>
    public string StatusColor => Status switch
    {
        SoftwareStatus.Installed => "#2E9E6B",
        SoftwareStatus.Missing => "#C2801F",
        SoftwareStatus.Broken or SoftwareStatus.Failed => "#C2453F",
        SoftwareStatus.Installing or SoftwareStatus.Updating => "#5B6BE0",
        SoftwareStatus.Unsupported => "#6E6E85",
        _ => "#8A8AA0",
    };

    /// <summary>The tier pill colour, deliberately muted: tier is information, not an alarm.</summary>
    public string TierColor => Definition.Tier switch
    {
        SoftwareTier.Core => "#7C7BF0",
        SoftwareTier.Recommended => "#4FD1C5",
        SoftwareTier.Optional => "#A5A8FF",
        _ => "#B9B9CC",
    };

    /// <summary>
    /// The state icon for this row, from the library's shared state set. A list of
    /// two hundred rows is where an icon earns its place: it is what makes "installed"
    /// and "missing" tell themselves apart at a glance.
    /// </summary>
    public string StatusIconId => Status switch
    {
        SoftwareStatus.Installed => "icon_success",
        SoftwareStatus.Missing => "icon_warning",
        SoftwareStatus.Broken => "icon_error",
        SoftwareStatus.Installing or SoftwareStatus.Updating => "icon_download",
        SoftwareStatus.Failed => "icon_error",
        SoftwareStatus.Unsupported => "icon_unsupported",
        _ => "icon_unknown",
    };

    [ObservableProperty]
    private SoftwareStatus _status = SoftwareStatus.Unknown;

    [ObservableProperty]
    private string? _detectedVersion;

    [ObservableProperty]
    private string? _statusDetail;
}

/// <summary>
/// 软件页「插件资源树」里的一个节点（schema v2 树浏览，AC-12）。
///
/// <para>
/// 四种形态：插件根（<see cref="IsPluginRoot"/>）、纯分组分支、文件叶子、文件夹叶子。
/// 文件夹资源展开时懒加载云盘内容（复用 PluginBuilder 的 ShareTreeRow 范式）。
/// </para>
/// </summary>
public sealed partial class SoftwareTreeNodeVm : ObservableObject
{
    private SoftwareViewModel? _owner;

    // 懒加载所需的云盘信息（仅 folder 资源节点有值）。
    private string? _shareUrl;
    private string? _providerId;
    private string? _providerItemId;
    // 这个 folder 资源在分享里的路径（locator.path），用于展开后给子文件创建 source 时填 Path。
    // 没这个 path，子文件的 source.Path 会变成 "/"，ShareTreeWalker 会去分享根目录找，
    // 但子文件其实在这个 folder 里——根目录里没它的 ID，于是报"分享里没有「xxx」"。
    private string? _folderPath;

    public SoftwareTreeNodeVm(string id, string name, bool isPluginRoot, SoftwareItemViewModel? item, bool isFolderResource)
    {
        Id = id;
        Name = name;
        _isPluginRoot = isPluginRoot;
        _item = item;
        _isFolderResource = isFolderResource;
        _isExpanded = isPluginRoot;
    }

    public string Id { get; }

    public string Name { get; }

    private readonly bool _isPluginRoot;

    [ObservableProperty]
    private SoftwareItemViewModel? _item;

    [ObservableProperty]
    private bool _isFolderResource;

    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>懒加载状态：0=未加载, 1=加载中, 2=已加载, 3=失败。</summary>
    [ObservableProperty]
    private int _loadState;

    [ObservableProperty]
    private string? _loadError;

    public ObservableCollection<SoftwareTreeNodeVm> Children { get; } = new();

    public bool IsPluginRoot => _isPluginRoot;

    public bool IsInstallable => Item is not null;

    /// <summary>有子节点或可能可以懒加载子节点（分组/插件根/folder 资源）。</summary>
    public bool HasChildren => _isPluginRoot || Item is null || IsFolderResource;

    public string KindText => _isPluginRoot
        ? "插件"
        : Item is null
            ? "分组"
            : IsFolderResource ? "文件夹" : "文件";

    public string? StatusText => Item?.StatusText;

    public string StatusIconId => Item?.StatusIconId ?? "icon_unknown";

    public bool HasStatus => Item is not null;

    /// <summary>是否可以懒加载子节点（folder 资源且有云盘信息）。</summary>
    public bool CanLazyLoad => IsFolderResource && _shareUrl is not null && _providerId is not null;

    /// <summary>是否正在加载。</summary>
    public bool IsLoading => LoadState == 1;

    /// <summary>是否加载失败。</summary>
    public bool IsLoadFailed => LoadState == 3;

    partial void OnItemChanged(SoftwareItemViewModel? value)
    {
        OnPropertyChanged(nameof(IsInstallable));
        OnPropertyChanged(nameof(KindText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusIconId));
        OnPropertyChanged(nameof(HasStatus));
    }

    partial void OnIsFolderResourceChanged(bool value) => OnPropertyChanged(nameof(KindText));

    partial void OnLoadStateChanged(int value)
    {
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(IsLoadFailed));
    }

    /// <summary>
    /// 展开时触发懒加载：首次展开或失败重试时从云盘拉取子节点。
    /// </summary>
    async partial void OnIsExpandedChanged(bool value)
    {
        if (!value || !CanLazyLoad || LoadState == 1 || LoadState == 2)
        {
            return;
        }

        if (_owner is null)
        {
            return;
        }

        LoadState = 1;
        LoadError = null;
        try
        {
            await _owner.EnsureNodeChildrenAsync(this).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LoadState = 3;
            LoadError = ex.Message;
        }
    }

    /// <summary>设置懒加载所需的云盘信息和所有者引用（由 RebuildPluginTree 调用）。</summary>
    internal void SetCloudInfo(SoftwareViewModel owner, string shareUrl, string providerId, string? providerItemId, string? folderPath)
    {
        _owner = owner;
        _shareUrl = shareUrl;
        _providerId = providerId;
        _providerItemId = providerItemId;
        _folderPath = folderPath;
        OnPropertyChanged(nameof(CanLazyLoad));
    }

    internal (string? ShareUrl, string? ProviderId, string? ProviderItemId, string? FolderPath) GetCloudInfo()
        => (_shareUrl, _providerId, _providerItemId, _folderPath);

    internal void AttachItem(SoftwareItemViewModel item, bool isFolderResource)
    {
        Item = item;
        IsFolderResource = isFolderResource;
    }
}

/// <summary>One cloud platform row. All five are peers (spec 76).</summary>
public sealed partial class CloudProviderViewModel(ICloudProvider provider, CloudAuthState authState, string? credentialHint) : ObservableObject
{
    public ICloudProvider Provider { get; } = provider;

    public string Id => Provider.Id;

    public string DisplayName => Provider.DisplayName;

    public string Implementation => Provider.ImplementationKind switch
    {
        CloudImplementationKind.OfficialApi => "官方 API",
        CloudImplementationKind.HtmlParsing => "HTML 解析",
        _ => "尚未接入",
    };

    public bool RequiresAuthentication => Provider.RequiresAuthentication;

    public string AuthStateText => authState switch
    {
        CloudAuthState.Authenticated => "已认证",
        CloudAuthState.AuthRequired => "需要登录",
        CloudAuthState.Expired => "凭据已过期",
        CloudAuthState.NotRequired => "无需登录",
        _ => "不支持",
    };

    /// <summary>What the user typed: an OAuth token, or a Cookie pair for cookie-based platforms.</summary>
    [ObservableProperty]
    private string _tokenInput = string.Empty;

    /// <summary>Result of the last save/clear, shown under the entry.</summary>
    [ObservableProperty]
    private string _loginStatus = string.Empty;

    /// <summary>True when the platform accepts a credential at all.</summary>
    public bool CanEnterCredential => RequiresAuthentication && Provider is CloudProviderBase;

    /// <summary>
    /// True when we have the platform's own sign-in page for the embedded browser. Deliberately
    /// independent of <see cref="CanEnterCredential"/>: 蓝奏云 parses share pages without an account,
    /// so it has no credential row, yet it still has a sign-in page worth offering.
    /// </summary>
    public bool CanOpenBrowserLogin => CloudSignInPages.For(Provider.Id) is not null;

    /// <summary>
    /// Saves a credential the embedded browser captured after a successful sign-in. It goes through
    /// the same command as the manual box, so one parser accepts both "BDUSS=...; STOKEN=...".
    /// </summary>
    public async Task AcceptCapturedCredentialAsync(string captured)
    {
        TokenInput = captured;
        await SaveCredentialCommand.ExecuteAsync(null).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SaveCredentialAsync()
    {
        if (Provider is not CloudProviderBase providerBase)
        {
            LoginStatus = "该平台不支持在这里登录。";
            return;
        }

        var input = TokenInput.Trim();
        if (input.Length == 0)
        {
            LoginStatus = "先填入 Token 或 Cookie。";
            return;
        }

        // A "k=v" value is a cookie pair (百度、夸克这类靠 Cookie）；其它按 OAuth Token 处理。
        var isCookie = input.Contains('=', StringComparison.Ordinal);
        var credential = new CloudCredential(Id, isCookie ? null : input, isCookie ? input : null);

        try
        {
            await providerBase.SaveCredentialAsync(credential).ConfigureAwait(true);
            LoginStatus = "已保存。点「刷新平台状态」看认证结果。";
            TokenInput = string.Empty;
        }
        catch (Exception ex)
        {
            // The generated command runs this as async void, so an exception escaping here ends the
            // process with no window, no dialog and no event-log entry. A store that cannot write
            // (sandboxed or locked-down profile) must surface as a message instead.
            LoginStatus = "保存凭据失败：" + ex.GetType().Name + " — " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task ClearCredentialAsync()
    {
        if (Provider is not CloudProviderBase providerBase)
        {
            LoginStatus = "该平台没有可解除的凭据。";
            return;
        }

        try
        {
            await providerBase.ClearCredentialAsync().ConfigureAwait(true);
            LoginStatus = "已解除绑定。点「刷新平台状态」确认。";
        }
        catch (Exception ex)
        {
            LoginStatus = "解除绑定失败：" + ex.GetType().Name + " — " + ex.Message;
        }
    }

    /// <summary>
    /// The platform badge from the asset library. The five platform ids (123, baidu,
    /// quark, lanzou, xunlei) are exactly the library's file names, so the badge is
    /// derived rather than hand-mapped per platform.
    /// </summary>
    public string BadgeAssetId => $"cloud_{Id}_badge";

    /// <summary>The status icon for this row, from the shared state icon set.</summary>
    public string StateIconId => authState switch
    {
        CloudAuthState.Authenticated => "icon_success",
        CloudAuthState.AuthRequired => "icon_warning",
        CloudAuthState.Expired => "icon_error",
        CloudAuthState.NotRequired => "icon_info",
        _ => "icon_unsupported",
    };

    public string? CredentialHint => credentialHint;

    public string? LimitationNote => Provider.LimitationNote;

    public string ImplementationNote => Provider.ImplementationKind == CloudImplementationKind.NotImplemented
        ? "本构建未接入该平台接口，调用会明确失败。"
        : Provider.ImplementationKind == CloudImplementationKind.HtmlParsing
            ? "基于网页结构解析，页面变化会导致结果不完整。"
            : "使用平台官方接口。";
}

/// <summary>One marketplace listing (spec 35).</summary>
public sealed partial class MarketplaceItemViewModel(Extension.Marketplace.MarketplaceEntry entry, bool installed) : ObservableObject
{
    public Extension.Marketplace.MarketplaceEntry Entry { get; } = entry;

    public string Id => Entry.Id;

    public string Name => Entry.Name;

    public string Author => Entry.Author ?? "未知作者";

    public string Version => Entry.Version ?? "-";

    public string Description => Entry.Description ?? string.Empty;

    public string Trust => Entry.TrustText;

    public string Categories => Entry.Categories.Count == 0 ? "-" : string.Join(", ", Entry.Categories);

    public string Tags => Entry.Tags.Count == 0 ? "-" : string.Join(", ", Entry.Tags);

    public string Permissions => Entry.Permissions.Count == 0 ? "无" : string.Join(", ", Entry.Permissions);

    public string Dependencies => Entry.Dependencies.Count == 0 ? "无" : string.Join(", ", Entry.Dependencies);

    public string Changelog => Entry.Changelog.Count == 0 ? "-" : string.Join("；", Entry.Changelog);

    public string? Homepage => Entry.Homepage;

    [ObservableProperty]
    private bool _isInstalled = installed;

    public string InstalledText => IsInstalled ? "已安装" : "未安装";

    partial void OnIsInstalledChanged(bool value) => OnPropertyChanged(nameof(InstalledText));
}

/// <summary>
/// One installed extension. Its state can change while the page is open, so the
/// item is observable and can be told to enable or disable itself (spec 34).
/// </summary>
public sealed partial class ExtensionItemViewModel : ObservableObject
{
    private ExtensionStatus _status;

    public ExtensionItemViewModel(ExtensionStatus status, Func<ExtensionItemViewModel, Task>? toggle = null)
    {
        _status = status;
        if (toggle is not null)
        {
            ToggleCommand = new AsyncRelayCommand(() => toggle(this));
        }
    }

    /// <summary>Null when the row has no toggle (e.g. read-only lists).</summary>
    public IAsyncRelayCommand? ToggleCommand { get; }

    public ExtensionStatus Status => _status;

    public string Id => _status.Manifest.Id;

    public string Name => _status.Manifest.Name;

    public string Version => _status.Manifest.Version ?? "-";

    public string Trust => _status.Manifest.Trust.ToString();

    public string State => _status.State.ToString();

    public string Message => _status.Message;

    /// <summary>Human-readable purpose, shown under the name so an extension is understandable at a glance.</summary>
    public string Description => _status.Manifest.Description ?? string.Empty;

    public string Directory => _status.Manifest.Directory;

    public string Permissions => _status.Manifest.Permissions.Count == 0
        ? "无"
        : string.Join(", ", _status.Manifest.Permissions);

    /// <summary>Loaded and running.</summary>
    public bool IsEnabled => _status.State == ExtensionLoadState.Loaded;

    public bool IsDisabled => _status.State == ExtensionLoadState.Disabled;

    /// <summary>A dependency could not be satisfied, so it cannot run.</summary>
    public bool IsBlocked => _status.State == ExtensionLoadState.DependencyMissing;

    /// <summary>Only a loaded or a disabled extension can be toggled.</summary>
    public bool CanToggle => IsEnabled || IsDisabled;

    public string ToggleText => IsEnabled ? "禁用" : "启用";

    /// <summary>Replaces the reported state after a successful toggle.</summary>
    public void Update(ExtensionStatus status)
    {
        _status = status;
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(IsDisabled));
        OnPropertyChanged(nameof(IsBlocked));
        OnPropertyChanged(nameof(CanToggle));
        OnPropertyChanged(nameof(ToggleText));
    }
}
