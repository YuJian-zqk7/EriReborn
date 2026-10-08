using System.Text;
using EriReborn.Core.Catalog;
using EriReborn.Core.Logging;

namespace EriReborn.Tools.CatalogMigration;

/// <summary>
/// One-shot migration from the v2 (schema 2) catalog to the v3 (schema 3)
/// model (spec 71). The legacy files are treated as input only; nothing is
/// copied through unchanged, and every converted entry is re-validated.
///
/// Usage: EriReborn.Tools.CatalogMigration --in &lt;legacyDir&gt; --out &lt;catalogDir&gt;
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var input = GetOption(args, "--in");
        var output = GetOption(args, "--out");

        if (input is null || output is null)
        {
            Console.Error.WriteLine("Usage: --in <legacyDir> --out <catalogDir>");
            return 2;
        }

        if (!Directory.Exists(input))
        {
            Console.Error.WriteLine($"Input directory not found: {input}");
            return 2;
        }

        Directory.CreateDirectory(output);

        AppLog.MinimumLevel = LogLevel.Info;
        AppLog.AddSink(new ConsoleLogSink());

        var totalRead = 0;
        var totalWritten = 0;
        var totalSkipped = 0;
        var issues = new List<string>();

        foreach (var file in Directory.GetFiles(input, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            var catalogId = Path.GetFileNameWithoutExtension(file);
            var json = File.ReadAllText(file, Encoding.UTF8);

            LegacyImportResult result;
            try
            {
                result = LegacyCatalogImporter.Import(json, catalogId);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[FAIL] {catalogId}: {ex.Message}");
                return 1;
            }

            totalRead += result.ItemsRead;
            totalSkipped += result.ItemsSkipped;
            issues.AddRange(result.Issues.Select(i => $"{catalogId} {i}"));

            var payload = CatalogWriter.Write(catalogId, result.Software);
            var target = Path.Combine(output, catalogId + ".json");
            File.WriteAllText(target, payload, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            totalWritten += result.Software.Count;
            Console.WriteLine($"[OK] {catalogId}: read={result.ItemsRead} written={result.Software.Count} skipped={result.ItemsSkipped}");
        }

        Console.WriteLine();
        Console.WriteLine($"total read={totalRead} written={totalWritten} skipped={totalSkipped} issues={issues.Count}");

        if (issues.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Issues (informational):");
            foreach (var group in issues.GroupBy(i => i.Split(':', 2)[0]))
            {
                Console.WriteLine($"  {group.Key}: {group.Count()}");
            }
        }

        // Verify the produced catalog loads with zero rejections.
        var reader = new CatalogReader(AppLog.For("Verify"));
        var catalog = reader.LoadDirectoryAsync(output).GetAwaiter().GetResult();
        Console.WriteLine();
        Console.WriteLine($"verification: accepted={catalog.Software.Count} rejected={catalog.Report.ItemsRejected}");
        foreach (var rejected in catalog.Report.Rejected.Take(20))
        {
            Console.WriteLine($"  REJECTED {rejected}");
        }

        return catalog.Report.ItemsRejected == 0 ? 0 : 1;
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
