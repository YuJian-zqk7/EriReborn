using EriReborn.App.Shared.Services;
using EriReborn.Core.Domain;
using EriReborn.Platform.Abstractions;

namespace EriReborn.Tools.Smoke;

/// <summary>
/// Exercises the PnP detector against this machine's real registry.
///
/// <para>
/// This is where the PnP and ClassGUID claims are actually verified. A unit test would
/// have to invent a registry, and the thing worth knowing is whether the walk finds the
/// hardware that is really here. The class filter is checked for discriminating power,
/// not merely for not erroring: a filter that matched nothing would have passed the
/// older version of this check.
/// </para>
/// </summary>
internal static class PnpProbeCheck
{
    private const string DisplayClass = "{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>A class guid no device can have, used to prove the filter rejects.</summary>
    private const string ImpossibleClass = "{00000000-0000-0000-0000-000000000000}";

    public static async Task<int> RunAsync(AppHost host)
    {
        // Reproduce the registry walk here so a failure can be localised.
        using (var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
                   Microsoft.Win32.RegistryHive.LocalMachine,
                   Microsoft.Win32.RegistryView.Default))
        {
            using var enumKey = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum");
            Console.WriteLine($"      enum key present: {enumKey is not null}");

            if (enumKey is not null)
            {
                var enumerators = enumKey.GetSubKeyNames();
                Console.WriteLine($"      enumerators: {enumerators.Length} [{string.Join(", ", enumerators.Take(5))}…]");

                var devices = 0;
                var instances = 0;
                var named = 0;
                foreach (var enumeratorName in enumerators)
                {
                    using var enumerator = enumKey.OpenSubKey(enumeratorName);
                    if (enumerator is null)
                    {
                        continue;
                    }

                    foreach (var deviceName in enumerator.GetSubKeyNames())
                    {
                        using var device = enumerator.OpenSubKey(deviceName);
                        if (device is null)
                        {
                            continue;
                        }

                        devices++;
                        foreach (var instanceName in device.GetSubKeyNames())
                        {
                            using var instance = device.OpenSubKey(instanceName);
                            if (instance is null)
                            {
                                continue;
                            }

                            instances++;
                            if (instance.GetValue("FriendlyName") is string f && !string.IsNullOrWhiteSpace(f))
                            {
                                named++;
                            }
                            else if (instance.GetValue("DeviceDesc") is string d && !string.IsNullOrWhiteSpace(d))
                            {
                                named++;
                            }
                        }
                    }
                }

                Console.WriteLine($"      devices={devices} instances={instances} with-a-name={named}");
            }
        }

        var detector = host.Platform.Detector;
        if (detector is null)
        {
            Console.WriteLine("FAIL no detector on this platform");
            return 1;
        }

        var cases = new (string Label, DetectorSpec Spec)[]
        {
            ("pnp NVIDIA (no class filter)", new DetectorSpec(DetectorKind.Pnp, "NVIDIA")),
            ("pnp NVIDIA (display class)", new DetectorSpec(DetectorKind.Pnp, "NVIDIA", DisplayClass)),
            ("pnp any (display class)", new DetectorSpec(DetectorKind.Pnp, ".", DisplayClass)),
            ("pnp any device at all", new DetectorSpec(DetectorKind.Pnp, ".")),
            ("video class (control)", new DetectorSpec(DetectorKind.Video, ".")),
        };

        var failures = 0;
        foreach (var (label, spec) in cases)
        {
            var result = await DetectAsync(detector, spec);
            Console.WriteLine($"      {label,-32} -> {result.Outcome,-12} {result.Detail ?? result.Source}");
            failures += result.Outcome == DetectionOutcome.Error ? 1 : 0;
        }

        // The class filter has to actually filter. Counting only Errors would accept a
        // filter that matched nothing, or one that matched everything, because both of
        // those answer without erroring. This machine is guaranteed to have a display
        // adapter, so a display-class filter that finds nothing is wrong, and a filter
        // naming a class that cannot exist that finds something is equally wrong.
        var anyDisplay = await DetectAsync(detector, new DetectorSpec(DetectorKind.Pnp, ".", DisplayClass));
        var impossibleClass = await DetectAsync(detector, new DetectorSpec(DetectorKind.Pnp, ".", ImpossibleClass));

        Console.WriteLine($"      class filter accepts the display class -> {anyDisplay.Outcome} {anyDisplay.Detail ?? anyDisplay.Source}");
        Console.WriteLine($"      class filter rejects an absent class   -> {impossibleClass.Outcome} {impossibleClass.Detail ?? impossibleClass.Source}");

        if (anyDisplay.Outcome != DetectionOutcome.Detected)
        {
            Console.WriteLine("FAIL the display class matched nothing; the class filter cannot be discriminating.");
            failures++;
        }

        if (impossibleClass.Outcome == DetectionOutcome.Detected)
        {
            Console.WriteLine("FAIL a class that cannot exist matched something; the class filter is not filtering.");
            failures++;
        }

        return failures;
    }

    private static async Task<DetectionResult> DetectAsync(ISoftwareDetector detector, DetectorSpec spec)
    {
        var software = new SoftwareDefinition
        {
            Id = "probe",
            Name = "probe",
            CategoryId = "System_Drivers",
            DirectoryName = "Probe",
            Trust = SoftwareTrust.Verified,
            Detector = spec,
            Sources = Array.Empty<SoftwareSource>(),
        };

        return await detector.DetectAsync(software);
    }
}
