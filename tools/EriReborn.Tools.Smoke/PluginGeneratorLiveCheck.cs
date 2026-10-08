using System.Text.Json;
using EriReborn.App.Shared;
using EriReborn.App.Shared.Services;
using EriReborn.Cloud;
using EriReborn.Cloud.Providers;
using EriReborn.Core.Domain;
using EriReborn.Engine.Ai;
using EriReborn.Engine.Plugins;

namespace EriReborn.Tools.Smoke;

/// <summary>
/// Runs the plugin generator the way the 插件 page runs it: a real platform is read first, a real
/// model is shown only what it returned, and the answer has to survive
/// <see cref="PluginDraftValidator"/> before any of it counts as a draft.
///
/// <para>
/// The reason this is done live, with a real share and a real model, is the failure it catches: a
/// perfectly good JSON object wrapped in a sentence or a code fence used to be cut at the first brace
/// by a first-brace-to-last-brace extraction, and the draft came back empty. Only a real answer has
/// that shape — a stub agrees with whatever the code happens to do, which is exactly how the bug
/// survived a green suite.
/// </para>
/// </summary>
internal static class PluginGeneratorLiveCheck
{
    public static async Task<int> RunAsync(
        AppHost host,
        AppPaths paths,
        string baseUrl,
        string model,
        string key,
        string? shareUrl)
    {
        var failures = 0;

        if (string.IsNullOrWhiteSpace(key))
        {
            Console.WriteLine("SKIP plugin generator: no key supplied, so no model was asked.");
            Console.WriteLine("     pass one with --ai-key <key>, or set ERIREBORN_AI_KEY.");
            return 0;
        }

        var share = string.IsNullOrWhiteSpace(shareUrl) ? DefaultShare(paths.AssetsRoot) : shareUrl.Trim();
        if (string.IsNullOrWhiteSpace(share))
        {
            Console.WriteLine("SKIP plugin generator: no share link, and the shipped locator table names none.");
            Console.WriteLine("     pass one with --plugin-share <url>.");
            return 0;
        }

        Console.WriteLine($"      share: {share}");

        // 1) The platform first. The model is shown the result of this call and nothing else, which is
        //    the order the capability is built on: a model asked before the tree exists can only invent
        //    one, and an invented tree is a plugin pointing at files nobody has.
        var providerId = CloudProviderIds.FromShareUrl(share);
        if (providerId is null)
        {
            Console.WriteLine("FAIL the share link names no platform this build has, so nothing was read");
            return failures + 1;
        }

        var provider = host.CloudProviders.Resolve(new SoftwareSource
        {
            Kind = SourceKind.CloudShare,
            ProviderId = providerId,
            ShareUrl = share,
        });

        if (provider is null)
        {
            Console.WriteLine($"FAIL no provider is registered for '{providerId}', so the share was not read");
            return failures + 1;
        }

        var credential = provider is CloudProviderBase providerBase
            ? await providerBase.LoadCredentialAsync()
            : null;

        var collection = await PluginCandidateCollector
            .CollectAsync(provider, share, credential)
            .ConfigureAwait(false);

        if (!collection.Success || collection.Tree is null)
        {
            Console.WriteLine($"FAIL the real share could not be read: {collection.Error ?? "unknown reason"}");
            return failures + 1;
        }

        var tree = collection.Tree;
        var note = tree.IsTruncated ? $" (truncated, {tree.OmittedCount} omitted)" : string.Empty;
        failures += Report(
            $"{provider.DisplayName} read {tree.Items.Count} real item(s){note}",
            tree.Items.Count > 0);

        if (tree.Items.Count == 0)
        {
            return failures;
        }

        // 2) The model, through the same capability and the same provider the page uses — by kind, so
        //    this check cannot pass against a capability the app does not have.
        var settings = new AiEndpointSettings(
            AiProviders.ForBaseUrl(baseUrl)?.Id ?? AiProviders.CompatibleId,
            baseUrl,
            model,
            key);

        var result = await AiService.Default
            .RunAsync(
                AiCapabilityKind.PluginGenerator,
                settings,
                new AiCapabilityInput(tree.ToPromptJson()),
                host.Platform.Network.Client)
            .ConfigureAwait(false);

        Console.WriteLine($"      answer: {Summarize(result.Text)}");
        failures += Report("the model answered", result.Success);

        if (!result.Success)
        {
            Console.WriteLine($"      {result.Message}");
            return failures;
        }

        // 3) The answer has to survive the reader the page uses, on the answer as it really arrived.
        var answer = result.Text ?? string.Empty;
        Console.WriteLine($"      raw answer kept at: {KeepAnswer(paths, answer)}");

        var validation = PluginDraftValidator.Parse(answer, tree);
        failures += Report(
            "the answer reads as a draft (the JSON survived extraction)",
            validation.Draft is not null);

        if (validation.Draft is null)
        {
            Describe(validation);
            return failures;
        }

        var accepted = validation.Draft.Resources;
        failures += Report("the draft carries at least one resource", accepted.Count > 0);
        failures += Report(
            "every accepted resource points at an item that is really in the share",
            accepted.All(resource => tree.Find(resource.CandidateId) is not null));

        // 4) A real answer arrives with material around the object, so the same answer is read again
        //    with some. Finding the object has to be the extractor's doing, not the model's manners.
        var surrounded = "这是你要的草稿：\r\n```json\r\n" + answer.Trim() + "\r\n```\r\n还需要调整就告诉我。";
        var revalidated = PluginDraftValidator.Parse(surrounded, tree);
        failures += Report(
            "the same answer still reads when prose and a fence are around it",
            revalidated.Draft is not null);

        Describe(validation);

        Console.WriteLine($"      draft name: {validation.Draft.Name}");
        Console.WriteLine($"      accepted: {accepted.Count} of {tree.Items.Count} item(s) read");

        foreach (var resource in accepted.Take(5))
        {
            Console.WriteLine(
                $"        {resource.CandidateId}  {resource.Name}"
                + $"  [{resource.Platform ?? "Unknown"}/{resource.Architecture ?? "Unknown"}]");
        }

        return failures;
    }

    /// <summary>
    /// The share to read, taken from data the build ships rather than typed here: the link is a fact
    /// about the catalog, and a copy of it in a tool is one more place to update. The locator table
    /// names it, but that table is a build input and is not published, so a published build is asked
    /// through the official plugin instead — every source it carries records the share it came from.
    /// </summary>
    private static string? DefaultShare(string assetsRoot)
        => ShareFromLocatorTable(assetsRoot) ?? ShareFromOfficialPlugin(assetsRoot);

    private static string? ShareFromLocatorTable(string assetsRoot)
    {
        var path = Path.Combine(assetsRoot, "shares", "official-locators.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("share", out var share)
                && share.ValueKind == JsonValueKind.String
                    ? share.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ShareFromOfficialPlugin(string assetsRoot)
    {
        var plugins = Path.Combine(assetsRoot, "plugins");
        if (!Directory.Exists(plugins))
        {
            return null;
        }

        foreach (var file in Directory.EnumerateFiles(plugins, "*.json").OrderBy(path => path, StringComparer.Ordinal))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                var share = FirstShareUrl(document.RootElement);
                if (share is not null)
                {
                    return share;
                }
            }
            catch (JsonException)
            {
                // A plugin this tool cannot read is a finding for the plugin checks, not for this one.
            }
        }

        return null;
    }

    /// <summary>The first share link anywhere in a plugin: they all point at the same place.</summary>
    private static string? FirstShareUrl(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("shareUrl")
                        && property.Value.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                    {
                        return property.Value.GetString();
                    }

                    var fromProperty = FirstShareUrl(property.Value);
                    if (fromProperty is not null)
                    {
                        return fromProperty;
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var fromItem = FirstShareUrl(item);
                    if (fromItem is not null)
                    {
                        return fromItem;
                    }
                }

                break;
        }

        return null;
    }

    /// <summary>
    /// Writes the answer out as it arrived. A live answer is the one piece of evidence a failure here
    /// cannot be investigated without, and by the time it is needed the run is over.
    /// </summary>
    private static string KeepAnswer(AppPaths paths, string answer)
    {
        var path = Path.Combine(paths.LogDirectory, "plugin-generator-answer.txt");

        try
        {
            Directory.CreateDirectory(paths.LogDirectory);
            File.WriteAllText(path, answer);
            return path;
        }
        catch (IOException ex)
        {
            return "(not kept: " + ex.Message + ")";
        }
        catch (UnauthorizedAccessException ex)
        {
            return "(not kept: " + ex.Message + ")";
        }
    }

    /// <summary>What the model said that was not used, so a shrunken draft is never read as a whole one.</summary>
    private static void Describe(PluginDraftValidation validation)
    {
        if (validation.Rejected.Count > 0)
        {
            Console.WriteLine($"      refused outright: {validation.Rejected.Count}");
            foreach (var issue in validation.Rejected.Take(5))
            {
                Console.WriteLine($"        {issue.Subject}: {issue.Reason}");
            }
        }

        if (validation.Issues.Count > 0)
        {
            Console.WriteLine($"      dropped fields: {validation.Issues.Count}");
            foreach (var issue in validation.Issues.Take(5))
            {
                Console.WriteLine($"        {issue.Subject}: {issue.Reason}");
            }
        }
    }

    /// <summary>The answer on one line, cut short: enough to see its shape, not enough to bury the run.</summary>
    private static string Summarize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "(no text)";
        }

        var flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
        while (flat.Contains("  ", StringComparison.Ordinal))
        {
            flat = flat.Replace("  ", " ", StringComparison.Ordinal);
        }

        return flat.Length <= 300 ? flat : flat[..300] + "…";
    }

    private static int Report(string label, bool ok)
    {
        Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {label}");
        return ok ? 0 : 1;
    }
}
