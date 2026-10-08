using System.Collections.ObjectModel;
using System.Net.Http.Headers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EriReborn.App.Shared.Services;
using EriReborn.Core.Domain;
using EriReborn.Engine.Ai;
using EriReborn.Engine.Software;
using EriReborn.Platform.Abstractions;

namespace EriReborn.App.Shared.ViewModels;

public sealed record AiProviderPreset(string Id, string DisplayName, string BaseUrl, string DefaultModel, bool RequiresKey);

/// <summary>One thing this build can ask a model to do, as the configuration page shows it.</summary>
public sealed record AiCapabilityPreset(AiCapabilityKind Kind, string Name, string Description);

/// <summary>
/// AI configuration, not a chat client (spec 58). The API key is stored in the
/// secure credential store, never in a skin/workshop/plugin file (spec 59).
/// </summary>
public sealed partial class AiViewModel : ViewModelBase
{
    /// <summary>The secure-store key for the API key. The literal lives with the other callers of it.</summary>
    private const string CredentialKey = AiSettingsKeys.ApiKey;

    private readonly AppHost _host;

    public AiViewModel(AppHost host)
    {
        _host = host;
        Title = "AI 接口";

        // The list is the registry's, not a list written here. A service is added by adding an
        // implementation, and what each one can really do travels with it — which is how a name in the
        // drop-down stops being a promise the code cannot keep (spec 58).
        foreach (var provider in AiProviders.BuiltIn.All)
        {
            Presets.Add(new AiProviderPreset(
                provider.Id,
                provider.DisplayName,
                provider.DefaultBaseUrl,
                provider.DefaultModel,
                provider.RequiresKey));
        }

        // The capabilities are the registry's as well, so the page cannot promise something the build
        // has no implementation for, and adding one does not mean editing this list (spec 58).
        foreach (var capability in AiService.Default.Capabilities)
        {
            Capabilities.Add(new AiCapabilityPreset(capability.Kind, capability.DisplayName, capability.Description));
        }

        CapabilitySummary = Capabilities.Count == 0
            ? "这个构建没有注册任何 AI 能力。"
            : $"已注册 {Capabilities.Count} 项能力：" + string.Join("、", Capabilities.Select(item => item.Name)) + "。";

        // Restore the last saved endpoint, so the configuration is not lost on
        // restart the way it used to be.
        var saved = host.UserConfig.Current;
        var savedProviderId = saved.AiProviderId;
        var savedBaseUrl = saved.AiBaseUrl;
        var savedModel = saved.AiModel;

        // The saved service first, so a gateway or a self-hosted endpoint keeps the protocol it was
        // configured for; the address is only a fallback for configurations written before the service
        // was recorded at all.
        SelectedPreset = (savedProviderId is { Length: > 0 }
                ? Presets.FirstOrDefault(p => string.Equals(p.Id, savedProviderId, StringComparison.Ordinal))
                : null)
            ?? (AiProviders.ForBaseUrl(savedBaseUrl) is { } byAddress
                ? Presets.FirstOrDefault(p => string.Equals(p.Id, byAddress.Id, StringComparison.Ordinal))
                : null)
            ?? Presets[0];

        // Assigned after the preset, because selecting a preset rewrites both.
        BaseUrl = savedBaseUrl is { Length: > 0 } ? savedBaseUrl : SelectedPreset.BaseUrl;
        Model = savedModel is { Length: > 0 } ? savedModel : SelectedPreset.DefaultModel;

        CredentialStoreStatus = host.Platform.Credentials.IsAvailable
            ? $"安全凭据存储可用；API Key 只保存在 {host.CredentialStoreDescription} 中。"
            : "安全凭据存储不可用；本环境无法保存 API Key。";
    }

    public ObservableCollection<AiProviderPreset> Presets { get; } = new();

    /// <summary>What this build can ask a model to do, read from the capability registry.</summary>
    public ObservableCollection<AiCapabilityPreset> Capabilities { get; } = new();

    [ObservableProperty]
    private string _capabilitySummary = string.Empty;

    /// <summary>Model ids the endpoint advertised during the last successful probe.</summary>
    public ObservableCollection<string> AvailableModels { get; } = new();

    /// <summary>Set when the configured model is not one the endpoint offers.</summary>
    [ObservableProperty]
    private string? _modelWarning;

    /// <summary>
    /// Puts a model the endpoint advertised into the field, so it never has to be retyped from the list
    /// sitting right above it.
    /// </summary>
    /// <remarks>
    /// The probe's answer used to be shown and nothing more: a list on screen that had to be copied by
    /// hand, one character at a time, into the box that decides which model every later request uses. A
    /// typo there is a request that cannot work, and the list that would have prevented it was already
    /// there.
    /// </remarks>
    [RelayCommand]
    private void UseModel(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return;
        }

        // Choosing from the endpoint's own list is the one case where the warning cannot apply, but it
        // is recomputed rather than cleared so the two stay in step if the list is re-probed later.
        Model = modelId;
        ModelWarning = EriReborn.Engine.Ai.AiModelCheck.Evaluate(Model, AvailableModels);
    }

    [ObservableProperty]
    private AiProviderPreset? _selectedPreset;

    [ObservableProperty]
    private string _baseUrl = string.Empty;

    [ObservableProperty]
    private string _model = string.Empty;

    [ObservableProperty]
    private string _apiKey = string.Empty;

    [ObservableProperty]
    private string _testResult = "尚未测试。";

    [ObservableProperty]
    private string _credentialStoreStatus = string.Empty;

    /// <summary>The AI's prose. Kept in its own field so the facts stay visible beside it.</summary>
    [ObservableProperty]
    private string _analysisText = string.Empty;

    [ObservableProperty]
    private string _analysisStatus = "尚未分析。";

    [ObservableProperty]
    private bool _isAnalyzing;

    /// <summary>Proposed changes awaiting the user's decision. Nothing here has run.</summary>
    public ObservableCollection<AiActionItemViewModel> ProposedActions { get; } = new();

    [ObservableProperty]
    private string _planStatus = "尚未提出任何变更。";

    /// <summary>What the model asked for and did not get, kept visible.</summary>
    [ObservableProperty]
    private string _planRejections = string.Empty;

    [ObservableProperty]
    private bool _isPlanning;

    public bool HasPlan => ProposedActions.Count > 0;

    /// <summary>
    /// Gathers this machine's facts and asks the configured model to comment on
    /// them.
    ///
    /// <para>
    /// The model only ever reads and writes prose. There is no path from here to an
    /// install or a setting: a model acting on a machine it does not fully
    /// understand is not a feature (spec 57/58).
    /// </para>
    /// </summary>
    [RelayCommand]
    private async Task AnalyzeEnvironmentAsync()
    {
        if (IsAnalyzing)
        {
            return;
        }

        IsAnalyzing = true;
        AnalysisText = string.Empty;

        try
        {
            AnalysisStatus = "正在收集环境事实…";
            var facts = await CollectFactsAsync().ConfigureAwait(true);
            var digest = AiEnvironmentDigestBuilder.Build(facts);

            AnalysisStatus = $"已收集 {digest.FactCount} 项事实"
                + (digest.IsTruncated ? $"（为控制长度省略 {digest.OmittedCount} 项）" : string.Empty)
                + "，正在请求分析…";

            // Through the chosen provider, not through one hard-wired protocol: a service that speaks
            // its own shape (Claude) used to be configured, tested, and then answered through OpenAI's.
            var result = await AiService.Default
                .RunAsync(
                    AiCapabilityKind.EnvironmentAnalyst,
                    new AiEndpointSettings(SelectedPreset?.Id, BaseUrl, Model, await ResolveKeyAsync().ConfigureAwait(true)),
                    new AiCapabilityInput(digest.Text),
                    _host.Platform.Network.Client)
                .ConfigureAwait(true);

            if (!result.Success)
            {
                AnalysisStatus = "分析失败：" + result.Message;
                return;
            }

            AnalysisText = result.Text!;

            // The label is part of the answer, not decoration: this text is a
            // suggestion about facts gathered elsewhere, and the facts above it are
            // what actually ran.
            AnalysisStatus = result.Message + " 以下内容由 AI 生成，仅供参考；事实以上一步收集到的清单为准。";
        }
        catch (Exception ex)
        {
            AnalysisStatus = $"分析异常：{ex.GetType().Name}：{ex.Message}";
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    /// <summary>
    /// Asks the model what it would change, and shows it.
    ///
    /// <para>
    /// <b>Nothing is executed here.</b> The model's answer is parsed against a
    /// closed list of actions and against the catalog the app actually has; what
    /// survives becomes a list the user reviews.
    /// </para>
    /// </summary>
    [RelayCommand]
    private async Task ProposeChangesAsync()
    {
        if (IsPlanning)
        {
            return;
        }

        IsPlanning = true;
        ProposedActions.Clear();
        PlanRejections = string.Empty;
        OnPropertyChanged(nameof(HasPlan));

        try
        {
            PlanStatus = "正在收集环境事实…";
            var digest = AiEnvironmentDigestBuilder.Build(await CollectFactsAsync().ConfigureAwait(true));

            PlanStatus = "正在请求变更建议…";
            var result = await AiService.Default
                .RunAsync(
                    AiCapabilityKind.EnvironmentAnalyst,
                    new AiEndpointSettings(SelectedPreset?.Id, BaseUrl, Model, await ResolveKeyAsync().ConfigureAwait(true)),
                    new AiCapabilityInput(digest.Text, EnvironmentAnalystCapability.ProposeIntent),
                    _host.Platform.Network.Client)
                .ConfigureAwait(true);

            if (!result.Success)
            {
                PlanStatus = "未能获得建议：" + result.Message;
                return;
            }

            var known = _host.Catalog.Software.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            var plan = AiActionPlanner.Parse(result.Text, known);

            foreach (var action in plan.Accepted)
            {
                var definition = _host.Catalog.Find(action.Target);
                ProposedActions.Add(new AiActionItemViewModel(action, definition?.Name ?? action.Target));
            }

            OnPropertyChanged(nameof(HasPlan));

            PlanStatus = plan.HasActions
                ? $"模型提出 {plan.Accepted.Count} 项变更，尚未执行。请逐项确认后点击「执行勾选项」。"
                : "模型没有提出可执行的变更。";

            if (plan.Rejected.Count > 0)
            {
                // Shown rather than swallowed: a plan that quietly shrank between the
                // model and the screen is one the user approves without knowing it.
                PlanRejections = "已忽略 " + plan.Rejected.Count + " 项："
                    + string.Join("；", plan.Rejected.Take(5).Select(r => $"{r.Target}（{r.Reason}）"));
            }
        }
        catch (Exception ex)
        {
            PlanStatus = $"提出建议时出错：{ex.GetType().Name}：{ex.Message}";
        }
        finally
        {
            IsPlanning = false;
        }
    }

    /// <summary>
    /// Runs only the ticked items, and only after this call — the model had no path
    /// to any of these services.
    /// </summary>
    [RelayCommand]
    private async Task ConfirmChangesAsync()
    {
        var chosen = ProposedActions.Where(item => item.IsSelected).ToList();
        if (chosen.Count == 0)
        {
            PlanStatus = "没有勾选任何变更，未做任何改动。";
            return;
        }

        IsPlanning = true;
        var done = 0;
        var problems = new List<string>();

        try
        {
            foreach (var item in chosen)
            {
                // Looked up again from the catalog: the model named an id, and the id
                // is what gets checked, never the model's description of it.
                var definition = _host.Catalog.Find(item.Action.Target);
                if (definition is null)
                {
                    problems.Add($"{item.Action.Target}（已不在清单中）");
                    continue;
                }

                try
                {
                    switch (item.Action.Kind)
                    {
                        case AiActionKind.Hint:
                            // Counted only when it reached the disk: telling the user an
                            // override was applied when it exists only in memory is the
                            // same lie, one layer up.
                            if (_host.DetectionHints.Set(definition.Id, new DetectionHint { ArpPattern = item.Action.Value }))
                            {
                                done++;
                            }
                            else
                            {
                                problems.Add($"{definition.Name}（检测方式未能写入磁盘）");
                            }

                            break;

                        case AiActionKind.Install:
                            var source = definition.Sources.FirstOrDefault();
                            if (source is null)
                            {
                                problems.Add($"{definition.Name}（没有可用来源）");
                                break;
                            }

                            var outcome = await _host.SoftwareEngine
                                .InstallAsync(definition, _host.Environment, source, null)
                                .ConfigureAwait(true);

                            if (outcome.State is InstallState.Succeeded or InstallState.AlreadyInstalled)
                            {
                                done++;
                            }
                            else
                            {
                                problems.Add($"{definition.Name}（{outcome.State}：{outcome.Message}）");
                            }

                            break;

                        default:
                            problems.Add($"{item.Action.Target}（不支持的动作）");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    problems.Add($"{definition.Name}（{ex.GetType().Name}）");
                }
            }
        }
        finally
        {
            IsPlanning = false;
        }

        ProposedActions.Clear();
        OnPropertyChanged(nameof(HasPlan));

        PlanStatus = $"已执行 {done} 项"
            + (problems.Count > 0 ? "；未完成：" + string.Join("；", problems) : "。");
    }

    /// <summary>Throws the plan away without touching anything.</summary>
    [RelayCommand]
    private void DiscardChanges()
    {
        var count = ProposedActions.Count;

        ProposedActions.Clear();
        OnPropertyChanged(nameof(HasPlan));

        PlanStatus = count == 0 ? "没有待处理的变更。" : $"已放弃 {count} 项变更，未做任何改动。";
    }

    /// <summary>
    /// The key to send: what is typed, or else the one in the secure store. Read from the store only
    /// when a provider actually needs a key, so a local server is not asked for one it never had.
    /// </summary>
    private async Task<string?> ResolveKeyAsync()
    {
        if (!string.IsNullOrWhiteSpace(ApiKey))
        {
            return ApiKey;
        }

        return SelectedPreset?.RequiresKey == true
            ? await _host.Platform.Credentials.GetAsync(CredentialKey).ConfigureAwait(true)
            : null;
    }

    private async Task<AiEnvironmentFacts> CollectFactsAsync()
    {
        var unavailable = new List<string>();

        SystemInfo? info = null;
        try
        {
            info = _host.Platform.SystemInfo.GetSystemInfo();
        }
        catch (Exception ex)
        {
            // "We could not read this" is a fact, and dropping it would let the
            // analysis reason about a machine that is not this one.
            unavailable.Add($"系统信息（{ex.GetType().Name}）");
        }

        var graphics = new List<string>();
        var storage = new List<string>();

        if (info is not null)
        {
            if (info.GraphicsAdapters is { Count: > 0 } adapters)
            {
                graphics.AddRange(adapters);
            }
            else
            {
                // Not "there is no GPU": an empty answer is a question that could not be asked.
                unavailable.Add("显卡（未能读取）");
            }

            if (info.StorageVolumes is { Count: > 0 } volumes)
            {
                foreach (var volume in volumes)
                {
                    var size = AiEnvironmentDigestBuilder.FormatBytes(volume.TotalBytes) ?? "未知";
                    var room = AiEnvironmentDigestBuilder.FormatBytes(volume.FreeBytes) ?? "未知";
                    storage.Add($"{volume.Name} {volume.Kind}，共 {size}，可用 {room}");
                }
            }
            else
            {
                unavailable.Add("存储卷（未能读取）");
            }
        }

        var installed = new List<string>();
        var missing = new List<string>();
        var unknown = new List<string>();

        var definitions = _host.Catalog.Software;
        if (definitions.Count > 0)
        {
            var scan = await _host.ScanService.ScanAsync(definitions, _host.Environment).ConfigureAwait(true);

            foreach (var definition in definitions)
            {
                if (!scan.Results.TryGetValue(definition.Id, out var detection))
                {
                    continue;
                }

                var label = string.IsNullOrWhiteSpace(detection.Version)
                    ? definition.Name
                    : $"{definition.Name} {detection.Version}";

                switch (detection.Outcome)
                {
                    case DetectionOutcome.Detected:
                        installed.Add(label);
                        break;

                    case DetectionOutcome.NotDetected:
                        missing.Add(definition.Name);
                        break;

                    default:
                        // Unsupported / Unknown / Error all mean "we could not decide",
                        // which is not the same as "not installed".
                        unknown.Add($"{definition.Name}（{detection.Outcome}）");
                        break;
                }
            }
        }

        return new AiEnvironmentFacts
        {
            // Deliberately not the machine or user name: the analysis needs to know
            // what the machine is, not who owns it.
            OperatingSystem = info is null ? null : $"{info.OsName} {info.OsVersion}",
            Architecture = info?.Architecture,
            Processor = info is null ? null : $"{info.ProcessorCount} 核",
            MemoryBytes = info?.TotalPhysicalMemoryBytes is > 0 ? info.TotalPhysicalMemoryBytes : null,
            FreeDiskBytes = FreeSpaceForInstall(info, _host.Environment.RootPath),
            Graphics = graphics,
            Storage = storage,
            Installed = installed,
            Missing = missing,
            Unknown = unknown,
            Unavailable = unavailable,
        };
    }

    /// <summary>
    /// Free space on the volume the install root sits on — the one number that decides whether an
    /// install fits. Null when that volume cannot be matched, rather than reporting some other drive's
    /// space as if it were the one that matters.
    /// </summary>
    private static long? FreeSpaceForInstall(SystemInfo? info, string installRoot)
    {
        var volumes = info?.StorageVolumes;
        if (volumes is null || volumes.Count == 0 || string.IsNullOrWhiteSpace(installRoot))
        {
            return null;
        }

        string? root;
        try
        {
            root = Path.GetPathRoot(Path.GetFullPath(installRoot));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        foreach (var volume in volumes)
        {
            if (string.Equals(volume.Name, root, StringComparison.OrdinalIgnoreCase))
            {
                return volume.FreeBytes > 0 ? volume.FreeBytes : null;
            }
        }

        return null;
    }

    /// <summary>
    /// What the selected service can really do, in its own words. A drop-down entry that is not
    /// implemented shows as exactly that, instead of as a field that cannot work (spec 10/40/58).
    /// </summary>
    [ObservableProperty]
    private string _providerNote = string.Empty;

    partial void OnSelectedPresetChanged(AiProviderPreset? value)
    {
        if (value is null)
        {
            return;
        }

        BaseUrl = value.BaseUrl;
        Model = value.DefaultModel;
        ProviderNote = AiProviders.BuiltIn.Resolve(value.Id)?.Capabilities.Note ?? string.Empty;
    }

    /// <summary>
    /// Persists the endpoint and model. These lived in the config model but were
    /// never written, so every restart silently discarded them.
    /// </summary>
    [RelayCommand]
    private void SaveSettings()
    {
        _host.UserConfig.SetAiSettings(SelectedPreset?.Id, BaseUrl?.Trim(), Model?.Trim());
        TestResult = $"已保存服务商（{SelectedPreset?.DisplayName ?? "未选"}）、Base URL 与模型。";
    }

    [RelayCommand]
    private async Task SaveKeyAsync()
    {
        if (!_host.Platform.Credentials.IsAvailable)
        {
            TestResult = "无法保存：当前环境没有安全凭据存储。";
            return;
        }

        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            TestResult = "API Key 为空，未保存。";
            return;
        }

        await _host.Platform.Credentials.SetAsync(CredentialKey, ApiKey).ConfigureAwait(true);
        TestResult = "API Key 已写入安全凭据存储。";
    }

    [RelayCommand]
    private async Task LoadKeyAsync()
    {
        var stored = await _host.Platform.Credentials.GetAsync(CredentialKey).ConfigureAwait(true);
        if (string.IsNullOrEmpty(stored))
        {
            TestResult = "凭据存储中没有已保存的 API Key。";
            return;
        }

        ApiKey = stored;
        TestResult = "已从安全凭据存储读取 API Key。";
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (string.IsNullOrWhiteSpace(BaseUrl))
        {
            TestResult = "Base URL 为空。";
            return;
        }

        var key = ApiKey;
        if (string.IsNullOrWhiteSpace(key) && SelectedPreset?.RequiresKey == true)
        {
            key = await _host.Platform.Credentials.GetAsync(CredentialKey).ConfigureAwait(true) ?? string.Empty;
        }

        if (SelectedPreset?.RequiresKey == true && string.IsNullOrWhiteSpace(key))
        {
            TestResult = "缺少 API Key，未发起请求。";
            return;
        }

        // Which service is being tested comes from the registry rather than from an assumption: Claude is
        // reached with Anthropic's own protocol and the rest with the OpenAI-compatible one, and which is
        // which is the provider's business, not this page's. A service that cannot answer says so through
        // its own result — there is no branch here for a service with nothing behind it, because offering
        // one would be the placeholder this codebase refuses to keep (spec 58/69).
        var provider = AiProviders.BuiltIn.Resolve(SelectedPreset?.Id) ?? AiProviders.OpenAiCompatible;
        ProviderNote = provider.Capabilities.Note;

        TestResult = "正在测试…";

        var probe = await provider
            .ProbeAsync(_host.Platform.Network.Client, BaseUrl, key, CancellationToken.None)
            .ConfigureAwait(true);

        // A reachable endpoint is not the same as a usable model, so show what
        // the service actually offers and say so when the typed model is absent.
        AvailableModels.Clear();
        foreach (var modelId in probe.Models)
        {
            AvailableModels.Add(modelId);
        }

        ModelWarning = EriReborn.Engine.Ai.AiModelCheck.Evaluate(Model, probe.Models);

        TestResult = probe.Models.Count > 0
            ? $"{probe.Message} 可用模型 {probe.Models.Count} 个。"
            : probe.Message;
    }
}
