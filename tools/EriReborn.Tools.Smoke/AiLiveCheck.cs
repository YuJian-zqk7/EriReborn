using EriReborn.App.Shared;
using EriReborn.App.Shared.Services;
using EriReborn.Core.Logging;
using EriReborn.Engine.Ai;


namespace EriReborn.Tools.Smoke;

/// <summary>
/// Exercises AI configuration the way the AI page does: the key goes into the
/// platform credential store (DPAPI on Windows), is read back, and is then used
/// against the real endpoint. The key is never printed.
/// </summary>
internal static class AiLiveCheck
{
    private const string CredentialKey = "ai/provider/api_key";

    public static async Task<int> RunAsync(AppHost host, AppPaths paths, string baseUrl, string model, string key)
    {
        var failures = 0;

        if (string.IsNullOrWhiteSpace(key))
        {
            Console.WriteLine("SKIP ai: no key supplied, so nothing was verified.");
            Console.WriteLine("     pass one with --ai-key <key>, or set ERIREBORN_AI_KEY.");
            return 0;
        }

        // 1) Persist through the real configuration service.
        var config = new UserConfigService(paths, AppLog.For("Smoke"));

        // The service is named too, and never guessed from the address alone: a capability has to know
        // which protocol to speak, and a gateway shares no address with any shipped service.
        var serviceId = AiProviders.ForBaseUrl(baseUrl)?.Id ?? AiProviders.CompatibleId;
        config.SetAiSettings(serviceId, baseUrl, model);

        failures += Report(
            "service, base url and model were persisted",
            config.Load().AiProviderId == serviceId
            && config.Load().AiBaseUrl == baseUrl
            && config.Load().AiModel == model);

        // 2) The key belongs in the credential store, not in the config file.
        var credentials = host.Platform.Credentials;
        Console.WriteLine($"      credential store: {host.CredentialStoreDescription}");

        if (!credentials.IsAvailable)
        {
            Console.WriteLine("FAIL the credential store is unavailable, so the key cannot be kept safely");
            return failures + 1;
        }

        try
        {
            await credentials.SetAsync(CredentialKey, key);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // The environment refused the write. That is not a verification
            // result, so it is reported as SKIP with the concrete reason — never
            // as a pass, and never as a crash that hides everything after it.
            Console.WriteLine("SKIP the credential store could not be written, so the key was not verified");
            Console.WriteLine($"      {ex.GetType().Name}: {ex.Message}");
            return failures;
        }

        var stored = await credentials.GetAsync(CredentialKey);
        failures += Report("the key round-trips through the credential store", stored == key);
        failures += Report("the key is not written to the plain config file", !File.ReadAllText(config.ConfigFile).Contains(key, StringComparison.Ordinal));

        Console.WriteLine($"      key: {Mask(key)}");

        // 3) The real call.
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var result = await AiConnectivityProbe.ProbeAsync(client, baseUrl, stored);

        Console.WriteLine($"      {baseUrl} -> {(result.StatusCode?.ToString() ?? "no response")} {result.Message}");

        failures += Report("the live endpoint answered and accepted the key", result.Success);
        failures += Report("the endpoint reported at least one model", result.Models.Count > 0);

        if (result.Models.Count > 0)
        {
            Console.WriteLine($"      {result.Models.Count} model(s): {string.Join(", ", result.Models.Take(5))}");

            // Either the configured model is offered, or the app must say so
            // instead of leaving the user to discover it at request time.
            var warning = AiModelCheck.Evaluate(model, result.Models);
            if (warning is null)
            {
                failures += Report($"the configured model '{model}' is offered", true);
            }
            else
            {
                Console.WriteLine($"      model warning: {warning}");
                failures += Report("a model the endpoint does not offer is reported, not ignored", true);
                failures += Report("the warning names the alternatives", result.Models.Any(m => warning.Contains(m, StringComparison.Ordinal)));
            }
        }

        return failures;
    }

    private static string Mask(string key)
        => key.Length <= 10 ? new string('*', key.Length) : $"{key[..6]}…{key[^4..]} ({key.Length} chars)";

    private static int Report(string label, bool ok)
    {
        Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {label}");
        return ok ? 0 : 1;
    }
}
