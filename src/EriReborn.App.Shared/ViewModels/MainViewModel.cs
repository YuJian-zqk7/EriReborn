using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EriReborn.App.Shared.Services;
using EriReborn.Skin;

namespace EriReborn.App.Shared.ViewModels;

/// <summary>
/// Application shell: navigation, active page and skin selection (spec 55/56).
/// Pages are plain view models; the skin decides how they are drawn.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly NavigationService _navigation;

    /// <summary>The page key currently showing, so a skin change can re-resolve its title.</summary>
    private string _currentKey = "home";

    /// <summary>Thread the shell was built on; extension page changes are marshalled back here.</summary>
    private readonly SynchronizationContext? _context;

    public MainViewModel(AppHost host, NavigationService navigation)
    {
        _host = host;
        _navigation = navigation;

        Home = new HomeViewModel(host);
        Software = new SoftwareViewModel(host);
        Environment = new EnvironmentViewModel(host);
        Cloud = new CloudViewModel(host);
        Ai = new AiViewModel(host);
        Extensions = new ExtensionViewModel(host);
        Marketplace = new MarketplaceViewModel(host);
        Plugins = new PluginsViewModel(host);
        Tutorial = new TutorialViewModel(host);
        Jobs = new JobsViewModel(host);
        Workshop = new WorkshopViewModel(host);
        Settings = new SettingsViewModel(host);
        Update = new UpdateViewModel(new RemoteContentService(host.Platform.Network, host.Paths, host.Log.For("Remote")));
        Blog = new BlogViewModel(new RemoteContentService(host.Platform.Network, host.Paths, host.Log.For("Remote")));

        BuildNavigation();

        // An extension enabled (or disabled) at runtime changes the page set; the rail follows
        // at once. Registration happens on whatever thread the loader finished on, so the rail
        // update is marshalled back to the thread this view model was built on.
        _context = SynchronizationContext.Current;
        _host.ExtensionPagesChanged += (_, _) =>
        {
            if (_context is null || SynchronizationContext.Current == _context)
            {
                UpdateExtensionNavigation();
            }
            else
            {
                _context.Post(_ => UpdateExtensionNavigation(), null);
            }
        };

        foreach (var skin in host.Skins.Available)
        {
            Skins.Add(skin);
        }

        foreach (var item in BuildMobileNavigation())
        {
            BottomNavigationItems.Add(item);
        }

        // The workshop can create or rename a skin. The picker is built here, so it has to be
        // told — otherwise a skin the user just made would only show up after a restart.
        // The workshop edits skins on disk: the picker has to be told, or a skin the user just made
        // would only show up after a restart — and if the edited skin is the one in force, the
        // interface has to re-read its words and art (spec 5).
        Workshop.SkinsChanged += (_, _) =>
        {
            ReloadSkins();
            ReapplySkin();
        };

        // The workshop asks for the skin it is editing to become the one in force: that is what
        // makes its preview the real interface under the user's own skin (spec 12/15).
        Workshop.ActivateSkinRequested += (_, id) =>
        {
            if (_host.Skins.Available.FirstOrDefault(skin => skin.Id == id) is { } wanted)
            {
                ActiveSkin = wanted;
            }
        };

        // The settings page has the same picker in a second place. It used to apply the skin to the
        // engine alone, which left the words and pictures of the shell on the old skin; routing it
        // through the shell's own property is what keeps both halves in step.
        Settings.ActivateSkinRequested += (_, id) =>
        {
            if (_host.Skins.Available.FirstOrDefault(skin => skin.Id == id) is { } wanted)
            {
                ActiveSkin = wanted;
            }
        };

        ActiveSkin = host.Skins.Active ?? host.Skins.Available.FirstOrDefault();

        // From here on a skin change is the user's doing, so it is worth remembering. The
        // assignment above is startup restoring the saved choice, not a new one.
        _persistActiveSkin = true;

        // Pages ask through the host so that a confirmation looks the same everywhere, and so a host
        // with no shell (tests) simply proceeds rather than blocking on a question nobody can answer.
        host.Confirm = AskAsync;
        _navigation.Navigated += (_, key) =>
        {
            CurrentPage = Resolve(key);
            RefreshPageChrome();
        };
        CurrentPage = Home;

        // Walkthrough first when it is still owed; the cloud question only when it is not.
        if (!ShowTutorialStep())
        {
            AskCloudBind();
        }
        Title = "EriReborn 3.0";

        // A finished walkthrough returns the user to the page they came for, and only then
        // asks the cloud question: one dialog at a time.
        Tutorial.OnboardingFinished += (_, _) =>
        {
            // Close the walkthrough first: the cloud question reuses the same overlay, and
            // opening it while the walkthrough is still up replaces the wizard mid-sentence.
            PromptOpen = false;
            _navigation.Navigate("home");
            AskCloudBind();
        };

        // Lets a developer or a smoke run open a specific page directly.
        var startPage = System.Environment.GetEnvironmentVariable("ERIREBORN_START_PAGE");
        if (!string.IsNullOrWhiteSpace(startPage))
        {
            // An explicit request wins, so a smoke run is never hijacked by the wizard.
            _navigation.Navigate(startPage!);
        }
        // Not while the walkthrough is already up as a dialog: a page carrying a second copy
        // of the same wizard means two controls driving one state, and the first one to act
        // wins — which is how the dialog got replaced mid-sentence.
        else if (!PromptOpen && !host.TutorialProgress.Current.OnboardingCompleted && host.Tutorial.Onboarding.Count > 0)
        {
            // First run opens the walkthrough rather than expecting the user to
            // find it. Its state is already persisted, so this happens once.
            _navigation.Navigate("tutorial");
        }
    }

    public HomeViewModel Home { get; }

    public SoftwareViewModel Software { get; }

    public EnvironmentViewModel Environment { get; }

    public CloudViewModel Cloud { get; }

    public AiViewModel Ai { get; }

    public ExtensionViewModel Extensions { get; }

    public MarketplaceViewModel Marketplace { get; }

    public PluginsViewModel Plugins { get; }

    public TutorialViewModel Tutorial { get; }

    public JobsViewModel Jobs { get; }

    public WorkshopViewModel Workshop { get; }

    public SettingsViewModel Settings { get; }

    public UpdateViewModel Update { get; }

    public BlogViewModel Blog { get; }

    public ObservableCollection<NavigationItem> NavigationItems { get; } = new();

    public ObservableCollection<SkinManifest> Skins { get; } = new();

    /// <summary>Flat navigation used by the mobile bottom bar (spec 8).</summary>
    public ObservableCollection<NavigationItem> BottomNavigationItems { get; } = new();

    [ObservableProperty]
    private object? _currentPage;

    [ObservableProperty]
    private string _title = "EriReborn";

    [ObservableProperty]
    private string _currentPageTitle = "Overview";

    [ObservableProperty]
    private SkinManifest? _activeSkin;

    /// <summary>
    /// True once the shell has finished restoring the skin that was in force. Only changes after
    /// that point are the user switching skins, and only those are written to the preferences.
    /// </summary>
    private bool _persistActiveSkin;

    [ObservableProperty]
    private NavigationItem? _selectedNavigationItem;

    partial void OnSelectedNavigationItemChanged(NavigationItem? value)
    {
        if (value is not null)
        {
            _navigation.Navigate(value.Key);
        }
    }

    /// <summary>
    /// Goes to a page by key — what a layout link names.
    ///
    /// <para>
    /// The bare key (<c>software</c>) and the dotted forms a link may be written with (<c>nav.software</c>,
    /// <c>page.software</c>) both work: these are typed by hand, and which table a key came from is not
    /// something the person writing a link should have to know.
    /// </para>
    ///
    /// <para>
    /// False means no page has that key, and the page stays where it is. A wrong word in a link must not
    /// move the user somewhere else — the same rule as a wrong key resolving to a visible marker instead
    /// of disappearing (spec 5).
    /// </para>
    /// </summary>
    public bool NavigateTo(string key)
    {
        if (FindNavigation(key) is not { } item)
        {
            return false;
        }

        SelectedNavigationItem = item;
        return true;
    }

    /// <summary>Finds a navigation entry by key, at any depth of the tree.</summary>
    private NavigationItem? FindNavigation(string key)
    {
        var wanted = key?.Trim();
        if (string.IsNullOrEmpty(wanted))
        {
            return null;
        }

        foreach (var prefix in new[] { "nav.", "page." })
        {
            if (wanted.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                wanted = wanted[prefix.Length..];
                break;
            }
        }

        return Find(NavigationItems, wanted);

        static NavigationItem? Find(IReadOnlyList<NavigationItem> items, string wanted)
        {
            foreach (var item in items)
            {
                if (string.Equals(item.Key, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return item;
                }

                if (Find(item.Children, wanted) is { } found)
                {
                    return found;
                }
            }

            return null;
        }
    }

    [ObservableProperty]
    private string _statusBar = string.Empty;

    // ------------------------------------------------ 询问弹窗（第二版那种）
    /// <summary>True while the shell is asking the user something.</summary>
    [ObservableProperty]
    private bool _promptOpen;

    [ObservableProperty] private string _promptTitle = string.Empty;
    [ObservableProperty] private string _promptBody = string.Empty;
    [ObservableProperty] private string _promptDontRemind = string.Empty;
    [ObservableProperty] private bool _promptDontRemindChecked;
    [ObservableProperty] private string _promptPrimaryText = string.Empty;
    [ObservableProperty] private string _promptSecondaryText = string.Empty;

    /// <summary>Optional step line, used by multi-step dialogs such as the walkthrough.</summary>
    [ObservableProperty] private string _promptStep = string.Empty;

    [ObservableProperty] private string _promptTertiaryText = string.Empty;

    // What each dialog button *is*, so the UI layer can pick the skin's art for it. Empty
    // means "no art": the button then shows its label.
    [ObservableProperty] private string _promptPrimaryKey = string.Empty;
    [ObservableProperty] private string _promptSecondaryKey = string.Empty;
    [ObservableProperty] private string _promptTertiaryKey = string.Empty;

    // Generic actions, so one dialog shell serves both a yes/no question and a wizard.
    // The cloud question sets these to its own commands; the walkthrough sets them to the
    // tutorial's next/back/skip.
    [ObservableProperty]
    private System.Windows.Input.ICommand? _promptPrimaryAction;

    [ObservableProperty]
    private System.Windows.Input.ICommand? _promptSecondaryAction;

    [ObservableProperty]
    private System.Windows.Input.ICommand? _promptTertiaryAction;

    private string? _promptFlagPath;

    /// <summary>
    /// Asks the user a yes/no question and answers whether they agreed. Pages call this through
    /// <see cref="AppHost.ConfirmAsync"/> rather than deleting or leaving on a single click.
    /// </summary>
    public Task<bool> AskAsync(string title, string body, string confirmText = "确定", string cancelText = "取消")
    {
        var completion = new TaskCompletionSource<bool>();

        PromptStep = string.Empty;
        PromptTitle = title;
        PromptBody = body;
        PromptDontRemind = string.Empty;
        PromptDontRemindChecked = false;

        PromptTertiaryText = string.Empty;
        PromptTertiaryKey = string.Empty;
        PromptTertiaryAction = null;

        PromptPrimaryText = confirmText;
        PromptPrimaryKey = string.Empty;
        PromptPrimaryAction = new RelayCommand(() =>
        {
            PromptOpen = false;
            completion.TrySetResult(true);
        });

        PromptSecondaryText = cancelText;
        PromptSecondaryKey = string.Empty;
        PromptSecondaryAction = new RelayCommand(() =>
        {
            PromptOpen = false;
            completion.TrySetResult(false);
        });

        PromptOpen = true;
        return completion.Task;
    }

    /// <summary>
    /// Asks the cloud-account question once, as a dialog with a "do not remind me" choice —
    /// the reference launcher's shape. Asking inside a page the user may never open is how a
    /// question goes unasked.
    /// </summary>
    /// <summary>
    /// The first-run walkthrough, shown as a dialog rather than as a block inside the
    /// tutorial page. A wizard the user has to navigate to is a wizard most users never see.
    /// </summary>
    /// <returns>True when the walkthrough was shown.</returns>
    private bool ShowTutorialStep()
    {
        _host.Log.Info("prompt.tutorial", $"showOnboarding={Tutorial.ShowOnboarding}; steps={Tutorial.StepCount}");

        if (!Tutorial.ShowOnboarding || Tutorial.StepCount == 0)
        {
            return false;
        }

        Tutorial.PropertyChanged += (_, _) =>
        {
            if (PromptOpen && PromptStep.Length > 0)
            {
                RefreshTutorialStep();
            }
        };

        PromptDontRemind = "不再显示（之后可以在「使用教程」里重看）";
        PromptDontRemindChecked = false;
        PromptTertiaryText = "跳过";
        PromptTertiaryKey = "skip";
        PromptSecondaryKey = "back";
        PromptTertiaryAction = Tutorial.SkipCommand;
        PromptSecondaryText = "上一步";
        PromptSecondaryAction = Tutorial.BackCommand;
        RefreshTutorialStep();
        PromptOpen = true;
        return true;
    }

    /// <summary>
    /// The walkthrough's "do not show again" box is not the cloud question's deferred choice:
    /// the step line is only ever set by the walkthrough, so a tick while it is up means the
    /// walkthrough is done being offered, and it is recorded immediately rather than when the
    /// wizard is finished — closing the app mid-wizard must not bring it back.
    /// </summary>
    partial void OnPromptDontRemindCheckedChanged(bool value)
    {
        if (value && PromptStep.Length > 0)
        {
            _host.TutorialProgress.MarkCompleted();
        }
    }

    private void RefreshTutorialStep()
    {
        PromptStep = Tutorial.StepCounter;
        PromptTitle = Tutorial.StepTitle;
        PromptBody = Tutorial.StepBody;
        PromptPrimaryText = Tutorial.CanGoForward ? "下一步" : "开始使用";
        PromptPrimaryKey = Tutorial.CanGoForward ? "next" : string.Empty;
        PromptPrimaryAction = Tutorial.NextCommand;
        PromptSecondaryAction = Tutorial.BackCommand;
        PromptSecondaryText = "上一步";
    }

    private void AskCloudBind()
    {
        var flag = Path.Combine(_host.Paths.UserDataDirectory, "cloud-bind-asked.flag");
        _host.Log.Info("prompt.cloud_bind", $"userData={_host.Paths.UserDataDirectory}; flag={flag}; exists={File.Exists(flag)}");
        if (File.Exists(flag))
        {
            return;
        }

        _promptFlagPath = flag;
        PromptTitle = "是否要绑定云盘账号？";
        PromptBody =
            "绑定后，启动器会用你的网盘登录态把安装包自动下载到本地缓存再安装，全程不用手动点。\n" +
            "不绑定的话，可以打开分享页手动把安装包下载下来。\n" +
            "五个平台地位相同，绑哪一个都行；每个平台会如实报告自己现在能不能用。";
        PromptDontRemind = "不再提醒（之后可以随时在「来源」页绑定）";
        PromptPrimaryText = "绑定账号";
        PromptSecondaryText = "暂不";
        PromptDontRemindChecked = false;
        PromptStep = string.Empty;
        PromptTertiaryText = string.Empty;
        PromptPrimaryKey = string.Empty;
        PromptSecondaryKey = string.Empty;
        PromptTertiaryKey = string.Empty;
        PromptPrimaryAction = PromptPrimaryCommand;
        PromptSecondaryAction = PromptSecondaryCommand;
        PromptTertiaryAction = null;
        PromptOpen = true;
    }

    [RelayCommand]
    private void PromptPrimary()
    {
        RememberPromptChoice();
        PromptOpen = false;
        _navigation.Navigate("cloud");
    }

    [RelayCommand]
    private void PromptSecondary()
    {
        RememberPromptChoice();
        PromptOpen = false;
    }

    private void RememberPromptChoice()
    {
        if (!PromptDontRemindChecked || _promptFlagPath is null)
        {
            return;
        }

        try
        {
            File.WriteAllText(_promptFlagPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        }
        catch (Exception ex)
        {
            _host.Log.Warn("prompt.flag", "「不再提醒」没有写成功：" + ex.Message);
        }

        _promptFlagPath = null;
    }

    /// <summary>Actions the page now showing contributes to the top bar.</summary>
    public ObservableCollection<PageAction> PageActions { get; } = new();

    /// <summary>One line under the page title, supplied by the page.</summary>
    [ObservableProperty]
    private string _currentPageSubtitle = string.Empty;

    /// <summary>
    /// Rebuilds the top bar for the page that is now showing. A page that has nothing to
    /// contribute leaves the bar with just its title, which is the honest result.
    /// </summary>
    /// <summary>
    /// True when the page lays itself out to fill the viewport and scrolls its own panes.
    /// The shell must then stop scrolling vertically, otherwise one wheel turns the whole
    /// page — list and detail together — and the panes can never move independently.
    /// </summary>
    [ObservableProperty] private bool _pageFillsViewport;

    /// <summary>
    /// True when the page's own content may be wider than the window, so the shell must lay it out from
    /// its natural width rather than stretching it to the viewport. Without this the horizontal bar
    /// reaches its end with content still off-screen: the stretched page reports the viewport as the
    /// widest it needs, so the scrollable extent never covers what it paints.
    /// </summary>
    [ObservableProperty] private bool _pageScrollsSideways;

    private void RefreshPageChrome()
    {
        PageActions.Clear();

        // A page belongs here once its own view scrolls its body: the shell then stops
        // scrolling, so the page's header stays put and its list scrolls on its own — the
        // same arrangement the tutorial and cloud pages use. The extensions page used to be
        // left out while its list was a bare last child of a DockPanel, which measured it to
        // the remaining height and clipped the rest: no overflow was reported, so the shell
        // had nothing to scroll and the last cards could not be reached.
        PageFillsViewport = CurrentPage is TutorialViewModel
            or CloudViewModel
            or ExtensionViewModel
            or ExtensionPageViewModel
            or SoftwareViewModel
            or PluginsViewModel
            or MarketplaceViewModel
            or EnvironmentViewModel
            or HomeViewModel
            or AiViewModel
            or SettingsViewModel
            or UpdateViewModel
            or BlogViewModel
            or JobsViewModel
            or WorkshopViewModel;

        // Pages whose own content is wider than a window: forms with fixed-width fields, three-column
        // editors, tables. The shell's scroll viewport stretches its content to the viewport's width, so
        // such a page reports the viewport width as the widest it needs while painting wider than that
        // inside — the horizontal bar then reaches its end with content still off-screen, which is
        // exactly the complaint. These pages are laid out from their own natural width instead, so the
        // bar covers what is really there. See ApplyPageHostHeight, which applies it.
        PageScrollsSideways = CurrentPage is WorkshopViewModel
            or PluginsViewModel
            or MarketplaceViewModel
            or SettingsViewModel
            or JobsViewModel
            or CloudViewModel
            or ExtensionViewModel
            or EnvironmentViewModel;

        OnPropertyChanged(nameof(PageScrollsSideways));

        if (CurrentPage is not IPageActions page)
        {
            CurrentPageSubtitle = string.Empty;
            return;
        }

        CurrentPageSubtitle = page.Subtitle;

        foreach (var action in page.GetPageActions())
        {
            PageActions.Add(action);
        }
    }

    /// <summary>
    /// True when the active skin targets a mobile layout. The shell swaps the
    /// sidebar for bottom navigation rather than shrinking the desktop layout
    /// (spec 8/42).
    /// </summary>
    public bool IsMobile => ActiveSkin?.IsMobile ?? false;

    /// <summary>
    /// Where the active skin puts navigation. The skin decides, not the page:
    /// a desktop skin asks for a sidebar, a mobile one for bottom tabs
    /// (spec 117/118).
    /// </summary>
    public string NavPosition =>
        ActiveSkin is { } skin && skin.Navigation.TryGetValue("position", out var position) && !string.IsNullOrWhiteSpace(position)
            ? position
            : "left";

    /// <summary>True when navigation belongs along the bottom.</summary>
    public bool IsBottomNav => string.Equals(NavPosition, "bottom", StringComparison.OrdinalIgnoreCase);

    partial void OnActiveSkinChanged(SkinManifest? value)
    {
        if (value is null)
        {
            return;
        }

        _ = _host.Skins.ApplyAsync(value);

        // Remember the choice so the next start opens with the same skin — including a user skin
        // the workshop just switched on (spec 12).
        if (_persistActiveSkin)
        {
            _host.UserConfig.SetActiveSkin(value.Id);
        }

        // Skin and Persona are independent layers, but a skin names the voice it
        // is designed to be read in, so switching skin re-selects the persona.
        _host.Personas.Activate(value.Persona);
        SkinChanged?.Invoke(this, value);
        StatusBar = $"皮肤：{value.Name}（{value.Layout}）";

        // The rail's words are the skin's, so a skin change re-derives them — and the page title
        // above them comes from the same table.
        RebuildNavigation();
        CurrentPageTitle = Word(PageTitleKey(_currentKey));

        OnPropertyChanged(nameof(MaximizeTooltip));
        OnPropertyChanged(nameof(MinimizeTooltip));
        OnPropertyChanged(nameof(CloseTooltip));
        OnPropertyChanged(nameof(IsMobile));
        OnPropertyChanged(nameof(NavPosition));
        OnPropertyChanged(nameof(IsBottomNav));
    }

    /// <summary>
    /// The wording for a text key under the skin in force. One helper so no view or navigation entry
    /// carries its own copy of the words (spec 5/6).
    /// </summary>
    private string Word(string key) => UiTexts.Resolve(ActiveSkin, key);

    private IEnumerable<NavigationItem> BuildMobileNavigation() => new[]
    {
        new NavigationItem("home", Word("nav.home")) { TextKey = "nav.home" },
        new NavigationItem("software", Word("nav.software")) { TextKey = "nav.software" },
        new NavigationItem("environment", Word("nav.environment")) { TextKey = "nav.environment" },
        new NavigationItem("cloud", Word("nav.cloud")) { TextKey = "nav.cloud" },
        new NavigationItem("extensions", Word("nav.extensions")) { TextKey = "nav.extensions" },
        new NavigationItem("marketplace", Word("nav.marketplace")) { TextKey = "nav.marketplace" },
        new NavigationItem("settings", Word("nav.settings")) { TextKey = "nav.settings" },
    };

    /// <summary>
    /// Rebuilds both rails from the current skin.
    ///
    /// <para>
    /// Entries are immutable records, so a rename cannot be pushed into an existing item — the rails
    /// are rebuilt instead, which is cheap and happens only when the skin changes.
    /// </para>
    /// </summary>
    private void RebuildNavigation()
    {
        NavigationItems.Clear();
        BuildNavigation();

        BottomNavigationItems.Clear();
        foreach (var item in BuildMobileNavigation())
        {
            BottomNavigationItems.Add(item);
        }
    }

    /// <summary>
    /// Re-reads the skin in force. The workshop writes skins on disk, and the copy in hand is the
    /// parse from before that edit — so a renamed button would keep its old word until a restart.
    /// </summary>
    public void ReapplySkin()
    {
        if (ActiveSkin is not { } current)
        {
            return;
        }

        var fresh = _host.Skins.Available.FirstOrDefault(
            skin => string.Equals(skin.Id, current.Id, StringComparison.Ordinal));

        if (fresh is not null)
        {
            // A different instance, so the property's change handler runs and re-applies everything.
            ActiveSkin = fresh;
            return;
        }

        // Same instance: the setter would not fire, so the shell is refreshed by hand.
        _ = _host.Skins.ApplyAsync(current);
        SkinChanged?.Invoke(this, current);
    }

    /// <summary>Raised so the shell can re-apply skin resources to the UI (spec 47).</summary>
    public event EventHandler<SkinManifest>? SkinChanged;

    private void BuildNavigation()
    {
        NavigationItems.Add(new NavigationItem("home", Word("nav.home")) { TextKey = "nav.home" });

        NavigationItems.Add(new NavigationItem("software", Word("nav.software"))
        {
            TextKey = "nav.software",
            Children = new NavigationItem[]
            {
                new("software:All", Word("nav.software.All"), "software", 1) { TextKey = "nav.software.All" },
                new("software:System", Word("nav.software.System"), "software", 2) { TextKey = "nav.software.System" },
                new("software:Runtime", Word("nav.software.Runtime"), "software", 3) { TextKey = "nav.software.Runtime" },
                new("software:Development", Word("nav.software.Development"), "software", 4) { TextKey = "nav.software.Development" },
                new("software:Network", Word("nav.software.Network"), "software", 5) { TextKey = "nav.software.Network" },
                new("software:Games", Word("nav.software.Games"), "software", 6) { TextKey = "nav.software.Games" },
                new("software:Graphics", Word("nav.software.Graphics"), "software", 7) { TextKey = "nav.software.Graphics" },
                new("software:Media", Word("nav.software.Media"), "software", 8) { TextKey = "nav.software.Media" },
                new("software:Office", Word("nav.software.Office"), "software", 9) { TextKey = "nav.software.Office" },
                new("software:Utility", Word("nav.software.Utility"), "software", 10) { TextKey = "nav.software.Utility" },
            },
        });

        NavigationItems.Add(new NavigationItem("environment", Word("nav.environment"))
        {
            TextKey = "nav.environment",
            Children = new NavigationItem[]
            {
                new("environment", Word("nav.environment.Runtime"), "environment", 1) { TextKey = "nav.environment.Runtime" },
            },
        });

        NavigationItems.Add(new NavigationItem("cloud", Word("nav.cloud")) { Order = 1, TextKey = "nav.cloud" });
        NavigationItems.Add(new NavigationItem("ai", Word("nav.ai")) { Order = 2, TextKey = "nav.ai" });
        NavigationItems.Add(new NavigationItem("extensions", Word("nav.extensions")) { Order = 3, TextKey = "nav.extensions" });
        NavigationItems.Add(new NavigationItem("marketplace", Word("nav.marketplace")) { Order = 3, TextKey = "nav.marketplace" });
        NavigationItems.Add(new NavigationItem("plugins", Word("nav.plugins")) { Order = 3, TextKey = "nav.plugins" });
        NavigationItems.Add(new NavigationItem("tutorial", Word("nav.tutorial")) { Order = 5, TextKey = "nav.tutorial" });
        NavigationItems.Add(new NavigationItem("jobs", Word("nav.jobs")) { Order = 6, TextKey = "nav.jobs" });
        NavigationItems.Add(new NavigationItem("workshop", Word("nav.workshop")) { Order = 4, TextKey = "nav.workshop" });
        NavigationItems.Add(new NavigationItem("settings", Word("nav.settings")) { Order = 5, TextKey = "nav.settings" });
        NavigationItems.Add(new NavigationItem("update", Word("nav.update")) { Order = 6, TextKey = "nav.update" });
        NavigationItems.Add(new NavigationItem("blog", Word("nav.blog")) { Order = 7, TextKey = "nav.blog" });

        // Pages contributed by enabled extensions are appended after the built-in
        // ones, so a reader or notes screen shows up without a code change here.
        UpdateExtensionNavigation();
    }

    /// <summary>
    /// Rebuilds the rail's extension entries from the host's current page set. Called on
    /// build and again whenever an extension registers or releases pages, which is what
    /// makes toggling an extension take effect without a restart.
    /// </summary>
    private void UpdateExtensionNavigation()
    {
        for (var i = NavigationItems.Count - 1; i >= 0; i--)
        {
            if (NavigationItems[i].TextKey?.StartsWith("nav.extension.", StringComparison.Ordinal) == true)
            {
                NavigationItems.RemoveAt(i);
            }
        }

        foreach (var page in _host.ExtensionPages)
        {
            NavigationItems.Add(new NavigationItem(page.Key, page.Title) { Order = 8, TextKey = "nav.extension." + page.Key });
        }
    }

    /// <summary>The text key a page's title is looked up under — one place, so the shell's bar and
    /// the rail beside it cannot drift apart.</summary>
    private static string PageTitleKey(string head) => head switch
    {
        "software" => "page.software",
        "environment" => "page.environment",
        "cloud" => "page.cloud",
        "ai" => "page.ai",
        "extensions" => "page.extensions",
        "marketplace" => "page.marketplace",
        "plugins" => "page.plugins",
        "tutorial" => "page.tutorial",
        "jobs" => "page.jobs",
        "workshop" => "page.workshop",
        "settings" => "page.settings",
        "update" => "page.update",
        "blog" => "page.blog",
        _ => "page.home",
    };

    private object Resolve(string key)
    {
        _host.Log.Info("nav", $"Navigate -> {key}");
        var separator = key.IndexOf(':');
        var head = separator < 0 ? key : key[..separator];
        var argument = separator < 0 ? null : key[(separator + 1)..];

        // The title is a skin word, like the rail entries beside it: a skin that calls this page
        // 软件库 has to be able to say so, which is impossible while the words are written here.
        _currentKey = head;
        CurrentPageTitle = Word(PageTitleKey(head));

        return head switch
        {
            "software" => FocusSoftware(argument),
            "environment" => Environment,
            "cloud" => Cloud,
            "ai" => Ai,
            "extensions" => Extensions,
            "marketplace" => Marketplace,
            "plugins" => Plugins,
            "tutorial" => Tutorial,
            "jobs" => Jobs,
            "workshop" => Workshop,
            "settings" => Settings,
            "update" => Update,
            "blog" => Blog,
            _ => ResolveExtensionPage(head) ?? Home,
        };
    }

    /// <summary>
    /// Looks up a navigation key among the pages extensions contributed. Returns
    /// null when no extension owns that key, so the caller falls back to Home.
    /// </summary>
    private object? ResolveExtensionPage(string key)
    {
        var page = _host.ExtensionPages.FirstOrDefault(p =>
            string.Equals(p.Key, key, StringComparison.Ordinal));
        return page is not null ? new ExtensionPageViewModel(page) : null;
    }

    private object FocusSoftware(string? categoryId)
    {
        if (!string.IsNullOrWhiteSpace(categoryId))
        {
            Software.FocusCategory(categoryId);
        }

        return Software;
    }

    [RelayCommand]
    private void Navigate(NavigationItem? item)
    {
        if (item is null)
        {
            return;
        }

        _navigation.Navigate(item.Key);
    }

    /// <summary>
    /// Mirrors the window's state, so the title bar can show the right glyph.
    /// The window pushes this; the view model does not guess.
    /// </summary>
    [ObservableProperty]
    private bool _windowMaximized;

    /// <summary>The maximise button shows "restore" once the window is maximised.</summary>
    public string MaximizeGlyph => WindowMaximized ? "❐" : "▢";

    /// <summary>A skin word, like every other label the shell draws.</summary>
    public string MaximizeTooltip => Word(WindowMaximized ? "shell.restore" : "shell.maximize");

    /// <summary>
    /// The other two window buttons name their words as well. They used to be literal strings in the
    /// view, which left their two keys read by nothing — the editor then offered an edit that changed
    /// nothing on screen.
    /// </summary>
    public string MinimizeTooltip => Word("shell.minimize");

    public string CloseTooltip => Word("shell.close");

    partial void OnWindowMaximizedChanged(bool value)
    {
        OnPropertyChanged(nameof(MaximizeGlyph));
        OnPropertyChanged(nameof(MaximizeTooltip));
    }

    /// <summary>
    /// Walks one step back through the page trail.
    ///
    /// <para>
    /// Returns false when there is nowhere to go, so the platform can decide what
    /// back means there — on Android that is leaving the app. Deliberately not a
    /// relay command: it answers a question, and turning that into a command would
    /// let a UI hide the "there is nowhere to go" answer behind a disabled button.
    /// </para>
    /// </summary>
    public bool TryGoBack() => _navigation.TryGoBack();

    [RelayCommand]
    private void Minimize() => _host.Platform.Windows.Minimize();

    [RelayCommand]
    private void ToggleMaximize() => _host.Platform.Windows.ToggleMaximize();

    [RelayCommand]
    private void Close() => _host.Platform.Windows.Close();

    public void ReportStartup()
    {
        Home.Refresh();
        StatusBar = $"平台 {_host.PlatformId} · 软件目录 {_host.Catalog.Software.Count} 条 · 皮肤 {_host.Skins.Available.Count} 套";
    }

    /// <summary>
    /// Rebuilds the skin picker from the engine's current list. The workshop adds and renames
    /// skins, and a picker that cannot show them would make "create a skin" a dead end.
    /// </summary>
    public void ReloadSkins()
    {
        // Two witnesses, read before the list is touched. The picker's SelectedItem is two-way, so
        // emptying the list makes the control write null back into ActiveSkin; and a rebuild that only
        // consulted ActiveSkin afterwards would then have nothing to restore and would fall through to
        // the first pack. The engine's own Active is what was actually applied and no control can blank
        // it, so it answers when the property cannot.
        var activeId = ActiveSkin?.Id ?? _host.Skins.Active?.Id;

        // Rebuilt in place rather than emptied: a control bound to this list answers an empty list by
        // choosing an item itself, and the interface would follow that choice.
        var available = _host.Skins.Available;

        for (var index = 0; index < available.Count; index++)
        {
            if (index < Skins.Count && string.Equals(Skins[index].Id, available[index].Id, StringComparison.Ordinal))
            {
                Skins[index] = available[index];
            }
            else if (index < Skins.Count)
            {
                Skins.Insert(index, available[index]);
            }
            else
            {
                Skins.Add(available[index]);
            }
        }

        while (Skins.Count > available.Count)
        {
            Skins.RemoveAt(Skins.Count - 1);
        }

        // Re-assigning re-applies the skin, which is what makes an edited persona take effect.
        //
        // When the skin in force is no longer in the list, it stays in force. Picking the first item
        // instead is how editing a skin used to change the whole interface to some other one: the list
        // is rebuilt on every save, and a rebuild that could not find the active skin answered with the
        // first pack it had. Saying nothing and switching is worse than keeping what the user chose.
        ActiveSkin = (activeId is null ? null : Skins.FirstOrDefault(skin => skin.Id == activeId))
            ?? ActiveSkin
            ?? _host.Skins.Active;
    }
}
