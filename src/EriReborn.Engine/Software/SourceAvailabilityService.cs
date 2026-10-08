using EriReborn.Cloud;
using EriReborn.Cloud.Providers;
using EriReborn.Core.Domain;
using EriReborn.Core.Logging;

namespace EriReborn.Engine.Software;

/// <summary>
/// How ready one source is right now, in the words a person would use about it.
/// </summary>
/// <remarks>
/// The spec asks for "resource → several sources → pick one" (38-41). Deciding which
/// source to use used to be <c>Sources.FirstOrDefault()</c>, which is not a decision at
/// all: it is declaration order, and a source that needs a sign-in it cannot get looks
/// exactly as good as one that is ready to download.
/// </remarks>
public enum SourceReadiness
{
    /// <summary>Usable as it stands.</summary>
    Ready,

    /// <summary>Works once the person signs in to the platform.</summary>
    NeedsSignIn,

    /// <summary>The stored sign-in has gone stale and has to be redone.</summary>
    SignInExpired,

    /// <summary>The manifest is missing the address this kind needs.</summary>
    Incomplete,

    /// <summary>This build cannot reach that platform.</summary>
    PlatformUnavailable,
}

/// <summary>
/// One source of one resource, with its real availability and the reason behind it.
/// </summary>
/// <param name="Source">The manifest entry this describes.</param>
/// <param name="Readiness">Whether it can be used right now.</param>
/// <param name="Summary">One plain sentence saying why, shown verbatim in the UI.</param>
/// <param name="PlatformName">The cloud platform's display name, empty for non-cloud sources.</param>
/// <param name="Capability">How that platform says it obtains data, when it is a cloud source.</param>
/// <param name="Verifiable">True when the download can be checked against a hash or a size.</param>
public sealed record SourceAvailability(
    SoftwareSource Source,
    SourceReadiness Readiness,
    string Summary,
    string PlatformName,
    CloudImplementationKind? Capability,
    bool Verifiable)
{
    /// <summary>True when the source can be handed to the installer without asking for anything.</summary>
    public bool IsReady => Readiness == SourceReadiness.Ready;
}

/// <summary>
/// Reports what each source of a resource can actually do, and picks the one to use.
/// </summary>
/// <remarks>
/// Availability is read from the provider's own answers (<see cref="ICloudProvider.GetAuthState"/>
/// and <see cref="ICloudProvider.ImplementationKind"/>) rather than from whether a credential
/// happens to be on disk, so a deleted cookie cannot leave a page claiming to be connected.
/// </remarks>
public sealed class SourceAvailabilityService(CloudProviderRegistry providers, IAppLogger log)
{
    /// <summary>Assesses every source of a resource, in declaration order.</summary>
    public async Task<IReadOnlyList<SourceAvailability>> AssessAsync(
        SoftwareDefinition software,
        CancellationToken cancellationToken = default)
    {
        var results = new List<SourceAvailability>(software.Sources.Count);
        foreach (var source in software.Sources)
        {
            var assessed = await AssessAsync(source, cancellationToken).ConfigureAwait(false);
            results.Add(assessed);

            if (!assessed.IsReady)
            {
                log.Info(
                    "source.availability",
                    $"{software.Id} 的来源 {source}：{assessed.Readiness} — {assessed.Summary}");
            }
        }

        return results;
    }

    /// <summary>Assesses a single source.</summary>
    public async Task<SourceAvailability> AssessAsync(
        SoftwareSource source,
        CancellationToken cancellationToken = default)
    {
        var verifiable = !string.IsNullOrWhiteSpace(source.Sha256) || source.SizeBytes is > 0;

        if (source.Kind != SourceKind.CloudShare)
        {
            return AssessAddressable(source, verifiable);
        }

        if (string.IsNullOrWhiteSpace(source.ShareUrl))
        {
            return new SourceAvailability(
                source,
                SourceReadiness.Incomplete,
                "这条来源没有写分享链接。",
                string.Empty,
                null,
                verifiable);
        }

        var provider = providers.Resolve(source);
        if (provider is null)
        {
            return new SourceAvailability(
                source,
                SourceReadiness.PlatformUnavailable,
                $"没有接入这条来源写明的平台「{source.ProviderId}」。",
                source.ProviderId ?? string.Empty,
                null,
                verifiable);
        }

        if (provider.ImplementationKind == CloudImplementationKind.NotImplemented)
        {
            return new SourceAvailability(
                source,
                SourceReadiness.PlatformUnavailable,
                provider.LimitationNote ?? $"{provider.DisplayName} 在这个版本里还接不上。",
                provider.DisplayName,
                provider.ImplementationKind,
                verifiable);
        }

        // A share link names a tree. A source that carries only the link says where to look but not what
        // to take, so the resolve step can only look for its file name at the share root — which for the
        // official share holds folders and no files at all, so the download fails after the user has
        // already committed to it. Reporting that here is what lets the page say why up front, and lets
        // Pick() prefer a source that can really be used (spec 30).
        if (NeedsRelocation(source))
        {
            return new SourceAvailability(
                source,
                SourceReadiness.Incomplete,
                string.IsNullOrWhiteSpace(source.FileName)
                    ? "这条来源只登记了分享链接，没有登记要取哪个文件、也没有登记它在分享里的位置；需要在插件编辑器里重新定位。"
                    : $"这条来源只登记了分享链接，没有登记「{source.FileName}」在这份分享里的位置，只能按文件名在分享根目录找；需要在插件编辑器里重新定位。",
                provider.DisplayName,
                provider.ImplementationKind,
                verifiable);
        }

        var credential = provider is CloudProviderBase withStore
            ? await withStore.LoadCredentialAsync(cancellationToken).ConfigureAwait(false)
            : null;

        var state = provider.GetAuthState(credential);
        var (readiness, summary) = state switch
        {
            CloudAuthState.NotRequired =>
                (SourceReadiness.Ready, "不用登录就能取。"),
            CloudAuthState.Authenticated =>
                (SourceReadiness.Ready, $"{provider.DisplayName} 已登录，可以直接取。"),
            CloudAuthState.Expired =>
                (SourceReadiness.SignInExpired, $"{provider.DisplayName} 的登录已过期，重新登录就能用。"),
            CloudAuthState.Unsupported =>
                (SourceReadiness.PlatformUnavailable,
                    provider.LimitationNote ?? $"{provider.DisplayName} 在这个版本里还接不上。"),
            _ => (SourceReadiness.NeedsSignIn, $"先登录 {provider.DisplayName} 才能取。"),
        };

        return new SourceAvailability(source, readiness, summary, provider.DisplayName, provider.ImplementationKind, verifiable);
    }

    /// <summary>
    /// Picks the source to install from: usable before usable-after-signing, a checkable
    /// download before an uncheckable one, and a steadier way of obtaining the file before a
    /// shakier one. Ties keep declaration order, so the answer is reproducible.
    /// </summary>
    /// <remarks>
    /// The tie-breaker reads each platform's self-reported
    /// <see cref="CloudImplementationKind"/>, so it ranks how the file is obtained and never
    /// names a preferred platform. All five stay peers (30/31).
    /// </remarks>
    public static SourceAvailability? Pick(IReadOnlyList<SourceAvailability> candidates)
        => candidates
            .OrderBy(ReadinessRank)
            .ThenBy(candidate => candidate.Verifiable ? 0 : 1)
            .ThenBy(candidate => CapabilityRank(candidate.Capability))
            .FirstOrDefault();

    private static int ReadinessRank(SourceAvailability candidate) => candidate.Readiness switch
    {
        SourceReadiness.Ready => 0,
        SourceReadiness.NeedsSignIn => 1,
        SourceReadiness.SignInExpired => 2,
        SourceReadiness.Incomplete => 3,
        _ => 4,
    };

    private static int CapabilityRank(CloudImplementationKind? capability) => capability switch
    {
        CloudImplementationKind.OfficialApi => 0,
        CloudImplementationKind.HtmlParsing => 1,
        _ => 2,
    };

    /// <summary>
    /// True when the source is marked as not yet located and has no location to fall back on.
    /// </summary>
    /// <remarks>
    /// <see cref="SoftwareSource.NeedsLocatorResolution"/> is the deliberate mark the official plugin
    /// generator writes for an entry it could not find in the share. Only that mark counts here: a source
    /// that merely has no locator is left alone, because a share whose root does hold files can still
    /// resolve one by name, and calling every unlocated source incomplete would be a false alarm. A
    /// locator, when one is present, is what really decides this — the mark is for when there is none —
    /// so a stale mark beside a real locator does not hide a source that works.
    /// </remarks>
    private static bool NeedsRelocation(SoftwareSource source)
        => source.NeedsLocatorResolution && (source.Locator is null || source.Locator.IsEmpty);

    /// <summary>Sources that do not go through a cloud platform are judged on their own address.</summary>
    private static SourceAvailability AssessAddressable(SoftwareSource source, bool verifiable)
    {
        var (readiness, summary) = source.Kind switch
        {
            SourceKind.Official => (SourceReadiness.Ready, "官方目录里的条目。"),
            SourceKind.Winget when !string.IsNullOrWhiteSpace(source.WingetId) =>
                (SourceReadiness.Ready, "系统自带的软件源，不用网盘账号。"),
            SourceKind.Winget => (SourceReadiness.Incomplete, "这条来源没有写软件包编号。"),
            SourceKind.HttpUrl when !string.IsNullOrWhiteSpace(source.Url) =>
                (SourceReadiness.Ready, "官方直链，不用网盘账号。"),
            SourceKind.HttpUrl => (SourceReadiness.Incomplete, "这条来源没有写下载地址。"),
            SourceKind.Local when !string.IsNullOrWhiteSpace(source.FileName) =>
                (SourceReadiness.Ready, "本地已有的文件。"),
            SourceKind.Local => (SourceReadiness.Incomplete, "这条来源没有写文件名。"),
            _ => (SourceReadiness.Incomplete, "这条来源需要你自己指一下位置。"),
        };

        return new SourceAvailability(source, readiness, summary, string.Empty, null, verifiable);
    }
}
