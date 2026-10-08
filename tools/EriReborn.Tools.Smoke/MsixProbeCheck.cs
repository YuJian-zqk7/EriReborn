using EriReborn.App.Shared.Services;
using EriReborn.Core.Domain;

namespace EriReborn.Tools.Smoke;

/// <summary>
/// Exercises MSIX/Appx detection against packages that are actually installed,
/// and confirms a pattern that matches nothing is reported as not detected
/// rather than as an error.
/// </summary>
internal static class MsixProbeCheck
{
    public static async Task<int> RunAsync(AppHost host)
    {
        var detector = host.Platform.Detector;
        if (detector is null)
        {
            Console.WriteLine("FAIL no detector on this platform");
            return 1;
        }

        var failures = 0;

        // Real packages present on Windows: EarTrumpet is a Store app, and the
        // Calculator ships with Windows.
        var cases = new (string Label, string Pattern, string Expect)[]
        {
            ("a Store app by name", "^EarTrumpet$", "Detected"),
            ("a built-in Windows app", "^Microsoft\\.WindowsCalculator", "Detected"),
            ("a vendor package by its dot name", "^Adobe\\.Fresco", "Detected"),
            ("a package that is not installed", "^EriRebornDefinitelyNotInstalledPackage$", "NotDetected"),
        };

        foreach (var (label, pattern, expect) in cases)
        {
            var software = new SoftwareDefinition
            {
                Id = "probe",
                Name = "probe",
                CategoryId = "Utility",
                DirectoryName = "Probe",
                Trust = SoftwareTrust.Verified,
                Provenance = EriReborn.Core.Catalog.CatalogProvenance.Official,
                Detector = new DetectorSpec(DetectorKind.Msix, pattern),
                Sources = Array.Empty<SoftwareSource>(),
            };

            var result = await detector.DetectAsync(software);
            var actual = result.Outcome.ToString();
            Console.WriteLine($"      {label,-34} -> {actual,-12} {result.Version ?? ""} {result.Detail ?? ""}");

            failures += Report($"msix: {label}", actual == expect);
        }

        return failures;
    }

    private static int Report(string label, bool ok)
    {
        Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {label}");
        return ok ? 0 : 1;
    }
}
