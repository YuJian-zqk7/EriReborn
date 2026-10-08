using System.Text.Json;
using EriReborn.Core.Logging;

namespace EriReborn.App.Shared.Tutorial;

/// <summary>One page of the tutorial. Body lines are plain text, never markup.</summary>
public sealed record TutorialTopic
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public IReadOnlyList<string> Body { get; init; } = Array.Empty<string>();

    /// <summary>Sub-topics, so related pages sit together instead of in one long list.</summary>
    public IReadOnlyList<TutorialTopic> Children { get; init; } = Array.Empty<TutorialTopic>();

    public IEnumerable<TutorialTopic> Flatten()
    {
        yield return this;

        foreach (var child in Children)
        {
            foreach (var descendant in child.Flatten())
            {
                yield return descendant;
            }
        }
    }
}

public sealed record TutorialCatalog
{
    public const string FileName = "tutorial.json";

    /// <summary>Where the tutorial lives under the assets root.</summary>
    public const string DirectoryName = "tutorial";

    public int Schema { get; init; } = 1;

    /// <summary>The first-run walkthrough, in order.</summary>
    public IReadOnlyList<TutorialTopic> Onboarding { get; init; } = Array.Empty<TutorialTopic>();

    /// <summary>The reference tree reachable from the tutorial centre.</summary>
    public IReadOnlyList<TutorialTopic> Topics { get; init; } = Array.Empty<TutorialTopic>();

    public static TutorialCatalog Empty { get; } = new();

    public IEnumerable<TutorialTopic> AllTopics() => Topics.SelectMany(topic => topic.Flatten());

    public TutorialTopic? Find(string? id)
        => id is null
            ? null
            : Onboarding.Concat(AllTopics()).FirstOrDefault(topic =>
                string.Equals(topic.Id, id, StringComparison.Ordinal));
}

/// <summary>
/// Loads the tutorial from the shipped assets.
///
/// It is bundled rather than fetched, because a tutorial that needs the network
/// is unavailable exactly when it is most needed (spec 11 of the patch).
/// </summary>
public static class TutorialCatalogReader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <param name="assetsRoot">The assets root, not the tutorial directory.</param>
    public static TutorialCatalog Load(string assetsRoot, IAppLogger log)
    {
        var path = Path.Combine(assetsRoot, TutorialCatalog.DirectoryName, TutorialCatalog.FileName);
        if (!File.Exists(path))
        {
            // No tutorial is better than a broken page, and it is said plainly.
            log.Warn("tutorial.missing", $"教程文件不存在：{path}");
            return TutorialCatalog.Empty;
        }

        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            log.Error("tutorial.load", $"教程文件无法读取：{ex.Message}", ex);
            return TutorialCatalog.Empty;
        }
    }

    public static TutorialCatalog Parse(string json)
    {
        var catalog = JsonSerializer.Deserialize<TutorialCatalog>(json, Options) ?? TutorialCatalog.Empty;

        // An entry without an id cannot be navigated to or remembered, so it is
        // dropped rather than shown and then un-findable.
        return catalog with
        {
            Onboarding = Clean(catalog.Onboarding),
            Topics = Clean(catalog.Topics),
        };
    }

    private static IReadOnlyList<TutorialTopic> Clean(IReadOnlyList<TutorialTopic> topics)
        => topics
            .Where(topic => !string.IsNullOrWhiteSpace(topic.Id))
            .Select(topic => topic with { Children = Clean(topic.Children) })
            .ToArray();
}

/// <summary>What the user has already seen.</summary>
public sealed record TutorialProgress
{
    /// <summary>True once the first-run walkthrough has been finished or skipped.</summary>
    public bool OnboardingCompleted { get; init; }

    public string? LastTopicId { get; init; }

    public IReadOnlyList<string> SeenTopics { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Remembers tutorial progress across restarts. A damaged file is reported and
/// treated as "nothing seen yet", which is the safe direction: it shows the
/// tutorial again rather than hiding it forever.
/// </summary>
public sealed class TutorialProgressStore(string filePath, IAppLogger log)
{
    private readonly string _filePath = filePath;
    private readonly IAppLogger _log = log;

    public TutorialProgress Current { get; private set; } = new();

    public void Load()
    {
        if (!File.Exists(_filePath))
        {
            Current = new TutorialProgress();
            return;
        }

        try
        {
            Current = JsonSerializer.Deserialize<TutorialProgress>(
                File.ReadAllText(_filePath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new TutorialProgress();
        }
        catch (Exception ex)
        {
            _log.Error("tutorial.progress", $"教程进度无法读取，按未看过处理：{ex.Message}", ex);
            Current = new TutorialProgress();
        }
    }

    public void Save(TutorialProgress progress)
    {
        Current = progress;

        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_filePath, JsonSerializer.Serialize(progress, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            // Progress that cannot be saved must not break the tutorial itself.
            _log.Error("tutorial.progress", $"教程进度无法写入：{ex.Message}", ex);
        }
    }

    public void MarkCompleted(string? lastTopicId = null)
        => Save(Current with { OnboardingCompleted = true, LastTopicId = lastTopicId ?? Current.LastTopicId });

    public void MarkSeen(string topicId)
    {
        if (Current.SeenTopics.Contains(topicId, StringComparer.Ordinal))
        {
            return;
        }

        Save(Current with
        {
            SeenTopics = Current.SeenTopics.Append(topicId).ToArray(),
            LastTopicId = topicId,
        });
    }

    /// <summary>Lets the user see the walkthrough again from the tutorial centre.</summary>
    public void Reset() => Save(new TutorialProgress());
}
