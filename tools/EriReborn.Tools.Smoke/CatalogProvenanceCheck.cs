using EriReborn.App.Shared;
using EriReborn.Core.Catalog;
using EriReborn.Core.Domain;
using EriReborn.Core.Logging;

namespace EriReborn.Tools.Smoke;

/// <summary>
/// Proves the catalog authority chain end to end: the shipped catalog verifies,
/// a single edited byte invalidates it, the loader then grants nobody official
/// authority, and an install is refused rather than silently proceeding.
/// </summary>
internal static class CatalogProvenanceCheck
{
    public static async Task<int> RunAsync(AppPaths paths, Func<Task<EriReborn.Engine.Software.SoftwareEngine>> engineFactory)
    {
        var failures = 0;

        // ---------------------------------------------------------- shipped data
        var verdict = CatalogVerifier.Verify(paths.CatalogDirectory, AppLog.For("Catalog"));
        Console.WriteLine($"      signature : {verdict.State} — {verdict.Message}");
        Console.WriteLine($"      key       : {verdict.KeyId ?? "(none)"} / {verdict.Publisher ?? "(none)"}");
        Console.WriteLine($"      covered   : {verdict.OfficialFiles.Count} file(s)");

        failures += Report("the shipped official catalog carries a valid signature",
            verdict.State == CatalogSignatureState.Verified);
        failures += Report("the signature covers every shipped catalog file",
            verdict.OfficialFiles.Count == Directory.GetFiles(paths.CatalogDirectory, "*.json").Length);

        var reader = new CatalogReader(AppLog.For("Catalog"));
        var catalog = await reader.LoadDirectoryAsync(paths.CatalogDirectory);

        Console.WriteLine($"      entries   : {catalog.Software.Count} total, {catalog.OfficialCount} official, "
            + $"{catalog.ThirdPartyCount} third-party, {catalog.UntrustedCount} untrusted");
        Console.WriteLine($"      {catalog.Report.ItemsRejected} rejection(s)");

        failures += Report("every shipped entry is official", catalog.UntrustedCount == 0 && catalog.ThirdPartyCount == 0);
        failures += Report("the catalog reports official authority", catalog.HasOfficialAuthority);

        // ------------------------------------------------------- tamper detection
        var sandbox = Path.Combine(Path.GetTempPath(), "erireborn-smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        try
        {
            foreach (var file in Directory.GetFiles(paths.CatalogDirectory))
            {
                File.Copy(file, Path.Combine(sandbox, Path.GetFileName(file)));
            }

            var victim = Directory.GetFiles(sandbox, "*.json").OrderBy(f => f, StringComparer.Ordinal).First();
            var original = File.ReadAllText(victim);

            // One changed character is enough: this is what shipping a cracked
            // catalog looks like.
            File.WriteAllText(victim, original.Replace("\"name\"", "\"name\" ", StringComparison.Ordinal));

            var tampered = CatalogVerifier.Verify(sandbox, AppLog.For("Catalog"));
            Console.WriteLine($"      tampered  : {tampered.State} — {tampered.Message}");

            failures += Report("editing an official file invalidates the signature",
                tampered.State == CatalogSignatureState.Invalid);
            failures += Report("a tampered catalog grants no official authority", !tampered.GrantsOfficialAuthority);

            var tamperedCatalog = await reader.LoadDirectoryAsync(sandbox);
            Console.WriteLine($"      tampered  : {tamperedCatalog.OfficialCount} official, "
                + $"{tamperedCatalog.UntrustedCount} untrusted entries");

            failures += Report("no tampered entry is treated as official", tamperedCatalog.OfficialCount == 0);
            failures += Report("tampered entries are marked untrusted", tamperedCatalog.UntrustedCount > 0);

            // An install driven by that data must be refused.
            var engine = await engineFactory();
            var suspect = tamperedCatalog.Software.First();
            var context = new EriReborn.Core.Paths.EnvironmentContext
            {
                RootPath = Path.Combine(sandbox, "install-root"),
            };

            var result = await engine.InstallAsync(suspect, context);
            Console.WriteLine($"      install   : {result.State} — {result.Message}");

            failures += Report("installing from tampered data is refused",
                result.State == InstallState.UntrustedDefinition);
        }
        finally
        {
            try
            {
                Directory.Delete(sandbox, recursive: true);
            }
            catch
            {
                // Best effort.
            }
        }

        return failures;
    }

    private static int Report(string label, bool ok)
    {
        Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {label}");
        return ok ? 0 : 1;
    }
}
