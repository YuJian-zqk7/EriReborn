using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EriReborn.App.Shared.Services;
using EriReborn.Cloud;
using EriReborn.Cloud.Providers;
using EriReborn.Core.Domain;
using EriReborn.Core.Jobs;
using EriReborn.Engine.Download;
using EriReborn.Engine.Software;

namespace EriReborn.App.Shared.ViewModels;

/// <summary>
/// The sources page. Its unit is the resource, not the platform: pick a piece of software and
/// the page says where that resource can be fetched from and which of those places works right
/// now (spec 38-41). The five platforms stay peers, so none of them is the subject of the page
/// (spec 30/31); they appear as the accounts a source is fetched through.
///
/// <para>
/// The page only ever decides what to ask for. It resolves nothing and downloads nothing itself:
/// starting a download hands the source to <see cref="DownloadJobService"/>, which walks the one
/// resolver → route → engine → job path, so what the task list shows is the real transfer.
/// </para>
/// </summary>
public sealed partial class CloudViewModel : ViewModelBase, IPageActions
{
    /// <summary>One line under the page title in the shell's top bar.</summary>
    public string Subtitle => Summary;

    /// <inheritdoc />
    public IReadOnlyList<PageAction> GetPageActions() => new[]
    {
        new PageAction("重新检查", RefreshCommand, Primary: true, ActionKey: "refresh"),
    };

    private readonly AppHost _host;

    public CloudViewModel(AppHost host)
    {
        _host = host;
        Title = "资源来源";
        Reload();

        // The accounts and the summary need the credential store, so they arrive after the
        // resource list is on screen rather than holding the page up.
        RefreshCommand.Execute(null);
    }

    /// <summary>Resources that have at least one source, sorted by name.</summary>
    public ObservableCollection<ResourceRow> Resources { get; } = new();

    [ObservableProperty]
    private ResourceRow? _selectedResource;

    /// <summary>The selected resource's sources, each with its real availability.</summary>
    public ObservableCollection<SourceRow> Sources { get; } = new();

    /// <summary>Which source the automatic choice landed on, said in one sentence.</summary>
    [ObservableProperty]
    private string _recommended = string.Empty;

    [ObservableProperty]
    private string _sourceStatus = "左边选一个资源，这里会列出它能从哪儿取。";

    /// <summary>
    /// The accounts sources are fetched through. A source names a platform, and that platform may
    /// need a sign-in before its sources work, so the accounts sit beside the sources instead of
    /// being the page's subject.
    /// </summary>
    public ObservableCollection<CloudProviderViewModel> Providers { get; } = new();

    [ObservableProperty]
    private string _credentialsAvailable = string.Empty;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string _shareStatus = "选一个来源就能读出分享里的文件；「我的网盘」会先让你登录。";

    /// <summary>Rows of the most recent share or drive listing, each able to download itself.</summary>
    public ObservableCollection<ShareFileRow> ShareFiles { get; } = new();

    /// <summary>
    /// Where the listing currently is, from the root down. Shown as a breadcrumb so the user can
    /// walk back up instead of opening a new page for every folder.
    /// </summary>
    public ObservableCollection<ShareCrumb> Breadcrumbs { get; } = new();

    /// <summary>True once there is somewhere to go back to, which is what shows the breadcrumb row.</summary>
    public bool HasBreadcrumbs => Breadcrumbs.Count > 1;

    /// <summary>The most recent job this page started, so a caller can watch it finish.</summary>
    public Job? LastDownloadJob { get; private set; }

    // What the listing below is showing: a share (with a URL) or the user's own drive.
    private ICloudProvider? _browseProvider;
    private CloudCredential? _browseCredential;
    private string? _browseShareUrl;
    private bool _browsingDrive;
    private readonly List<ShareCrumb> _trail = new();

    private bool _reloading;

    partial void OnSelectedResourceChanged(ResourceRow? value)
    {
        // A reload assigns the selection itself and assesses once at the end, so the hook stays
        // quiet for that assignment instead of racing the explicit load.
        if (!_reloading)
        {
            _ = LoadSourcesAsync(value);
        }
    }

    /// <summary>Rebuilds the resource list, re-reads the accounts, and re-assesses the selection.</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            Reload();
            await ReloadAccountsAsync().ConfigureAwait(true);
            await LoadSourcesAsync(SelectedResource).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Fail("刷新来源", ex);
        }
    }

    /// <summary>
    /// Reports a failed page action without letting the exception escape.
    ///
    /// <para>
    /// These commands run on the dispatcher, and an exception thrown there does not stop at the page: it
    /// leaves the dispatcher and takes the whole application down — which is exactly what signing in to a
    /// drive and being refused once did (see CloudJson). A page that says what failed is the difference
    /// between a visible problem and an application that disappears.
    /// </para>
    /// </summary>
    private void Fail(string action, Exception ex)
    {
        _host.Log.Error("cloud.command", $"{action}失败：{ex.GetType().Name} — {ex.Message}", ex);
        ShareStatus = $"{action}失败：{ex.GetType().Name} — {ex.Message}";
    }

    /// <summary>
    /// Reads the accounts and the summary line. The resource list is built from the manifest
    /// alone, so opening this page does not ask the credential store about hundreds of share
    /// links; availability is worked out for the resource the person actually selects.
    /// </summary>
    private async Task ReloadAccountsAsync()
    {
        var vaultAvailable = _host.Platform.Credentials.IsAvailable;
        var accounts = new List<CloudProviderViewModel>();

        foreach (var provider in _host.CloudProviders.InDisplayOrder())
        {
            var credential = provider is CloudProviderBase providerBase
                ? await providerBase.LoadCredentialAsync().ConfigureAwait(true)
                : null;

            accounts.Add(new CloudProviderViewModel(
                provider,
                provider.GetAuthState(credential),
                vaultAvailable ? null : "当前环境没有可用的安全凭据存储。"));
        }

        Providers.Clear();
        foreach (var account in accounts)
        {
            Providers.Add(account);
        }

        var authenticated = accounts.Count(account => account.AuthStateText == "已认证");
        var waiting = accounts.Count(account => account.AuthStateText is "需要登录" or "凭据已过期");
        var fromCloud = Resources.Count(resource => resource.UsesCloud);

        CredentialsAvailable = vaultAvailable
            ? $"安全凭据存储可用：{_host.CredentialStoreDescription}。"
            : "安全凭据存储不可用：无法保存 Token / Cookie。";
        Summary = Resources.Count == 0
            ? "目录里还没有带来源的资源。"
            : $"{Resources.Count} 个资源有来源，其中 {fromCloud} 个用到网盘；网盘账号已登录 {authenticated} 个，待登录 {waiting} 个。";
    }

    /// <summary>Asks the engines what each of a resource's sources can do, and which one to use.</summary>
    private async Task LoadSourcesAsync(ResourceRow? row)
    {
        Sources.Clear();
        Recommended = string.Empty;

        if (row is null)
        {
            SourceStatus = "左边选一个资源，这里会列出它能从哪儿取。";
            return;
        }

        SourceStatus = row.Name + "：正在检查各个来源…";

        try
        {
            var assessed = await _host.SourceAvailability.AssessAsync(row.Software).ConfigureAwait(true);
            var recommended = SourceAvailabilityService.Pick(assessed);

            foreach (var availability in assessed)
            {
                Sources.Add(new SourceRow(this, availability, ReferenceEquals(availability, recommended)));
            }

            var ready = assessed.Count(availability => availability.IsReady);
            SourceStatus = assessed.Count == 0
                ? row.Name + "：没有登记来源。"
                : $"{row.Name}：{assessed.Count} 个来源，现在就能用 {ready} 个。";

            Recommended = recommended is null
                ? string.Empty
                : recommended.IsReady
                    ? "建议用这条：" + DescribeTitle(recommended)
                    : "最接近可用的是这条：" + DescribeTitle(recommended) + "（" + recommended.Summary + "）";
        }
        catch (Exception ex)
        {
            // A page that cannot answer says so, rather than showing an empty list that reads
            // as if the resource had no sources at all.
            SourceStatus = row.Name + "：检查来源时出错 — " + ex.GetType().Name + " — " + ex.Message;
        }
    }

    /// <summary>What one source is, in the words the page shows.</summary>
    internal static string DescribeTitle(SourceAvailability availability)
        => availability.Source.Kind switch
        {
            SourceKind.CloudShare => availability.PlatformName + " 的分享",
            SourceKind.Winget => "系统软件源",
            SourceKind.HttpUrl => "官方直链",
            SourceKind.Local => "本地文件",
            _ => "手工来源",
        };

    internal CloudProviderViewModel? FindAccount(SourceAvailability availability)
        => availability.Source.Kind != SourceKind.CloudShare
            ? null
            : Providers.FirstOrDefault(account =>
                string.Equals(account.Id, availability.Source.ProviderId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads the share behind a source, through the platform that source names, and shows the folder
    /// the source's locator points into, together with a breadcrumb so the rest of the tree can be
    /// walked from there.
    /// </summary>
    internal async Task ReadSourceShareAsync(SourceRow row)
    {
        var source = row.Availability.Source;
        if (source.Kind != SourceKind.CloudShare || string.IsNullOrWhiteSpace(source.ShareUrl))
        {
            ShareFiles.Clear();
            Breadcrumbs.Clear();
            OnPropertyChanged(nameof(HasBreadcrumbs));
            ShareStatus = "这条来源不是网盘分享，没有可读的分享目录。";
            return;
        }

        // The platform comes from the source itself: nothing here matches on a domain name, so a
        // source naming a platform this build has no adapter for is reported instead of guessed at.
        var provider = _host.CloudProviders.Resolve(source);
        if (provider is null)
        {
            ShareFiles.Clear();
            Breadcrumbs.Clear();
            OnPropertyChanged(nameof(HasBreadcrumbs));
            ShareStatus = $"没有接入这条来源写明的平台「{source.ProviderId}」。";
            return;
        }

        ShareFiles.Clear();
        ShareStatus = provider.DisplayName + "：正在读取分享目录…";

        var credential = provider is CloudProviderBase providerBase
            ? await providerBase.LoadCredentialAsync().ConfigureAwait(true)
            : null;

        _browseProvider = provider;
        _browseCredential = credential;
        _browseShareUrl = source.ShareUrl;
        _browsingDrive = false;
        _trail.Clear();
        _trail.Add(new ShareCrumb("分享根目录", null));

        await ListLocatedFolderAsync(source.Locator).ConfigureAwait(true);
    }

    /// <summary>
    /// Shows the folder a source's locator points into, walking into it folder by folder, instead of
    /// stopping at the share's root.
    /// </summary>
    /// <remarks>
    /// A share link names a tree, and the locator is the part that says which item in it this source
    /// means. Reading only the root therefore threw that away: an official source names one file three
    /// folders down, and opening the root left the user to walk there by hand, folder by folder, every
    /// time — the location was known and simply not used. A locator that cannot be walked is not fatal;
    /// the root listing is shown instead, with the reason said out loud rather than a blank page.
    /// </remarks>
    private async Task ListLocatedFolderAsync(ResourceLocator? locator)
    {
        if (_browseProvider is null || locator is null || locator.IsEmpty || !locator.NeedsFolderWalk)
        {
            await RefreshListingAsync(null).ConfigureAwait(true);
            AnnotateLocatedItem(locator);
            return;
        }

        var folderId = (string?)null;

        // Where the walk began. A step that cannot be walked falls back to the root listing below,
        // and the trail has to go back with it: leaving the crumb of the folder we failed to open
        // made the breadcrumb name a folder while the rows underneath were the root's.
        var trailDepth = _trail.Count;

        foreach (var step in locator.FolderSegments)
        {
            CloudListResult listed;
            try
            {
                listed = await _browseProvider
                    .ListShareFolderAsync(_browseShareUrl!, folderId, _browseCredential)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Fail("读取分享目录", ex);
                return;
            }

            if (!listed.Success)
            {
                ShareStatus = _browseProvider.DisplayName + "：" + (listed.Message ?? "读取失败。");
                return;
            }

            var folder = listed.Files.FirstOrDefault(
                file => file.IsFolder && string.Equals(file.Name, step, StringComparison.Ordinal));

            if (folder is null)
            {
                // The folder the locator names is gone. Falling back to the root keeps the page usable,
                // and saying which step failed is what makes "re-locate this source" a fixable request.
                if (_trail.Count > trailDepth)
                {
                    _trail.RemoveRange(trailDepth, _trail.Count - trailDepth);
                }

                await RefreshListingAsync(null).ConfigureAwait(true);
                ShareStatus += $"  位置里的「{step}」在分享里没有找到，可能已经改动过；"
                    + "可以在插件编辑器里重新定位这条来源。";
                return;
            }

            _trail.Add(new ShareCrumb(folder.Name, folder.Id));
            folderId = folder.Id;
        }

        await RefreshListingAsync(folderId).ConfigureAwait(true);
        AnnotateLocatedItem(locator);
    }

    /// <summary>Names the one file a located source wants, so it is not lost among its neighbours.</summary>
    private void AnnotateLocatedItem(ResourceLocator? locator)
    {
        if (locator?.ItemName is { Length: > 0 } name)
        {
            ShareStatus += $"  这条来源要的是「{name}」，点它那一行的「下载」即可。";
        }
    }

    /// <summary>Lists a signed-in account's own drive root, so its files can be downloaded here.</summary>
    [RelayCommand]
    private async Task OpenDriveAsync()
    {
        try
        {
            ShareFiles.Clear();
            Breadcrumbs.Clear();
            OnPropertyChanged(nameof(HasBreadcrumbs));
            ShareStatus = "正在读取我的网盘…";

            foreach (var candidate in _host.CloudProviders.InDisplayOrder())
            {
                if (candidate is not CloudProviderBase providerBase)
                {
                    continue;
                }

                var credential = await providerBase.LoadCredentialAsync().ConfigureAwait(true);
                var listed = await candidate.ListChildrenAsync("0", credential).ConfigureAwait(true);
                if (!listed.Success)
                {
                    continue;
                }

                _browseProvider = candidate;
                _browseCredential = credential;
                _browseShareUrl = null;
                _browsingDrive = true;
                _trail.Clear();
                _trail.Add(new ShareCrumb(candidate.DisplayName + " 我的网盘", null));

                Fill(listed);
                RebuildBreadcrumbs();
                ShareStatus = candidate.DisplayName + " 我的网盘：共 " + listed.Files.Count + " 项（点「进入」打开目录，点「下载」取文件）";
                return;
            }

            ShareStatus = "没有可直接浏览的网盘：先在上面登录一个平台。";
        }
        catch (Exception ex)
        {
            Fail("读取我的网盘", ex);
        }
    }

    /// <summary>Opens one folder of the current listing, one level deeper.</summary>
    internal async Task OpenFolderAsync(CloudFile folder)
    {
        if (_browseProvider is null)
        {
            ShareStatus = "先读取一次分享目录或「我的网盘」，再进入目录。";
            return;
        }

        if (!folder.IsFolder)
        {
            ShareStatus = folder.Name + " 不是目录。";
            return;
        }

        _trail.Add(new ShareCrumb(folder.Name, folder.Id));
        await RefreshListingAsync(folder.Id).ConfigureAwait(true);
    }

    /// <summary>Walks back up to a breadcrumb, dropping everything below it.</summary>
    [RelayCommand]
    private async Task GoToCrumbAsync(ShareCrumb? crumb)
    {
        if (crumb is null || _browseProvider is null)
        {
            return;
        }

        try
        {
            var index = _trail.IndexOf(crumb);
            if (index < 0)
            {
                return;
            }

            _trail.RemoveRange(index + 1, _trail.Count - index - 1);
            await RefreshListingAsync(crumb.ItemId).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Fail("返回上级目录", ex);
        }
    }

    /// <summary>Reads the folder the trail currently points at and shows its rows.</summary>
    private async Task RefreshListingAsync(string? parentItemId)
    {
        if (_browseProvider is null)
        {
            ShareStatus = "先选一个来源读取分享，或打开「我的网盘」。";
            return;
        }

        ShareStatus = _browseProvider.DisplayName + "：正在读取目录…";

        try
        {
            var listed = _browsingDrive
                ? await _browseProvider.ListChildrenAsync(parentItemId ?? "0", _browseCredential).ConfigureAwait(true)
                : await _browseProvider
                    .ListShareFolderAsync(_browseShareUrl!, parentItemId, _browseCredential)
                    .ConfigureAwait(true);

            if (!listed.Success)
            {
                ShareFiles.Clear();
                RebuildBreadcrumbs();
                ShareStatus = _browseProvider.DisplayName + "：" + (listed.Message ?? "读取失败。");
                return;
            }

            Fill(listed);
            RebuildBreadcrumbs();

            var where = _trail.Count > 0 ? _trail[^1].Name : "目录";
            ShareStatus = $"{where}：共 {listed.Files.Count} 项"
                + (_browsingDrive ? "（点「进入」打开目录，点「下载」取文件）" : string.Empty);
        }
        catch (Exception ex)
        {
            Fail("读取目录", ex);
        }
    }

    private void RebuildBreadcrumbs()
    {
        Breadcrumbs.Clear();
        foreach (var crumb in _trail)
        {
            Breadcrumbs.Add(crumb);
        }

        OnPropertyChanged(nameof(HasBreadcrumbs));
    }

    private void Fill(CloudListResult result)
    {
        ShareFiles.Clear();
        foreach (var file in result.Files)
        {
            var size = FormatSize(file.SizeBytes);
            var prefix = file.IsFolder ? "[目录] " : "[文件] ";
            ShareFiles.Add(new ShareFileRow(this, file, prefix + file.Name + (size.Length > 0 ? "  " + size : string.Empty)));
        }
    }

    /// <summary>
    /// Starts a real download for a listed file. The page builds the source and asks
    /// <see cref="DownloadJobService"/> for a job: it never opens a connection or writes a file
    /// itself, so everything the task list shows comes from the downloader.
    /// </summary>
    internal async Task DownloadShareFileAsync(CloudFile file)
    {
        if (_browseProvider is null)
        {
            ShareStatus = "先读取一次分享目录或「我的网盘」，再下载。";
            return;
        }

        if (file.IsFolder)
        {
            await DownloadFolderAsync(file).ConfigureAwait(true);
            return;
        }

        LastDownloadJob = _browsingDrive
            ? StartDriveDownload(file)
            : _host.DownloadJobs.Start(new DownloadJobRequest(
                BuildShareSource(file),
                _host.DownloadDirectory,
                file.Name));

        ShareStatus = $"{file.Name}：已加入任务中心（{LastDownloadJob.Id}）。在「任务」页面可以看到真实进度、速度和校验结果。";
    }

    /// <summary>
    /// Downloads a whole folder, keeping its shape: each file becomes its own job and lands under a
    /// subdirectory mirroring its place in the tree, so two files with the same name in different
    /// folders do not overwrite each other.
    /// </summary>
    private async Task DownloadFolderAsync(CloudFile folder)
    {
        var pending = new List<(CloudFile File, string Relative)>();
        await CollectAsync(folder, folder.Name, pending, depth: 0).ConfigureAwait(true);

        if (pending.Count == 0)
        {
            ShareStatus = folder.Name + "：里面没有可下载的文件（或这个平台不能进入子目录）。";
            return;
        }

        foreach (var (entry, relative) in pending)
        {
            var directory = Path.Combine(_host.DownloadDirectory, relative);
            var source = _browsingDrive ? BuildDriveSource(entry) : BuildShareSource(entry);
            _host.DownloadJobs.Start(new DownloadJobRequest(
                source,
                directory,
                entry.Name,
                Title: "下载 " + relative.Replace('\\', '/') + "/" + entry.Name));
        }

        ShareStatus = $"{folder.Name}：已按目录结构加入 {pending.Count} 个下载任务（保留相对路径）。";
    }

    /// <summary>Collects a folder's files, walking subfolders to a bounded depth.</summary>
    private async Task CollectAsync(CloudFile folder, string relative, List<(CloudFile File, string Relative)> into, int depth)
    {
        if (depth > 8 || _browseProvider is null)
        {
            return;
        }

        var listed = _browsingDrive
            ? await _browseProvider.ListChildrenAsync(folder.Id, _browseCredential).ConfigureAwait(true)
            : await _browseProvider
                .ListShareFolderAsync(_browseShareUrl!, folder.Id, _browseCredential)
                .ConfigureAwait(true);

        if (!listed.Success)
        {
            return;
        }

        foreach (var entry in listed.Files)
        {
            if (entry.IsFolder)
            {
                await CollectAsync(entry, Path.Combine(relative, entry.Name), into, depth + 1).ConfigureAwait(true);
            }
            else
            {
                into.Add((entry, relative));
            }
        }
    }

    private Job StartDriveDownload(CloudFile file)
    {
        // A file in the user's own drive is a cloud source with the platform's own id and no share
        // link, so the drive resolver can find it and the bytes still travel the one job path.
        var source = BuildDriveSource(file);
        return _host.DownloadJobs.Start(new DownloadJobRequest(source, _host.DownloadDirectory, file.Name));
    }

    private SoftwareSource BuildShareSource(CloudFile file) => new()
    {
        Kind = SourceKind.CloudShare,
        ProviderId = _browseProvider!.Id,
        ShareUrl = _browseShareUrl,
        FileName = file.Name,

        // The id is preferred over the path: it survives a rename or a move, and the provider that
        // owns the share is the one that can turn it back into the item.
        Locator = new ResourceLocator
        {
            Kind = file.IsFolder ? ResourceLocatorKind.Folder : ResourceLocatorKind.File,
            Name = file.Name,
            ProviderItemId = file.Id,
        },
    };

    private SoftwareSource BuildDriveSource(CloudFile file) => new()
    {
        Kind = SourceKind.CloudShare,
        ProviderId = _browseProvider!.Id,
        FileName = file.Name,
        Locator = new ResourceLocator
        {
            Kind = ResourceLocatorKind.File,
            Name = file.Name,
            ProviderItemId = file.Id,
        },
    };

    /// <summary>Formats a size the platform may not have reported at all.</summary>
    private static string FormatSize(long? bytes)
    {
        if (bytes is not long value || value <= 0)
        {
            return string.Empty;
        }

        if (value >= 1L << 30)
        {
            return (value / (double)(1L << 30)).ToString("0.#") + " GB";
        }

        return value >= 1L << 20
            ? (value / (double)(1L << 20)).ToString("0.#") + " MB"
            : (value / 1024.0).ToString("0.#") + " KB";
    }

    /// <summary>Rebuilds the resource list from the catalog, keeping the current selection when it survives.</summary>
    private void Reload()
    {
        _reloading = true;
        try
        {
            Resources.Clear();
            foreach (var software in _host.AllSoftware()
                         .Where(item => item.Sources.Count > 0 && item.IsPluginProvided)
                         .OrderBy(item => item.Name, StringComparer.CurrentCulture))
            {
                Resources.Add(new ResourceRow(software));
            }

            var previous = SelectedResource?.Software.Id;
            SelectedResource = null;
            SelectedResource = previous is null
                ? Resources.FirstOrDefault()
                : Resources.FirstOrDefault(resource => resource.Software.Id == previous);
        }
        finally
        {
            _reloading = false;
        }
    }
}

/// <summary>One step of the path the listing is showing, from the root down.</summary>
public sealed class ShareCrumb(string name, string? itemId) : ObservableObject
{
    public string Name { get; } = name;

    /// <summary>The platform's id for the folder, or null for the root of a share or a drive.</summary>
    public string? ItemId { get; } = itemId;
}

/// <summary>One resource in the left column: a piece of software and how many places it can come from.</summary>
public sealed class ResourceRow(SoftwareDefinition software) : ObservableObject
{
    public SoftwareDefinition Software { get; } = software;

    public string Name => Software.Name;

    public int SourceCount => Software.Sources.Count;

    /// <summary>True when at least one of its sources is a cloud share.</summary>
    public bool UsesCloud => Software.Sources.Any(source => source.Kind == SourceKind.CloudShare);

    /// <summary>What the left column shows: the name, and where it can come from, in plain words.</summary>
    public string Text => $"{Software.Name}  ·  {SourceCountText}";

    public string SourceCountText => SourceCount == 1 ? "1 个来源" : SourceCount + " 个来源";
}

/// <summary>One source of the selected resource, with its real availability and the reason behind it.</summary>
public sealed partial class SourceRow : ObservableObject
{
    private readonly CloudViewModel _owner;

    public SourceRow(CloudViewModel owner, SourceAvailability availability, bool isRecommended)
    {
        _owner = owner;
        Availability = availability;
        IsRecommended = isRecommended;
    }

    public SourceAvailability Availability { get; }

    /// <summary>True when the automatic choice landed on this source.</summary>
    public bool IsRecommended { get; }

    public bool IsCloud => Availability.Source.Kind == SourceKind.CloudShare;

    /// <summary>True when the share behind this source can be listed.</summary>
    public bool CanReadShare => IsCloud && !string.IsNullOrWhiteSpace(Availability.Source.ShareUrl);

    /// <summary>What this source is, in the words the page shows.</summary>
    public string Title => CloudViewModel.DescribeTitle(Availability);

    /// <summary>Whether it can be used right now, in one short phrase.</summary>
    public string StateText => Availability.Readiness switch
    {
        SourceReadiness.Ready => "现在就能用",
        SourceReadiness.NeedsSignIn => "登录后可用",
        SourceReadiness.SignInExpired => "登录已过期",
        SourceReadiness.Incomplete => "信息不全",
        _ => "这个平台接不上",
    };

    /// <summary>Why it is in that state.</summary>
    public string Reason => Availability.Summary;

    /// <summary>Size, architecture, version and whether the download can be checked.</summary>
    public string Detail
    {
        get
        {
            var source = Availability.Source;
            var parts = new List<string>();

            if (source.SizeBytes is > 0)
            {
                parts.Add(FormatSize(source.SizeBytes));
            }

            if (!string.IsNullOrWhiteSpace(source.Architecture))
            {
                parts.Add(source.Architecture!);
            }

            if (!string.IsNullOrWhiteSpace(source.Version))
            {
                parts.Add("版本 " + source.Version);
            }

            if (source.Locator is { IsEmpty: false } locator)
            {
                parts.Add("位置 " + (locator.Path ?? locator.Name ?? locator.ProviderItemId));
            }

            parts.Add(Availability.Verifiable ? "可校验" : "无法校验");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>The account this source is fetched through, when it needs one.</summary>
    public CloudProviderViewModel? Account => _owner.FindAccount(Availability);

    [RelayCommand]
    private async Task ReadShareAsync() => await _owner.ReadSourceShareAsync(this).ConfigureAwait(true);

    private static string FormatSize(long? bytes)
    {
        if (bytes is not long value || value <= 0)
        {
            return string.Empty;
        }

        if (value >= 1L << 30)
        {
            return (value / (double)(1L << 30)).ToString("0.#") + " GB";
        }

        return value >= 1L << 20
            ? (value / (double)(1L << 20)).ToString("0.#") + " MB"
            : (value / 1024.0).ToString("0.#") + " KB";
    }
}

/// <summary>One row of a share or drive listing, with a download action bound to it.</summary>
public sealed partial class ShareFileRow(CloudViewModel owner, CloudFile file, string text) : ObservableObject
{
    public CloudFile File { get; } = file;

    public string Text { get; } = text;

    /// <summary>Folders offer "enter" instead of "download".</summary>
    public bool IsFolder { get; } = file.IsFolder;

    [RelayCommand]
    private async Task DownloadAsync() => await owner.DownloadShareFileAsync(File).ConfigureAwait(true);

    [RelayCommand]
    private async Task OpenAsync() => await owner.OpenFolderAsync(File).ConfigureAwait(true);
}
