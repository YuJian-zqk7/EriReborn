using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EriReborn.App.Shared.Services;
using EriReborn.Core.Domain;
using EriReborn.Layout;
using EriReborn.Engine.Software;
using EriReborn.Persona;

namespace EriReborn.App.Shared.ViewModels;

/// <summary>
/// The overview of the user's current environment (spec 10). It shows real
/// facts from the platform and the loaded catalog, not a welcome splash.
///
/// Facts and voice are kept apart: counts, paths and versions are reported
/// objectively, while commentary comes from the active persona (spec 57).
/// </summary>
public sealed partial class HomeViewModel : ViewModelBase
{
    private readonly AppHost _host;
    private readonly long _memoryBytes;
    private bool _hasScanned;

    public HomeViewModel(AppHost host)
    {
        _host = host;
        Title = "概览";

        var info = host.Platform.SystemInfo.GetSystemInfo();
        MachineName = info.MachineName;
        UserName = info.UserName;
        OsDescription = $"{info.OsName} {info.OsVersion}";
        Architecture = info.Architecture;
        ProcessorCount = info.ProcessorCount;
        _memoryBytes = info.TotalPhysicalMemoryBytes;
        PlatformId = host.PlatformId;

        CatalogCount = host.Catalog.Software.Count;
        CategoryCount = host.Catalog.UsedCategoryIds.Count();
        SkinCount = host.Skins.Available.Count;
        AssetSheetCount = host.Assets.Sheets.Count;
        PersonaCount = host.Personas.Count;
        CloudProviderCount = host.CloudProviders.Providers.Count;
        RejectedCatalogItems = host.Catalog.Report.ItemsRejected;

        foreach (var category in host.Catalog.UsedCategoryIds)
        {
            CategoryBreakdown.Add(new CategoryCount(host.Catalog.DisplayNameFor(category), host.Catalog.InCategory(category).Count()));
        }

        // Re-word when the voice changes, and when the skin does.
        //
        // A persona line may be reworded by the skin: the override lives in the skin's own texts under a
        // persona. prefix (spec 57), so editing that line changes the skin, not the persona. Re-activating
        // the same persona pack returns early without raising Changed, so subscribing to the persona alone
        // left the page saying the old line after an edit — the edit was saved, written, and never spoken.
        host.Personas.Changed += (_, _) => ApplyPersonaWording();
        host.Skins.SkinChanged += (_, _) => ApplyPersonaWording();
        ApplyPersonaWording();

        // Subscribed once, here. It used to sit inside ApplyPersonaWording, which added one more handler
        // on every re-word — and re-wording now happens on every skin change, so the leak would grow with
        // each edit the user made.
        host.Jobs.Changed += (_, _) => RefreshCharacterVoice();

        ReloadLayout();
    }

    public string MachineName { get; }

    public string UserName { get; }

    public string OsDescription { get; }

    public string Architecture { get; }

    public int ProcessorCount { get; }

    public string PlatformId { get; }

    public int CatalogCount { get; }

    public int CategoryCount { get; }

    public int SkinCount { get; }

    public int AssetSheetCount { get; }

    public int PersonaCount { get; }

    public int CloudProviderCount { get; }

    public int RejectedCatalogItems { get; }

    public ObservableCollection<CategoryCount> CategoryBreakdown { get; } = new();

    /// <summary>The Overview page's layout: the saved document, or the default.</summary>
    public LayoutDocument Layout { get; private set; } = LayoutDefaults.Overview();

    /// <summary>Raised when the page must re-render from the document.</summary>
    public event EventHandler<LayoutDocument>? LayoutChanged;

    /// <summary>
    /// 0 until a scan has completed, 100 afterwards. The scan does not report
    /// finer progress, and inventing some would be a fake reading.
    /// </summary>
    public double ScanPercent => _hasScanned ? 100 : 0;

    /// <summary>Who is speaking. A name, not a debug line.</summary>
    [ObservableProperty]
    private string _characterTitle = string.Empty;

    /// <summary>The assistive line: how it is going, in the active persona's voice.</summary>
    [ObservableProperty]
    private string _characterVoice = string.Empty;

    /// <summary>The numbers the voice is commenting on. Always visible, never replaced.</summary>
    [ObservableProperty]
    private string _characterFact = string.Empty;

    [ObservableProperty]
    private bool _characterBusy;

    /// <summary>
    /// Picks the line from real state only: a running job, a finished scan, or
    /// nothing yet. There is no branch here that invents a mood the application has
    /// no evidence for.
    /// </summary>
    private void RefreshCharacterVoice()
    {
        var running = _host.Jobs.RunningCount;

        CharacterBusy = running > 0;
        CharacterFact = ScanSummary;

        if (running > 0)
        {
            CharacterVoice = PersonaVoice.Message(_host.Skins.Active?.Texts, _host.Personas, PersonaState.Progress, $"正在执行 {running} 个任务…").Voice;
            return;
        }

        CharacterVoice = _hasScanned
            ? PersonaVoice.Message(_host.Skins.Active?.Texts, _host.Personas, PersonaState.DetectionCompleted, ScanSummary).Voice
            : PersonaVoice.Message(_host.Skins.Active?.Texts, _host.Personas, PersonaState.Waiting, ScanSummary).Voice;
    }

    /// <summary>
    /// Re-reads the saved document, so an edit made in the Creative Workshop is
    /// what this page renders. That is what makes the layout schema real rather
    /// than a preview.
    /// </summary>
    public void ReloadLayout()
    {
        var path = Path.Combine(_host.Paths.UserDataDirectory, "layouts", "home.layout.json");

        try
        {
            Layout = File.Exists(path) ? LayoutSerializer.Load(path) : LayoutDefaults.Overview();
        }
        catch (Exception)
        {
            // A broken document must not take the page down with it.
            Layout = LayoutDefaults.Overview();
        }

        OnPropertyChanged(nameof(Layout));
        LayoutChanged?.Invoke(this, Layout);
    }

    /// <summary>Live values and actions handed to the layout renderer.</summary>
    public LayoutBindings CreateBindings() => new(
        Text: name => name switch
        {
            nameof(MachineName) => MachineName,
            nameof(UserName) => UserName,
            nameof(OsDescription) => OsDescription,
            nameof(Architecture) => Architecture,
            nameof(MemoryText) => MemoryText,
            nameof(CatalogCount) => CatalogCount.ToString(),
            nameof(CategoryCount) => CategoryCount.ToString(),
            nameof(AssetSheetCount) => AssetSheetCount.ToString(),
            nameof(CloudProviderCount) => CloudProviderCount.ToString(),
            nameof(CatalogHealth) => CatalogHealth,
            nameof(ScanProgressText) => ScanProgressText,
            nameof(ScanCommentary) => ScanCommentary,
            nameof(ScanSummary) => ScanSummary,
            _ => null,
        },
        Actions: new Dictionary<string, System.Windows.Input.ICommand>(StringComparer.Ordinal)
        {
            ["scan"] = ScanEnvironmentCommand,
        },
        Items: name => string.Equals(name, nameof(CategoryBreakdown), StringComparison.Ordinal)
            ? CategoryBreakdown.Select(c => new LayoutBoundItem(c.Name, c.Count.ToString())).ToList()
            : Array.Empty<LayoutBoundItem>(),
        Value: name => string.Equals(name, "ScanPercent", StringComparison.Ordinal) ? ScanPercent : null);

    /// <summary>Objective reading of the machine's memory; wording comes from the persona.</summary>
    [ObservableProperty]
    private string _memoryText = string.Empty;

    [ObservableProperty]
    private string _catalogHealth = string.Empty;

    /// <summary>Factual scan result. Never re-worded by a persona.</summary>
    [ObservableProperty]
    private string _scanSummary = string.Empty;

    /// <summary>The persona's remark about the scan.</summary>
    [ObservableProperty]
    private string _scanCommentary = string.Empty;

    [ObservableProperty]
    private string _scanProgressText = string.Empty;

    /// <summary>How the active persona greets the user on this page.</summary>
    [ObservableProperty]
    private string _personaGreeting = string.Empty;

    /// <summary>Which voice is currently speaking, e.g. "Eri · Eri".</summary>
    [ObservableProperty]
    private string _personaBadge = string.Empty;

    [ObservableProperty]
    private bool _scanRunning;

    [ObservableProperty]
    private int _installedCount;

    [ObservableProperty]
    private int _missingCount;

    [ObservableProperty]
    private int _unsupportedCount;

    [ObservableProperty]
    private int _unknownCount;

    /// <summary>
    /// Runs the real detector across the whole catalog. This is a genuine scan,
    /// not a cached guess (spec 10/73).
    /// </summary>
    [RelayCommand]
    private async Task ScanEnvironmentAsync()
    {
        if (ScanRunning)
        {
            return;
        }

        ScanRunning = true;

        // The progress line is a fact. The voice belongs on the commentary line.
        ScanProgressText = "准备扫描…";
        ScanCommentary = PersonaVoice.Message(_host.Skins.Active?.Texts, _host.Personas, PersonaState.Progress, "准备扫描…").Voice;

        try
        {
            var progress = new Progress<ScanProgress>(p =>
                ScanProgressText = $"{p.Completed}/{p.Total} · {p.CurrentSoftware}");

            var result = await _host.ScanService
                .ScanAsync(_host.Catalog.Software, _host.Environment, progress)
                .ConfigureAwait(true);

            InstalledCount = result.Installed;
            MissingCount = result.Missing;
            UnsupportedCount = result.Unsupported;
            UnknownCount = result.Unknown;

            _hasScanned = true;

            // The fact is the scan's own description; the voice is a separate
            // field, and the persona has no path to either the numbers or the text.
            ScanSummary = result.Describe();
            ScanCommentary = PersonaVoice.Message(_host.Skins.Active?.Texts, _host.Personas, PersonaState.DetectionCompleted, ScanSummary).Voice;
            ScanProgressText = string.Empty;
        }
        catch (Exception ex)
        {
            // Even a failure keeps its real reason: the voice sits beside it, not
            // over it.
            ScanSummary = $"扫描失败：{ex.GetType().Name}: {ex.Message}";
            ScanCommentary = PersonaVoice.Message(_host.Skins.Active?.Texts, _host.Personas, PersonaState.Error, ScanSummary).Voice;
            ScanProgressText = string.Empty;
        }
        finally
        {
            ScanRunning = false;
        }
    }

    public void Refresh() => ApplyPersonaWording();

    /// <summary>Re-derives every persona-dependent string from the current state.</summary>
    private void ApplyPersonaWording()
    {
        var personas = _host.Personas;

        // A value that could not be read stays "unknown" in the fact, and may only
        // be commented on afterwards (spec 4: never spoken of as missing).
        MemoryText = _memoryBytes <= 0
            ? PersonaVoice.Message(_host.Skins.Active?.Texts, personas, PersonaState.Unknown, "未知").Display
            : FormatBytes(_memoryBytes);

        // A greeting is not a state, so it keeps its own key. The skin may still reword it.
        PersonaGreeting = PersonaVoice.Voice(_host.Skins.Active?.Texts, personas, PersonaKeys.OverviewGreeting, "环境概览已就绪。");
        PersonaBadge = $"{personas.ActiveOrDefault.Id} · {personas.ActiveOrDefault.Name}";

        // The character card is the assistive layer: it says who is speaking and how
        // things are going, and the numbers stay beside it rather than behind it.
        CharacterTitle = personas.ActiveOrDefault.Name;
        CharacterFact = ScanSummary;
        RefreshCharacterVoice();

        // Something is happening is a real state, so the character reflects it rather
        // than posing whatever the last static line was. The subscription for it lives in the
        // constructor: adding it here made every re-word add another one.
        var catalogFact = RejectedCatalogItems == 0
            ? "已全量通过校验"
            : $"{RejectedCatalogItems} 条未通过校验";

        CatalogHealth = PersonaVoice.Message(_host.Skins.Active?.Texts, personas, RejectedCatalogItems == 0 ? PersonaState.Success : PersonaState.Warning, catalogFact).Display;

        if (!_hasScanned)
        {
            ScanSummary = "尚未扫描软件。";
            ScanCommentary = PersonaVoice.Message(_host.Skins.Active?.Texts, personas, PersonaState.Waiting, ScanSummary).Voice;
        }
        else
        {
            ScanCommentary = PersonaVoice.Message(_host.Skins.Active?.Texts, personas, PersonaState.DetectionCompleted, ScanSummary).Voice;
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "未知";
        }

        double value = bytes;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }
}

public sealed record CategoryCount(string Name, int Count);
