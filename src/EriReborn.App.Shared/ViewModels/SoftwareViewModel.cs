using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EriReborn.App.Shared.Services;
using EriReborn.Cloud;
using EriReborn.Core.Catalog;
using EriReborn.Core.Net;
using EriReborn.Core.Domain;
using EriReborn.Engine.Software;

namespace EriReborn.App.Shared.ViewModels;

/// <summary>
/// The core software module (spec 11). Detection runs against the real
/// platform detector; nothing is inferred from a manifest alone.
/// </summary>
public sealed partial class SoftwareViewModel : ViewModelBase, IPageActions
{
    /// <summary>One line under the page title in the shell's top bar.</summary>
    public string Subtitle => Summary;

    /// <inheritdoc />
    public IReadOnlyList<PageAction> GetPageActions() => new[]
    {
        new PageAction("刷新列表", ReloadSoftwareCommand),
        new PageAction("检测所选项", DetectSelectedCommand),
        new PageAction("扫描当前列表", DetectVisibleCommand),
        new PageAction("安装 / 准备", PrepareInstallCommand, Primary: true, ActionKey: "install"),
    };

    private readonly AppHost _host;
    private readonly List<SoftwareItemViewModel> _all = new();

    public SoftwareViewModel(AppHost host)
    {
        _host = host;
        Title = "软件";

        // Never quiet about an unverified catalog: the data may be shown, but the
        // user has to know it carries no official authority (spec 12/37).
        var verdict = host.Catalog.Signature;
        CatalogIntegrityWarning = verdict.GrantsOfficialAuthority
            ? string.Empty
            : $"官方目录未通过签名校验：{verdict.Message}　"
              + $"当前 {host.Catalog.UntrustedCount} 条数据来源不明，已禁止自动安装。";

        ReloadSoftware();

        // A plugin can be imported while this page already exists, so the page has
        // to react. Without this it keeps showing the list it was built with, and
        // an imported resource only appears after navigating somewhere and back.
        host.PluginResources.Changed += OnPluginResourcesChanged;

        // Everything is shown by default; the weakest matches simply arrive
        // unticked, so the safe choice is also the visible one.
        SelectedSuggestionFilter = SuggestionFilters[0];
    }

    private void OnPluginResourcesChanged(object? sender, EventArgs e) => ReloadSoftware();

    /// <summary>
    /// Rebuilds the list from the catalog plus imported plugin resources.
    ///
    /// <para>
    /// Also on the toolbar, not only on the change event: a page that can only be refreshed by
    /// something else changing has no answer for a user who is looking at a stale list and asking
    /// it to show the truth now.
    /// </para>
    /// </summary>
    [RelayCommand]
    private void ReloadSoftware()
    {
        _all.Clear();
        foreach (var software in _host.AllSoftware()
                     .OrderBy(s => s.CategoryId, StringComparer.Ordinal)
                     .ThenBy(s => s.Name, StringComparer.Ordinal))
        {
            _all.Add(new SoftwareItemViewModel(software));
        }

        Categories.Clear();
        Categories.Add("All");

        // When the official catalog is hidden, categories come only from what
        // extensions/plugins contributed.
        var categorySource = _host.UserConfig.Current.HideBuiltInCatalog
            ? _host.PluginResources.Imported.Select(item => item.CategoryId)
            : _host.Catalog.UsedCategoryIds
                .Concat(_host.PluginResources.Imported.Select(item => item.CategoryId));

        foreach (var category in categorySource
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(id => id, StringComparer.Ordinal))
        {
            Categories.Add(category);
        }

        ApplyFilter();
        UpdateSummary();
        RebuildPluginTree();
    }

    // ------------------------------------------------ 插件资源树视图（AC-12）

    /// <summary>
    /// 插件资源树的根：每个导入插件一个根，下面按插件原本的 group/文件夹层级还原。
    /// 官方目录不进这棵树，继续留在扁平列表里。
    /// </summary>
    public ObservableCollection<SoftwareTreeNodeVm> PluginTreeRoots { get; } = new();

    /// <summary>false=扁平列表（原有视图，行为不变）；true=插件资源树。</summary>
    [ObservableProperty]
    private bool _isTreeView;

    [ObservableProperty]
    private SoftwareTreeNodeVm? _selectedTreeNode;

    /// <summary>树里没有任何插件资源时给一句实话，而不是空白面板。</summary>
    public bool HasPluginTree => PluginTreeRoots.Count > 0;

    [RelayCommand]
    private void ShowListView()
    {
        IsTreeView = false;
        ApplyFilter();
    }

    [RelayCommand]
    private void ShowTreeView()
    {
        IsTreeView = true;
        ApplyFilter();
    }

    /// <summary>
    /// 在树里选中叶子/混合文件夹分支时，同步为扁平列表语义上的「当前软件」，
    /// 于是页面顶部既有的安装计划、安装位置等卡片原样可用；纯分组与插件根不改变选择。
    /// </summary>
    partial void OnSelectedTreeNodeChanged(SoftwareTreeNodeVm? value)
    {
        if (value?.Item is { } item)
        {
            SelectedItem = item;
        }
    }

    /// <summary>
    /// 树行「安装 / 准备」按钮入口：file 叶子与 folder 分支都走同一条既有安装流程
    /// （folder 的 source 经 CloudShareResolver 打包成 zip，由安装引擎处理），
    /// 失败同样走任务状态，不在这里另造一套。
    /// </summary>
    public void PrepareNode(SoftwareTreeNodeVm? node)
    {
        if (node?.Item is not { } item)
        {
            return;
        }

        SelectedTreeNode = node;
        SelectedItem = item;
        PrepareInstall();
    }

    /// <summary>
    /// 由插件资源（<see cref="SoftwareDefinition.IsPluginProvided"/>）还原「插件根 →
    /// group/folder 分支 → file/folder 叶子」树。叶子直接复用 <see cref="_all"/> 里的行 VM，
    /// 安装/检测后的状态与扁平列表天然同步。
    /// </summary>
    private void RebuildPluginTree()
    {
        PluginTreeRoots.Clear();

        // 叶子必须复用扁平列表里的行 VM：安装/检测后的状态两个视图才能天然同步。
        var visibleItems = _all.ToDictionary(item => item.Id, StringComparer.Ordinal);

        // 结构顺序以注册表为准：导入按插件树前序写入（见 PluginTree.EnumerateResourcesWithGroupPath），
        // _all 自身按名称排序，不能用它建树，否则 Builder 树与浏览树的顺序对不上（TR-14.2）。
        // 与内置目录 id 冲突而被收起的插件条目在扁平列表里也不可见，树里同样跳过。
        var pluginGroups = _host.PluginResources.Imported
            .Where(definition => definition is { IsPluginProvided: true, CatalogId: not null }
                && visibleItems.ContainsKey(definition.Id))
            .GroupBy(definition => definition.CatalogId!, StringComparer.Ordinal);

        var roots = new List<SoftwareTreeNodeVm>();
        foreach (var group in pluginGroups)
        {
            var pluginId = group.Key;
            var root = new SoftwareTreeNodeVm(
                "plugin:" + pluginId,
                PluginRootName(pluginId),
                isPluginRoot: true,
                item: null,
                isFolderResource: false);

            // 「插件根到当前节点的显示名路径」→ 节点。同层同名分支全局只建一次：
            // 文件夹资源既是叶子又是子节点的分支时，两路挂到同一个节点上（混合节点）。
            var index = new Dictionary<NodePath, SoftwareTreeNodeVm>(NodePath.Comparer)
            {
                [NodePath.Empty] = root,
            };

            // 注册表保持导入时的树前序：父文件夹先于子节点到达，子节点层级自然稳定。
            foreach (var definition in group)
            {
                AttachToPluginTree(root, visibleItems[definition.Id], pluginId, index);
            }

            roots.Add(root);
        }

        // 只排插件根；插件内部顺序保持作者编写的树前序。
        roots.Sort((a, b) =>
        {
            var byName = string.Compare(a.Name, b.Name, StringComparison.Ordinal);
            return byName != 0 ? byName : string.Compare(a.Id, b.Id, StringComparison.Ordinal);
        });
        foreach (var root in roots)
        {
            PluginTreeRoots.Add(root);
        }

        OnPropertyChanged(nameof(HasPluginTree));
    }

    /// <summary>
    /// 懒加载：展开文件夹节点时从云盘拉取子文件列表。
    /// 复用 PluginBuilder 的 ShareTreeRow 范式：调 ICloudProvider.ListShareFolderAsync。
    /// </summary>
    internal async Task EnsureNodeChildrenAsync(SoftwareTreeNodeVm node)
    {
        var (shareUrl, providerId, providerItemId, folderPath) = node.GetCloudInfo();
        if (shareUrl is null || providerId is null)
        {
            node.LoadState = 2; // 标记为已加载（无云盘信息，不拉取）
            return;
        }

        var provider = _host.CloudProviders.Resolve(new SoftwareSource
        {
            Kind = SourceKind.CloudShare,
            ProviderId = providerId,
            ShareUrl = shareUrl,
        });

        if (provider is null)
        {
            node.LoadState = 3;
            node.LoadError = $"找不到云盘提供方：{providerId}";
            return;
        }

        var credential = await _host.Platform.Credentials
            .GetAsync(providerId, CancellationToken.None)
            .ConfigureAwait(true);

        CloudCredential? cloudCred = credential is { Length: > 0 }
            ? new CloudCredential(credential)
            : null;

        var listed = await provider
            .ListShareFolderAsync(shareUrl, providerItemId, cloudCred)
            .ConfigureAwait(true);

        if (!listed.Success)
        {
            node.LoadState = 3;
            node.LoadError = listed.Message ?? "读取失败。";
            return;
        }

        node.Children.Clear();
        foreach (var file in listed.Files)
        {
            // 为每个云盘文件创建临时 SoftwareDefinition，复用既有安装/下载链路。
            var source = new SoftwareSource
            {
                Kind = SourceKind.CloudShare,
                ProviderId = providerId,
                ShareUrl = shareUrl,
                // Path 必须是 file 所在的父 folder 的路径（来自 cloud_resource 节点的 locator.path，
                // 例如 "系统基础"），不是 "/"。ShareTreeWalker 会按这个 Path 从分享根目录一层层
                // 走到 folder，再在 folder 的 listing 里按 ProviderItemId 找到 file——
                // 给 "/" 的话 walk 只列根目录，根目录里没这个 file 的 ID，于是报"分享里没有"。
                Locator = new ResourceLocator
                {
                    Kind = file.IsFolder ? ResourceLocatorKind.Folder : ResourceLocatorKind.File,
                    Name = file.Name,
                    // cloud_resource 节点的 locator.path 是作者起的中文名（如"系统基础"），
                    // 不是云盘真实文件夹名——ShareTreeWalker 按名字在根目录找不到。
                    // 用 "by-parent/<父folderId>" 特殊 Path 让 ShareTreeWalker 走快捷路径：
                    // 直接用父 folder ID 列父 folder，再按 ProviderItemId 找 file。
                    // 父 folder ID 就是 cloud_resource 节点的 providerItemId（variable providerItemId）。
                    Path = $"by-parent/{providerItemId}",
                    ProviderItemId = file.Id,
                },
                FileName = file.Name,
                SizeBytes = file.SizeBytes,
            };

            var tempDef = new SoftwareDefinition
            {
                Id = $"cloud_{node.Id}_{file.Id}",
                Name = file.Name,
                // cloud_resource 是用户从插件资源树展开的云盘文件，不属于官方目录的任何分类。
                // 但 PathResolver 会用 ValidateOfficialCategoryId 校验 CategoryId 是否在官方注册表里——
                // 给 Uncategorized 这种自造 id 会触发 category.unknown 失败。Utility 是官方顶级分类
                // （"实用工具"），对杂项云盘文件也合理，借它通过校验而不影响显示。
                CategoryId = "Utility",
                // DirectoryName 必须是 ASCII（A-Z a-z 0-9 _），DirectoryNameValidator
                // 不允许中文/点号/空格。云盘文件名几乎都是中文，直接当目录名会触发
                // dir.not_ascii + dir.illegal_char 校验失败，根本装不了。
                // 用 file.Id（一般是云盘返回的 ASCII 短 ID）当目录名，文件原名走 Name 显示。
                DirectoryName = $"cloud_{file.Id}",
                Mode = InstallationMode.Portable,
                Trust = SoftwareTrust.Community,
                Tier = SoftwareTier.Optional,
                Sources = new[] { source },
                IsPluginProvided = true,
                // 这是用户从插件资源树展开出来的云盘文件条目，
                // 不属于官方目录也没有签名，但它是用户主动选择下载的——
                // 不设的话走默认 Untrusted，会被 SoftwareEngine 的签名校验拦下
                // （install.untrusted），让用户根本装不了。ThirdParty 允许安装但不带官方权威。
                Provenance = CatalogProvenance.ThirdParty,
            };

            var itemVm = new SoftwareItemViewModel(tempDef);
            var childNode = new SoftwareTreeNodeVm(
                $"cloud_{node.Id}_{file.Id}",
                file.Name,
                isPluginRoot: false,
                itemVm,
                isFolderResource: file.IsFolder);

            // 子文件夹也设置云盘信息，支持递归懒加载。
            if (file.IsFolder)
            {
                // folderPath 在 by-parent 形式下用不到（孙文件 Path 直接用子 folderId），
                // 传 null 即可，子文件夹展开时会用 file.Id 作为 by-parent 的 parent。
                childNode.SetCloudInfo(this, shareUrl, providerId, file.Id, null);
            }

            node.Children.Add(childNode);
        }

        node.LoadState = 2;
    }

    private string PluginRootName(string pluginId)
        => _host.PluginInstallations.Find(pluginId)?.Name is { Length: > 0 } installedName
            ? installedName
            : pluginId;

    private void AttachToPluginTree(
        SoftwareTreeNodeVm root,
        SoftwareItemViewModel item,
        string pluginId,
        IDictionary<NodePath, SoftwareTreeNodeVm> index)
    {
        var definition = item.Definition;
        var current = root;
        var path = new List<string>();

        // 祖先路径里既有 group 名也有父文件夹资源名（见 PluginTree.EnumerateResourcesWithGroupPath）。
        foreach (var segment in definition.PluginGroupPath ?? Array.Empty<string>())
        {
            path.Add(segment);
            var branchPath = new NodePath(path.ToArray());
            if (!index.TryGetValue(branchPath, out var branch))
            {
                branch = new SoftwareTreeNodeVm(
                    BranchId(pluginId, branchPath.Segments),
                    segment,
                    isPluginRoot: false,
                    item: null,
                    isFolderResource: false);
                index[branchPath] = branch;
                current.Children.Add(branch);
            }

            current = branch;
        }

        var leafPath = new NodePath(path.Append(definition.Name).ToArray());
        if (index.TryGetValue(leafPath, out var existing))
        {
            if (existing.Item is null)
            {
                // 分支占位先到：证明这一层正是一个「带孩子的文件夹资源」，补上叶子身份。
                existing.AttachItem(item, IsFolderDefinition(definition));
                // 文件夹资源节点设置云盘信息，支持懒加载展开。
                TrySetCloudInfo(existing, item.Definition);
                return;
            }

            if (string.Equals(existing.Item.Id, item.Id, StringComparison.Ordinal))
            {
                // 同一资源重复出现在快照里：忽略，不重复挂。
                return;
            }

            // 不同 id 却同名同路径，属于插件数据缺陷：不丢数据，作为独立叶子挂在父节点下。
            current.Children.Add(new SoftwareTreeNodeVm(
                "resource:" + item.Id, definition.Name, isPluginRoot: false, item, IsFolderDefinition(definition)));
            return;
        }

        var leaf = new SoftwareTreeNodeVm(
            "resource:" + item.Id,
            definition.Name,
            isPluginRoot: false,
            item,
            IsFolderDefinition(definition));
        // 文件夹资源叶子也设置云盘信息，支持懒加载展开。
        TrySetCloudInfo(leaf, item.Definition);
        index[leafPath] = leaf;
        current.Children.Add(leaf);
    }

    /// <summary>
    /// 从 SoftwareDefinition 的首个 CloudShare 来源提取 ShareUrl/ProviderId/ProviderItemId，
    /// 设置到树节点上，供懒加载展开使用。文件夹资源才设值。
    /// </summary>
    private void TrySetCloudInfo(SoftwareTreeNodeVm node, SoftwareDefinition definition)
    {
        if (!IsFolderDefinition(definition))
        {
            return;
        }

        var cloudSource = definition.Sources.FirstOrDefault(s => s.Kind == SourceKind.CloudShare);
        if (cloudSource is { ShareUrl: not null, ProviderId: not null })
        {
            // 把 locator.path 也带上：folder 资源展开后给子文件创建 source 时，
            // 子文件的 Locator.Path 必须是它父 folder 的路径，否则 ShareTreeWalker
            // 会去分享根目录找，根本找不到（见 EnsureNodeChildrenAsync 里的用法）。
            node.SetCloudInfo(this, cloudSource.ShareUrl, cloudSource.ProviderId, cloudSource.Locator?.ProviderItemId, cloudSource.Locator?.Path);
        }
    }

    private static string BranchId(string pluginId, IReadOnlyList<string> path)
        => "branch:" + pluginId + "\u001f" + string.Join("\u001f", path);

    /// <summary>与 PluginNode.ClassifyResourceKind 同一口径：任一网盘来源的 locator 指向文件夹即为文件夹资源。</summary>
    private static bool IsFolderDefinition(SoftwareDefinition definition)
        => definition.Sources.Any(source =>
            source.Kind == SourceKind.CloudShare
            && source.Locator is { Kind: ResourceLocatorKind.Folder });

    /// <summary>树路径字典键：显示名序列按序号做 ordinal 比较。</summary>
    private readonly record struct NodePath(IReadOnlyList<string> Segments)
    {
        public static NodePath Empty { get; } = new(Array.Empty<string>());

        public static IEqualityComparer<NodePath> Comparer { get; } = new PathEqualityComparer();

        private sealed class PathEqualityComparer : IEqualityComparer<NodePath>
        {
            public bool Equals(NodePath x, NodePath y)
            {
                if (x.Segments.Count != y.Segments.Count)
                {
                    return false;
                }

                for (var i = 0; i < x.Segments.Count; i++)
                {
                    if (!string.Equals(x.Segments[i], y.Segments[i], StringComparison.Ordinal))
                    {
                        return false;
                    }
                }

                return true;
            }

            public int GetHashCode(NodePath obj)
            {
                var hash = new HashCode();
                foreach (var segment in obj.Segments)
                {
                    hash.Add(segment, StringComparer.Ordinal);
                }

                return hash.ToHashCode();
            }
        }
    }

    /// <summary>Non-empty when the catalog has no official authority.</summary>
    [ObservableProperty]
    private string _catalogIntegrityWarning = string.Empty;

    public ObservableCollection<SoftwareItemViewModel> Items { get; } = new();

    public ObservableCollection<string> Categories { get; } = new();

    [ObservableProperty]
    private string _selectedCategory = "All";

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private SoftwareItemViewModel? _selectedItem;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string _detectionSummary = string.Empty;

    /// <summary>
    /// Install plan shown for confirmation before anything touches the system
    /// (spec 60: propose → preview → confirm → execute).
    /// </summary>
    [ObservableProperty]
    private PlanPreview? _installPlan;

    [ObservableProperty]
    private string _installSummary = string.Empty;

    [ObservableProperty]
    private string _installProgress = string.Empty;

    [ObservableProperty]
    private bool _installRunning;

    public bool HasInstallPlan => InstallPlan is not null;

    partial void OnInstallPlanChanged(PlanPreview? value)
    {
        OnPropertyChanged(nameof(HasInstallPlan));
        OnPropertyChanged(nameof(InstallPlanFacts));
    }

    /// <summary>The facts that belong beside the target directory in the confirmation card.</summary>
    public string InstallPlanFacts => InstallPlan is null ? string.Empty : DescribePlanFacts(InstallPlan);

    // ------------------------------------------------------- 安装到哪里（spec 25）

    private const string NoSelectionTargetText = "先选一个软件，这里会显示它将被安装到哪里。";

    /// <summary>
    /// Where the selected entry would be installed, shown before anything is pressed. Answering this
    /// only after 「准备安装」 is too late: "where is this going to land" is the question a person asks
    /// while choosing.
    /// </summary>
    [ObservableProperty]
    private string _selectedTargetDirectory = NoSelectionTargetText;

    /// <summary>Free space on that volume, whether the directory is already there, and the declared size.</summary>
    [ObservableProperty]
    private string _selectedTargetFacts = string.Empty;

    /// <summary>
    /// The card's heading, which must change with what the directory actually means: an archive really
    /// is extracted into the folder, while an installer's folder is never used. One fixed 「将安装到」
    /// over both is how users believe they chose a location the installer then ignores.
    /// </summary>
    [ObservableProperty]
    private string _selectedTargetHeading = "将安装到";

    /// <summary>
    /// One honest paragraph about what this entry's 「安装到哪里」 means for its package type: files
    /// extracted here, the vendor wizard deciding, winget deciding, or nothing auto-installing at all.
    /// </summary>
    [ObservableProperty]
    private string _selectedTargetMeaning = string.Empty;

    /// <summary>
    /// True only when the resolved directory is really where files land, so the path, the volume facts
    /// and 「更改安装位置」 are shown only when they can change anything.
    /// </summary>
    [ObservableProperty]
    private bool _selectedLocationSpecific = true;

    /// <summary>True only when changing the directory can steer this entry's real install location.</summary>
    [ObservableProperty]
    private bool _canChangeInstallPath;

    /// <summary>True when this entry's directory was chosen by the user rather than derived.</summary>
    [ObservableProperty]
    private bool _hasPathOverride;

    /// <summary>Result of the last 更改安装位置 / 恢复默认.</summary>
    [ObservableProperty]
    private string _pathOverrideStatus = string.Empty;

    /// <summary>
    /// Points one entry at a directory of the user's choosing (spec 25).
    ///
    /// <para>
    /// The official directory name is EriReborn's own bookkeeping and says nothing about where a person
    /// wants their software, so this replaces the whole resolved directory for this one id — the root
    /// and the other entries are untouched. The preference is written first and the context is derived
    /// from it, so the file on disk and the running app cannot disagree.
    /// </para>
    /// </summary>
    public void SetInstallPathForSelected(string? path)
    {
        if (SelectedItem is null)
        {
            PathOverrideStatus = "请先选择一条软件。";
            return;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            PathOverrideStatus = "没有选择目录，未做改动。";
            return;
        }

        var validation = EriReborn.Core.Validation.DirectoryNameValidator.ValidateFullPath(path);
        if (!validation.IsValid)
        {
            PathOverrideStatus = $"路径非法：{validation.Describe()}";
            return;
        }

        var preferences = _host.UserConfig.SetSoftwarePathOverride(SelectedItem.Id, path);
        _host.Environment = _host.Environment with { SoftwarePathOverrides = preferences.SoftwarePathOverrides };

        RefreshHint();
        PathOverrideStatus = $"已记住：{SelectedItem.Name} 将安装到 {path.Trim()}（立刻生效，并会记住）";
    }

    /// <summary>Drops the entry's own directory, so it follows the install root again.</summary>
    [RelayCommand]
    private void ClearInstallPathOverride()
    {
        if (SelectedItem is null)
        {
            PathOverrideStatus = "请先选择一条软件。";
            return;
        }

        var preferences = _host.UserConfig.SetSoftwarePathOverride(SelectedItem.Id, null);
        _host.Environment = _host.Environment with { SoftwarePathOverrides = preferences.SoftwarePathOverrides };

        RefreshHint();
        PathOverrideStatus = $"{SelectedItem.Name}：已恢复为按安装根目录推导的默认位置。";
    }

    /// <summary>
    /// Detection path the user can set for entries whose manifest declares no
    /// detector. Without it such entries can only ever be "unknown" (spec 22).
    /// </summary>
    [ObservableProperty]
    private string _hintPath = string.Empty;

    /// <summary>ARP display-name pattern, the highest-leverage override.</summary>
    [ObservableProperty]
    private string _hintArpPattern = string.Empty;

    [ObservableProperty]
    private string _hintStatus = "未选择软件。";

    /// <summary>Outcome of the last "recommend from installed programs" run.</summary>
    [ObservableProperty]
    private string _suggestionSummary = "尚未从已安装程序推荐。";

    /// <summary>Suggestions awaiting the user's decision. Never applied in bulk.</summary>
    public ObservableCollection<DetectionSuggestionViewModel> Suggestions { get; } = new();

    /// <summary>Confidence levels offered by the filter, strongest first.</summary>
    public IReadOnlyList<SuggestionFilterOption> SuggestionFilters { get; } = new[]
    {
        new SuggestionFilterOption("全部（含「包含」）", SuggestionConfidence.Containment),
        new SuggestionFilterOption("规范化以上", SuggestionConfidence.Normalized),
        new SuggestionFilterOption("仅精确匹配", SuggestionConfidence.Exact),
    };

    [ObservableProperty]
    private SuggestionFilterOption? _selectedSuggestionFilter;

    private readonly List<EriReborn.Engine.Software.DetectionSuggestion> _allSuggestions = new();

    partial void OnSelectedItemChanged(SoftwareItemViewModel? value) => RefreshHint();

    private void RefreshHint()
    {
        if (SelectedItem is null)
        {
            HintPath = string.Empty;
            HintArpPattern = string.Empty;
            HintStatus = "未选择软件。";
            SelectedTargetDirectory = NoSelectionTargetText;
            SelectedTargetFacts = string.Empty;
            HasPathOverride = false;
            PathOverrideStatus = string.Empty;
            return;
        }

        var existing = _host.DetectionHints.Get(SelectedItem.Id);
        HintPath = existing?.Path ?? string.Empty;
        HintArpPattern = existing?.ArpPattern ?? string.Empty;
        HintStatus = existing is null
            ? $"{SelectedItem.Id}：未设置检测方式（因此只能判定为未知）。可填 ARP 名称或检测路径。"
            : $"{SelectedItem.Id}：已设置 {existing.Describe()}";

        RefreshTargetPreview();
        PathOverrideStatus = string.Empty;
    }

    /// <summary>
    /// Re-reads where the selected entry would land, through the very resolver the installer uses —
    /// not a second guess at it, which is how a plan and an install end up in different folders.
    /// </summary>
    private void RefreshTargetPreview()
    {
        if (SelectedItem is null)
        {
            return;
        }

        var plan = _host.SoftwareEngine.Plan(SelectedItem.Definition, _host.Environment);

        HasPathOverride = _host.Environment.OverrideFor(SelectedItem.Id) is not null;
        SelectedTargetDirectory = plan.TargetDirectory;
        SelectedTargetFacts = DescribePlanFacts(plan);
        ApplyLocationSemantics(SelectedItem.Definition);
    }

    /// <summary>
    /// Kinds of honesty the target card needs. The resolved directory is only real estate for the
    /// machine for two of these — extracted/portable payloads. Installers go where their vendor
    /// decided, winget goes where winget decided, and scripts carry their own destination; showing a
    /// plausible folder for all of them used to promise control the install does not give.
    /// </summary>
    private enum LocationSemantic
    {
        ExtractHere,
        InstallerDecides,
        WingetDecides,
        ScriptDecides,
        ManualOnly,
        NoSource,
    }

    private static readonly IReadOnlySet<string> ArchiveExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".cab",
    };

    private static readonly IReadOnlySet<string> ScriptExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".ps1", ".bat", ".cmd", ".sh",
    };

    private static LocationSemantic ClassifyLocation(SoftwareDefinition software)
    {
        var source = software.Sources.FirstOrDefault();
        if (source is null)
        {
            return LocationSemantic.NoSource;
        }

        if (source.Kind == SourceKind.Winget)
        {
            return LocationSemantic.WingetDecides;
        }

        if (source.Kind == SourceKind.Manual || software.Mode == InstallationMode.Manual)
        {
            return LocationSemantic.ManualOnly;
        }

        var extension = source.FileName is { Length: > 0 } name ? Path.GetExtension(name) : string.Empty;

        if (ScriptExtensions.Contains(extension) || software.Mode == InstallationMode.Script)
        {
            return LocationSemantic.ScriptDecides;
        }

        if (ArchiveExtensions.Contains(extension))
        {
            return LocationSemantic.ExtractHere;
        }

        // A portable entry that ships as a bare file (one .exe tool) is copied into the folder, so the
        // folder is still real. An installer payload (msi/exe) — or an install-mode entry whose package
        // type we cannot name — lands wherever its own wizard decides.
        if (software.Mode == InstallationMode.Portable)
        {
            return LocationSemantic.ExtractHere;
        }

        return LocationSemantic.InstallerDecides;
    }

    /// <summary>Sets the heading/explanation/controls so the card matches what the install really does.</summary>
    private void ApplyLocationSemantics(SoftwareDefinition software)
    {
        switch (ClassifyLocation(software))
        {
            case LocationSemantic.ExtractHere:
                SelectedTargetHeading = "将安装到";
                SelectedLocationSpecific = true;
                CanChangeInstallPath = true;
                SelectedTargetMeaning =
                    "这是绿色/压缩包软件：安装时会把文件直接解压（或复制）到这个文件夹，装好后软件就在这里运行。"
                    + "默认位置按「设置里的安装根目录 \\ 分类 \\ 软件目录名」自动拼出，可以点下面的按钮换成任何文件夹。";
                break;

            case LocationSemantic.InstallerDecides:
                SelectedTargetHeading = "安装位置（由安装程序决定）";
                SelectedLocationSpecific = false;
                CanChangeInstallPath = false;
                SelectedTargetMeaning =
                    "这是带安装向导的软件（msi/exe 安装包）：真正装到哪个文件夹由厂商安装程序决定，通常在 C:\\Program Files 下；"
                    + "安装向导弹出时可以在向导里自行更改。本工具不会、也无法替安装器指定位置，"
                    + "所以下面推导出的文件夹不会被使用，「更改安装位置」对这类软件无效（安装包会先下载到设置里的下载暂存目录）。";
                break;

            case LocationSemantic.WingetDecides:
                SelectedTargetHeading = "安装位置（由 winget 决定）";
                SelectedLocationSpecific = false;
                CanChangeInstallPath = false;
                SelectedTargetMeaning =
                    "这个来源走 winget 安装：装在哪里由 winget 按该软件的默认规则决定，不能在这里指定。"
                    + "需要换位置的话，请在安装完成后用软件自身提供的方式迁移。";
                break;

            case LocationSemantic.ScriptDecides:
                SelectedTargetHeading = "安装位置（由安装脚本决定）";
                SelectedLocationSpecific = false;
                CanChangeInstallPath = false;
                SelectedTargetMeaning =
                    "这个来源是安装脚本：文件最终落在哪里由脚本内容决定，本工具不替脚本决定位置，因此推导出的文件夹仅供参考。";
                break;

            default:
                SelectedTargetHeading = "安装位置";
                SelectedLocationSpecific = false;
                CanChangeInstallPath = false;
                SelectedTargetMeaning =
                    "该条目没有可自动安装的来源（或被声明为手动安装），本工具不会执行安装，也就没有由本工具决定的安装位置；"
                    + "请按条目说明自行下载安装。";
                break;
        }
    }

    /// <summary>
    /// The facts beside a target directory. Each one is either a real reading or plainly unknown:
    /// nothing is filled in with a plausible-looking number (spec 10/151).
    /// </summary>
    private static string DescribePlanFacts(PlanPreview plan)
    {
        var parts = new List<string>
        {
            plan.FreeSpaceBytes is { } free ? $"该盘可用空间 {FormatBytes(free)}" : "该盘可用空间：无法读取",
            plan.ExpectedSizeBytes is { } size ? $"预计下载大小 {FormatBytes(size)}" : "预计下载大小：条目未声明",
            plan.TargetExists ? "目标目录已存在，同名文件会被覆盖" : "目标目录尚不存在，安装时会创建",
        };

        if (plan.FreeSpaceInsufficient)
        {
            parts.Add("可用空间小于预计下载大小，请换一个盘或先腾出空间");
        }

        if (!plan.PathValid)
        {
            parts.Add("路径未通过校验：" + (plan.PathProblem ?? "原因未说明"));
        }

        return string.Join(" · ", parts);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

    [RelayCommand]
    private async Task SaveHintAsync()
    {
        if (SelectedItem is null)
        {
            HintStatus = "请先选择一条软件。";
            return;
        }

        var path = HintPath?.Trim();
        var arp = HintArpPattern?.Trim();

        if (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(arp))
        {
            HintStatus = "请至少填写 ARP 名称或检测路径。";
            return;
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            var validation = EriReborn.Core.Validation.DirectoryNameValidator.ValidateFullPath(path!);
            if (!validation.IsValid)
            {
                HintStatus = $"路径非法：{validation.Describe()}";
                return;
            }
        }

        var hint = new EriReborn.Engine.Software.DetectionHint
        {
            Path = string.IsNullOrWhiteSpace(path) ? null : path,
            ArpPattern = string.IsNullOrWhiteSpace(arp) ? null : arp,
        };

        if (!_host.DetectionHints.Set(SelectedItem.Id, hint))
        {
            // Saying "saved" for something that did not reach the disk is the failure
            // this whole path exists to avoid.
            HintStatus = "检测方式未能写入磁盘，本次修改未生效。";
            return;
        }

        HintStatus = "已保存检测方式，正在重新检测…";
        await DetectItemAsync(SelectedItem).ConfigureAwait(true);
        HintStatus = $"已保存并重新检测：{SelectedItem.Status}";
    }

    [RelayCommand]
    private async Task ClearHintAsync()
    {
        if (SelectedItem is null)
        {
            return;
        }

        if (!_host.DetectionHints.Remove(SelectedItem.Id))
        {
            HintStatus = "清除未能写入磁盘，原有检测方式仍然有效。";
            return;
        }

        HintPath = string.Empty;
        HintArpPattern = string.Empty;
        await DetectItemAsync(SelectedItem).ConfigureAwait(true);
        HintStatus = $"已清除检测方式；当前状态 {SelectedItem.Status}";
    }

    /// <summary>
    /// Proposes detection overrides for catalog entries that declare none, using
    /// the platform's real installed-software list, and applies them in one
    /// write. Suggestions are evidence-based, not invented catalog data
    /// (spec 22).
    /// </summary>
    /// <summary>
    /// Computes suggestions and shows them. Nothing is written here: the user
    /// reviews the list and applies what they accept (spec 22).
    /// </summary>
    [RelayCommand]
    private async Task SuggestDetectionsAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (_host.Platform.Detector is not EriReborn.Platform.Abstractions.IInstalledSoftwareSource source)
        {
            SuggestionSummary = "当前平台不支持枚举已安装程序，无法推荐。";
            return;
        }

        IsBusy = true;
        try
        {
            SuggestionSummary = "正在读取已安装程序…";

            var installed = await source.ListInstalledAsync().ConfigureAwait(true);
            var undecidable = _host.Catalog.Software
                .Where(s => s.Detector is null || s.Detector.IsNone)
                .ToList();

            var suggestions = EriReborn.Engine.Software.DetectionSuggester.Suggest(undecidable, installed);

            _allSuggestions.Clear();
            _allSuggestions.AddRange(suggestions);

            if (suggestions.Count == 0)
            {
                Suggestions.Clear();
                SuggestionSummary = $"已安装 {installed.Count} 个程序，但没有找到可信的匹配。";
                return;
            }

            RefreshSuggestionView();

            SuggestionSummary =
                $"依据 {source.SourceDescription} 读取到 {installed.Count} 个程序，"
                + $"生成 {suggestions.Count} 条建议（{DescribeBreakdown(suggestions)}）。"
                + $"已勾选 {Suggestions.Count(s => s.IsSelected)} 条，确认后点击「应用勾选项」。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Applies only the suggestions the user ticked.</summary>
    [RelayCommand]
    private async Task ApplySelectedSuggestionsAsync()
    {
        var chosen = Suggestions.Where(s => s.IsSelected).ToList();
        if (chosen.Count == 0)
        {
            SuggestionSummary = "没有勾选任何建议，未做任何改动。";
            return;
        }

        var applied = _host.DetectionHints.SetMany(
            chosen.ToDictionary(s => s.Suggestion.SoftwareId, s => s.ToHint()));

        var weak = chosen.Count(s => s.IsWeak);
        SuggestionSummary = $"已应用 {applied} 条检测方式"
            + (weak > 0 ? $"（其中 {weak} 条是较弱的「包含」匹配，请留意检测结果）" : string.Empty)
            + "。";

        await DetectVisibleAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void SelectAllSuggestions()
    {
        foreach (var suggestion in Suggestions)
        {
            suggestion.IsSelected = true;
        }

        SuggestionSummary = $"已勾选全部 {Suggestions.Count} 条，确认后点击「应用勾选项」。";
    }

    [RelayCommand]
    private void SelectNoSuggestions()
    {
        foreach (var suggestion in Suggestions)
        {
            suggestion.IsSelected = false;
        }

        SuggestionSummary = "已取消全部勾选。";
    }

    [RelayCommand]
    private void ClearSuggestions()
    {
        _allSuggestions.Clear();
        Suggestions.Clear();
        SuggestionSummary = "已清空建议列表（已应用的检测方式不受影响）。";
    }

    /// <summary>
    /// Rebuilds the visible list for the selected minimum confidence.
    ///
    /// Whether an item starts ticked depends on the evidence, not on the filter:
    /// containment matches can be wrong, so they are always shown unticked the
    /// first time the list is built.
    /// </summary>
    partial void OnSelectedSuggestionFilterChanged(SuggestionFilterOption? value) => RefreshSuggestionView();

    private void RefreshSuggestionView()
    {
        var minimum = SelectedSuggestionFilter?.Minimum ?? SuggestionConfidence.Containment;
        var previouslySelected = Suggestions
            .Where(s => s.IsSelected)
            .Select(s => s.Suggestion.SoftwareId)
            .ToHashSet(StringComparer.Ordinal);

        Suggestions.Clear();

        foreach (var suggestion in _allSuggestions.Where(s => s.Confidence <= minimum))
        {
            // Keep a user's earlier tick; otherwise pre-select everything except
            // the weakest evidence.
            var selected = previouslySelected.Contains(suggestion.SoftwareId)
                || (previouslySelected.Count == 0 && suggestion.Confidence != SuggestionConfidence.Containment);

            Suggestions.Add(new DetectionSuggestionViewModel(suggestion, selected));
        }
    }

    private static string DescribeBreakdown(IReadOnlyCollection<EriReborn.Engine.Software.DetectionSuggestion> suggestions)
        => string.Join(
            "，",
            suggestions
                .GroupBy(s => s.Confidence)
                .OrderBy(g => g.Key)
                .Select(g => $"{DetectionSuggestionViewModel.Describe(g.Key)} {g.Count()}"));

    [RelayCommand]
    private void PrepareInstall()
    {
        if (SelectedItem is null)
        {
            InstallSummary = "请先选择一条软件。";
            return;
        }

        if (SelectedItem.Definition.Sources.Count == 0)
        {
            // 说清「为什么没有来源」：这类条目要么是 V1 迁移时没找到文件的内置条目，
            // 要么是只为环境检测准备的驱动/服务条目，都不是数据坏了。
            InstallSummary = "该条目没有登记任何来源（V1 迁移条目或仅用于环境检测的条目），无法安排安装；"
                + "可以在插件编辑器里为它补一个来源。";
            return;
        }

        InstallPlan = _host.SoftwareEngine.Plan(SelectedItem.Definition, _host.Environment);
        InstallSummary = InstallPlan.PathValid
            ? "请确认下方安装计划后执行。"
            : $"安装路径未通过校验：{InstallPlan.PathProblem}";
    }

    [RelayCommand]
    private void CancelInstall()
    {
        InstallPlan = null;
        InstallSummary = "已取消安装计划。";
    }

    [RelayCommand]
    private async Task ConfirmInstallAsync()
    {
        if (SelectedItem is null || InstallPlan is null || InstallRunning)
        {
            return;
        }

        var item = SelectedItem;
        var software = item.Definition;

        // The source is chosen by what can actually be fetched right now, not by the order
        // the manifest happens to list them in.
        var availability = await _host.SourceAvailability.AssessAsync(software).ConfigureAwait(true);
        var chosen = SourceAvailabilityService.Pick(availability);
        if (chosen is null)
        {
            InstallSummary = "该条目没有可用来源，无法安装。请先在插件编辑器里为它登记一个来源。";
            return;
        }

        var source = chosen.Source;

        InstallRunning = true;
        InstallSummary = "正在安装…";
        InstallProgress = string.Empty;

        // A single install is the same long work a batch install is, so it runs as a
        // job too: it appears in the task list with the transfer's own numbers, and the
        // task page's cancel reaches the download through the job's token (spec 10).
        var job = _host.Jobs.Start("install", "安装 " + software.Name, async context =>
        {
            // Progress<T> captures the UI context here because the job's first
            // synchronous stretch runs on the caller's thread — the same arrangement
            // the batch install relies on.
            var progress = new Progress<DownloadProgress>(p =>
            {
                InstallProgress = p.TotalBytes is > 0
                    ? $"{p.BytesReceived / 1024 / 1024} / {p.TotalBytes / 1024 / 1024} MB · {p.BytesPerSecond / 1024 / 1024:0.0} MB/s{(p.Eta is null ? string.Empty : " · 剩余 " + p.Eta.Value.ToString())}"
                    : $"{p.BytesReceived / 1024 / 1024} MB · {p.BytesPerSecond / 1024 / 1024:0.0} MB/s";
                context.ReportDownload(p, InstallProgress);
            });

            context.Report("正在安装…");

            var result = await _host.SoftwareEngine
                .InstallAsync(software, _host.Environment, source, progress, context.CancellationToken)
                .ConfigureAwait(true);

            // A failed result is a failed job: the reason becomes the job's error line,
            // not just a sentence on this page.
            if (result.State is not (InstallState.Succeeded or InstallState.AlreadyInstalled))
            {
                throw new InvalidOperationException(result.State + "：" + result.Message);
            }

            InstallSummary = $"{software.Name}：{result.State} — {result.Message}";

            // Re-detect so the row shows the verified state.
            await DetectItemAsync(item).ConfigureAwait(true);
        });

        await job.Completion.ConfigureAwait(true);

        InstallPlan = null;
        if (job.State == EriReborn.Core.Jobs.JobState.Failed)
        {
            InstallSummary = $"安装失败：{job.Error}";
        }
        else if (job.State == EriReborn.Core.Jobs.JobState.Cancelled)
        {
            InstallSummary = "安装已取消。";
        }

        InstallRunning = false;
        InstallProgress = string.Empty;
    }

    /// <summary>What a batch install would do, shown before anything runs.</summary>
    public ObservableCollection<SoftwareItemViewModel> BatchPlan { get; } = new();

    /// <summary>True while a plan is waiting to be confirmed.</summary>
    public bool HasBatchPlan => BatchPlan.Count > 0;

    [ObservableProperty]
    private string _batchSummary = string.Empty;

    [ObservableProperty]
    private string _batchProgress = string.Empty;

    [ObservableProperty]
    private bool _batchRunning;

    /// <summary>
    /// Collects the installable missing items into a plan. Nothing is installed
    /// until the plan is confirmed, because a batch is a bigger commitment than
    /// a single item.
    /// </summary>
    [RelayCommand]
    private void PrepareBatchInstall()
    {
        BatchPlan.Clear();

        foreach (var item in Items.Where(candidate =>
                     candidate.Status == SoftwareStatus.Missing
                     && candidate.Definition.Sources.Count > 0))
        {
            BatchPlan.Add(item);
        }

        BatchSummary = BatchPlan.Count switch
        {
            0 => "当前没有「未安装且有可用来源」的条目。",
            _ => $"将依次安装 {BatchPlan.Count} 项，逐项执行，项与项之间留出间隔。",
        };

        OnPropertyChanged(nameof(HasBatchPlan));
    }

    [RelayCommand]
    private void CancelBatchInstall()
    {
        BatchPlan.Clear();
        BatchSummary = "已取消批量安装计划。";
        OnPropertyChanged(nameof(HasBatchPlan));
    }

    [RelayCommand]
    private Task ConfirmBatchInstallAsync()
    {
        if (BatchRunning || BatchPlan.Count == 0)
        {
            return Task.CompletedTask;
        }

        var targets = BatchPlan.ToList();
        BatchPlan.Clear();
        OnPropertyChanged(nameof(HasBatchPlan));
        BatchRunning = true;

        // A batch install is long work, so it runs as a job: it shows up in the
        // job list, it can be cancelled, and it always reaches a terminal state
        // instead of leaving the page busy forever (spec 10).
        var job = _host.Jobs.Start("install", "批量安装 " + targets.Count + " 项", async context =>
        {
            try
            {
                // One at a time, with a pause between items: installers show their
                // own UI and must not overlap, and running them as fast as the loop
                // allows is what makes a batch look like a burst (spec 37).
                var runner = new BatchRunner(
                    new BatchOptions(MaxConcurrency: 1, Spacing: TimeSpan.FromSeconds(1)),
                    _host.Log);

                // Progress<T> was created here, so its callbacks come back on the UI
                // thread this state is bound to; the job is told separately.
                var progress = new Progress<(int Completed, int Total)>(value =>
                {
                    BatchProgress = value.Completed + " / " + value.Total;
                    context.Report(value.Completed, value.Total);
                });

                var results = await runner
                    .RunAsync(
                        targets,
                        async (item, token) =>
                        {
                            var availability = await _host.SourceAvailability
                                .AssessAsync(item.Definition, token)
                                .ConfigureAwait(true);
                            var chosen = SourceAvailabilityService.Pick(availability)
                                ?? throw new InvalidOperationException("该条目没有可用来源。");

                            var installed = await _host.SoftwareEngine
                                .InstallAsync(item.Definition, _host.Environment, chosen.Source, null, token)
                                .ConfigureAwait(true);

                            if (installed.State is not (InstallState.Succeeded or InstallState.AlreadyInstalled))
                            {
                                // The reason travels with the item instead of being
                                // flattened into one number.
                                throw new InvalidOperationException(installed.State + "：" + installed.Message);
                            }
                        },
                        progress,
                        context.CancellationToken)
                    .ConfigureAwait(true);

                var succeeded = results.Count(result => result.Succeeded);
                var failures = results
                    .Where(result => !result.Succeeded)
                    .Select(result => result.Item.Name + "（" + result.Error + "）")
                    .Take(3)
                    .ToList();

                BatchSummary = "批量安装完成：成功 " + succeeded + " / 共 " + results.Count + " 项。"
                    + (failures.Count > 0 ? "未成功：" + string.Join("；", failures) : string.Empty);

                // Re-detect what was attempted, so the rows show the verified state.
                foreach (var item in targets)
                {
                    await DetectItemAsync(item).ConfigureAwait(true);
                }
            }
            catch (Exception ex)
            {
                BatchSummary = "批量安装异常：" + ex.GetType().Name + ": " + ex.Message;

                // Rethrown so the job records the failure rather than success.
                throw;
            }
            finally
            {
                BatchRunning = false;
                BatchProgress = string.Empty;
            }
        });

        // The command still finishes when the work does, so callers keep their old
        // contract while the job makes the work visible and cancellable.
        return job.Completion;
    }

    partial void OnSelectedCategoryChanged(string value) => ApplyFilter();

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    public void FocusCategory(string categoryId)
    {
        if (Categories.Contains(categoryId))
        {
            SelectedCategory = categoryId;
        }
        else
        {
            SelectedCategory = "All";
        }
    }

    private void ApplyFilter()
    {
        Items.Clear();
        var query = _all.AsEnumerable();

        // 列表视图只显示插件资源，不显示官方预置目录。
        query = query.Where(i => i.Definition.IsPluginProvided);

        if (!string.Equals(SelectedCategory, "All", StringComparison.Ordinal))
        {
            query = query.Where(i => string.Equals(i.CategoryId, SelectedCategory, StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            query = query.Where(i =>
                i.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                || i.Id.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                || i.DirectoryName.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var item in query)
        {
            Items.Add(item);
        }
    }

    private void UpdateSummary()
    {
        // An empty list has two possible causes and they are not the same thing, so the page names
        // the one in force instead of leaving the user to guess why it is empty.
        if (_all.Count == 0 && _host.OfficialCatalogWithheld)
        {
            Summary = _host.UserConfig.Current.HideBuiltInCatalog
                ? "内置官方目录已隐藏（在设置里可以打开），当前没有插件或扩展贡献的软件。"
                : "官方插件已禁用，官方清单随之撤下，其它插件没有贡献软件。到「插件」页启用官方插件即可恢复。";
            return;
        }

        Summary = $"已加载 {_all.Count} 条，当前显示 {Items.Count} 条。";
    }

    [RelayCommand]
    private async Task DetectSelectedAsync()
    {
        if (SelectedItem is null)
        {
            DetectionSummary = "请先选择一条软件。";
            return;
        }

        await DetectItemAsync(SelectedItem).ConfigureAwait(true);
        DetectionSummary = $"{SelectedItem.Name}: {SelectedItem.Status} {SelectedItem.DetectedVersion}";
    }

    [RelayCommand]
    private Task DetectVisibleAsync()
    {
        if (IsBusy)
        {
            return Task.CompletedTask;
        }

        IsBusy = true;
        var targets = Items.ToList();

        // A scan touches the whole machine, so it runs as a job: visible while it
        // runs and cancellable in the middle (spec 10).
        var job = _host.Jobs.Start("scan", "检测 " + targets.Count + " 项", async context =>
        {
            try
            {
                var definitions = targets.Select(t => t.Definition).ToList();

                // Progress<T> was created here, so its callbacks arrive on the UI
                // thread; the job is told separately.
                var progress = new Progress<ScanProgress>(value =>
                    context.Report(value.Completed, value.Total));

                var result = await _host.ScanService
                    .ScanAsync(definitions, _host.Environment, progress, context.CancellationToken)
                    .ConfigureAwait(true);

                foreach (var item in targets)
                {
                    if (result.Results.TryGetValue(item.Id, out var detection))
                    {
                        item.Status = detection.ToStatus();
                        item.DetectedVersion = detection.Version;
                        item.StatusDetail = detection.Detail ?? detection.Source;
                    }
                }

                DetectionSummary = result.Describe();
            }
            finally
            {
                IsBusy = false;
            }
        });

        // The command still finishes when the work does, so callers keep their old
        // contract while the job makes the work visible and cancellable.
        return job.Completion;
    }

    private async Task DetectItemAsync(SoftwareItemViewModel item)
    {
        try
        {
            var result = await _host.SoftwareEngine.DetectAsync(item.Definition).ConfigureAwait(true);
            item.Status = result.ToStatus();
            item.DetectedVersion = result.Version;
            item.StatusDetail = result.Detail ?? result.Source;
        }
        catch (Exception ex)
        {
            item.Status = SoftwareStatus.Failed;
            item.StatusDetail = ex.Message;
        }
    }
}
