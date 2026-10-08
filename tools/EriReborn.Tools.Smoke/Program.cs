using System.Text;
using EriReborn.App.Shared;
using EriReborn.App.Shared.Services;
using EriReborn.App.Shared.ViewModels;
using EriReborn.Core.Diagnostics;
using EriReborn.Core.Logging;
using EriReborn.Platform.Windows;

namespace EriReborn.Tools.Smoke;

/// <summary>
/// Headless end-to-end smoke run: composition root → catalog → skins → assets →
/// cloud → navigation → real detection (spec 72/73). Exits non-zero if any step
/// fails, so it can gate a build.
///
/// Usage: EriReborn.Tools.Smoke [--scan]
/// </summary>
internal static class Program
{
    /// <summary>The value following a flag, or null.</summary>
    private static string? ArgumentValue(string[] args, string flag)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>
    /// How many asset sheets the shipped manifest declares. Comparing against this
    /// rather than a frozen number means adding art does not fail the check, while a
    /// loader that silently drops a sheet still does.
    /// </summary>
    private static int DeclaredAssetSheets(string assetsRoot)
    {
        var path = Path.Combine(assetsRoot, "asset_manifest.json");
        if (!File.Exists(path))
        {
            return 0;
        }

        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));

        return document.RootElement.TryGetProperty("sheets", out var array)
            && array.ValueKind == System.Text.Json.JsonValueKind.Array
                ? array.GetArrayLength()
                : 0;
    }

    /// <summary>
    /// How many official cloud-extension packages are present. The five platforms ship as extension
    /// packages, so "how many platforms does this build have" is answered by how many packages are
    /// there — the same "compare against the shipped data, not a frozen number" rule the catalog and
    /// asset checks follow.
    /// </summary>
    private static int DeclaredCloudPackages(string extensionsDirectory)
        => Directory.Exists(extensionsDirectory)
            ? Directory.EnumerateFiles(extensionsDirectory, "erireborn_cloud_*.zip").Count()
            : 0;

    /// <summary>Counts the shipped catalog data: every item, and every category it uses.</summary>
    private static (int Items, int Categories) CatalogDataTotals(string assetsRoot)
    {
        var directory = Path.Combine(assetsRoot, "catalog");
        if (!Directory.Exists(directory))
        {
            return (0, 0);
        }

        var items = 0;
        var categories = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            if (!document.RootElement.TryGetProperty("items", out var array)
                || array.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in array.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out _))
                {
                    continue;
                }

                items++;

                if (item.TryGetProperty("categoryId", out var category)
                    && category.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    categories.Add(category.GetString()!);
                }
            }
        }

        return (items, categories.Count);
    }

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // A restricted environment may refuse writes outside the workspace, so the
        // user-data directory is overridable rather than fixed to %APPDATA%.
        var paths = AppPaths.Detect(userDataOverride: ArgumentValue(args, "--user-data"));
        AppLog.MinimumLevel = LogLevel.Warn;
        var log = AppLog.For("Smoke");

        Console.WriteLine($"assets       : {paths.AssetsRoot}");
        Console.WriteLine($"user data    : {paths.UserDataDirectory}");
        Console.WriteLine();

        try
        {
            var platform = WindowsPlatformService.Create(AppLog.For("Platform"));
            var host = await AppHost.CreateAsync(paths, platform, platform.CloudRegistry, log);

            var failures = 0;
            // Compared against the shipped data rather than a frozen number, so
            // adding catalog entries does not break the check while a loader
            // regression still does.
            var (dataItems, dataCategories) = CatalogDataTotals(paths.AssetsRoot);

            failures += Check("catalog entries", host.Catalog.Software.Count, dataItems);
            failures += Check("catalog rejections", host.Catalog.Report.ItemsRejected, 0);
            failures += Check("categories", host.Catalog.UsedCategoryIds.Count(), dataCategories);
            failures += Check("skins", host.Skins.Available.Count, 6);
            failures += Check("asset sheets", host.Assets.Sheets.Count, DeclaredAssetSheets(paths.AssetsRoot));
            // The five platforms arrive as official extension packages. A source tree without them
            // has none to verify, and the honest answer is SKIP with the reason — not a count of 0
            // reported as a failure, and not a count of 0 reported as a pass. Run against a published
            // build (or with ERIREBORN_ASSETS pointing at one) to have the five verified.
            var cloudPackages = DeclaredCloudPackages(paths.ExtensionsDirectory);
            if (cloudPackages == 0)
            {
                Console.WriteLine(
                    "SKIP cloud providers: 官方云盘扩展包不在 assets/extensions（随发布版携带），本次没有可验证的网盘。");
            }
            else
            {
                failures += Check("cloud providers", host.CloudProviders.Providers.Count, cloudPackages);
            }

            Console.WriteLine();
            Console.WriteLine($"default skin : {host.Skins.Active?.Id ?? "(none)"}");
            Console.WriteLine($"extensions   : {host.Extensions.Statuses.Count} discovered at startup"
                + (host.Extensions.Statuses.Count == 0
                    ? string.Empty
                    : " -> " + string.Join(", ", host.Extensions.Statuses.Select(s => $"{s.Manifest.Id}:{s.State}"))));

            // Every first-class page must construct and resolve.
            var navigation = new NavigationService();
            var main = new MainViewModel(host, navigation);
            var pages = new[]
            {
                ("home", typeof(HomeViewModel)),
                ("software", typeof(SoftwareViewModel)),
                ("environment", typeof(EnvironmentViewModel)),
                ("cloud", typeof(CloudViewModel)),
                ("ai", typeof(AiViewModel)),
                ("extensions", typeof(ExtensionViewModel)),
                ("marketplace", typeof(MarketplaceViewModel)),
                ("workshop", typeof(WorkshopViewModel)),
                ("settings", typeof(SettingsViewModel)),
                ("update", typeof(UpdateViewModel)),
                ("blog", typeof(BlogViewModel)),
            };

            foreach (var (key, expected) in pages)
            {
                navigation.Navigate(key);
                var actual = main.CurrentPage?.GetType();
                var ok = actual == expected;
                Console.WriteLine($"{(ok ? "OK  " : "FAIL")} navigate {key,-12} -> {actual?.Name}");
                if (!ok)
                {
                    failures++;
                }
            }

            if (args.Contains("--genkey", StringComparer.OrdinalIgnoreCase))
            {
                var (publicKey, privateKey) = EriReborn.Extension.Signing.PackageSigner.CreateKeyPair();
                Console.WriteLine("-- public key: paste into assets/trust/keys.json --");
                Console.WriteLine(publicKey);
                Console.WriteLine();
                Console.WriteLine("-- private key: keep offline, never commit --");
                Console.WriteLine(privateKey);
                return 0;
            }

            if (args.Contains("--install", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine();
                Console.WriteLine("real install pipeline:");
                failures += await InstallCheck.RunAsync(host);
            }

            if (args.Contains("--realinstall", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine();
                Console.WriteLine("real install from the internet:");
                failures += await RealInstallCheck.RunAsync(
                    host,
                    executeInstalled: args.Contains("--runinstalled", StringComparer.OrdinalIgnoreCase));
            }

            if (args.Contains("--vendor", StringComparer.OrdinalIgnoreCase)
                || args.Contains("--vendor-uninstall", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine();
                Console.WriteLine("real vendor MSI (publisher signature + install + re-detect):");
                failures += await VendorInstallerCheck.RunAsync(
                    paths,
                    host.SoftwareEngine,
                    uninstall: args.Contains("--vendor-uninstall", StringComparer.OrdinalIgnoreCase));
            }

            if (args.Contains("--vendor-exe", StringComparer.OrdinalIgnoreCase)
                || args.Contains("--vendor-exe-uninstall", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine();
                Console.WriteLine("real vendor EXE (per-user: signature + install + re-detect):");
                failures += await VendorExeCheck.RunAsync(
                    host.SoftwareEngine,
                    uninstall: args.Contains("--vendor-exe-uninstall", StringComparer.OrdinalIgnoreCase));
            }

            if (args.Contains("--authenticode", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine();
                Console.WriteLine("authenticode (real files):");
                failures += AuthenticodeCheck.Run();
            }

            if (args.Contains("--catalog", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine();
                Console.WriteLine("catalog authority chain:");
                failures += await CatalogProvenanceCheck.RunAsync(paths, () => Task.FromResult(host.SoftwareEngine));
            }

            if (args.Contains("--gencatalogkey", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine();
                Console.WriteLine("generate catalog signing key:");
                failures += CatalogSigningCheck.GenerateKey(paths);
            }

            if (args.Contains("--signcatalog", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine();
                Console.WriteLine("sign the official catalog:");
                failures += CatalogSigningCheck.Sign(paths);
            }

            if (args.Contains("--ai", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine();
                Console.WriteLine("ai endpoint (live):");
                failures += await AiLiveCheck.RunAsync(
                    host,
                    paths,
                    ArgumentValue(args, "--ai-url") ?? "https://api.deepseek.com/v1",
                    ArgumentValue(args, "--ai-model") ?? "deepseek-chat",
                    ArgumentValue(args, "--ai-key") ?? Environment.GetEnvironmentVariable("ERIREBORN_AI_KEY") ?? string.Empty);
            }

            if (args.Contains("--plugin-ai", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine();
                Console.WriteLine("plugin generator (live share -> live model -> validated draft):");
                failures += await PluginGeneratorLiveCheck.RunAsync(
                    host,
                    paths,
                    ArgumentValue(args, "--ai-url") ?? "https://api.deepseek.com/v1",
                    ArgumentValue(args, "--ai-model") ?? "deepseek-chat",
                    ArgumentValue(args, "--ai-key") ?? Environment.GetEnvironmentVariable("ERIREBORN_AI_KEY") ?? string.Empty,
                    ArgumentValue(args, "--plugin-share"));
            }

            if (args.Contains("--aria2", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine();
                Console.WriteLine("aria2 download engine (real binary, local endpoint):");
                failures += await Aria2Check.RunAsync(host);
            }

            if (args.Contains("--msix", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine();
                Console.WriteLine("MSIX/Appx detection (real installed packages):");
                failures += await MsixProbeCheck.RunAsync(host);
            }

            if (args.Contains("--pnp", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine();
                Console.WriteLine("PnP detector probe:");
                failures += await PnpProbeCheck.RunAsync(host);
            }

            if (args.Contains("--environment", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine();
                Console.WriteLine("environment groups (runtime / drivers / security / system):");
                failures += await EnvironmentGroupsCheck.RunAsync(host);
            }

            if (args.Contains("--suggest", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine();
                Console.WriteLine("detection suggestions from installed software:");
                failures += await SuggestionCheck.RunAsync(host);
            }

            if (args.Contains("--signature", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine();
                Console.WriteLine("package signature / trust:");
                failures += await SignatureCheck.RunAsync(host);
            }

            if (args.Contains("--extensions", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine();
                Console.WriteLine("extension enable / disable:");
                failures += await ExtensionStateCheck.RunAsync(host);
            }

            if (args.Contains("--marketplace", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine();
                Console.WriteLine("marketplace round trip:");
                failures += await MarketplaceCheck.RunAsync(host);
            }

            if (args.Contains("--scan", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine();
                Console.WriteLine("scanning the environment with the real detector…");
                var scan = await host.ScanService.ScanAsync(host.Catalog.Software, host.Environment);
                Console.WriteLine(scan.Describe());
            }

            Console.WriteLine();
            Console.WriteLine(failures == 0
                ? "SMOKE OK"
                : $"SMOKE FAILED with {failures} problem(s)");

            return failures == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            var kind = StartupReport.Classify(ex);
            Console.Error.WriteLine($"SMOKE ERROR ({kind}): {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 2;
        }
    }

    private static int Check(string label, int actual, int expected)
    {
        var ok = actual == expected;
        Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {label,-20} {actual}" + (ok ? string.Empty : $" (expected {expected})"));
        return ok ? 0 : 1;
    }
}
