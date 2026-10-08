using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EriReborn.App.Shared.Services;

namespace EriReborn.App.Shared.ViewModels;

public sealed record Announcement(string Version, string Date, string Title, string Body);

/// <summary>
/// Update announcement page (spec 62). Failure to reach the network is shown
/// explicitly; it never prevents the app from running.
/// </summary>
public sealed partial class UpdateViewModel : ViewModelBase
{
    private readonly RemoteContentService _remote;

    public UpdateViewModel(RemoteContentService remote)
    {
        _remote = remote;
        Title = "更新公告";
        RemoteUrl = Environment.GetEnvironmentVariable("ERIREBORN_ANNOUNCEMENT_URL");
        LoadCommand.Execute(null);
    }

    public ObservableCollection<Announcement> Announcements { get; } = new();

    [ObservableProperty]
    private string? _remoteUrl;

    [ObservableProperty]
    private string _sourceState = string.Empty;

    [RelayCommand]
    private async Task LoadAsync()
    {
        // A configured remote address wins: it is how the list is updated without shipping a build. With
        // none — or with one that cannot be reached — the list that ships with the app is shown instead
        // of an error. The page used to be empty for everyone, because no address is configured and
        // nothing was bundled to fall back on.
        if (string.IsNullOrWhiteSpace(RemoteUrl))
        {
            LoadBundled("未配置远程公告地址，显示随包公告。");
            return;
        }

        var result = await _remote.FetchAsync(RemoteUrl, "announcements").ConfigureAwait(true);
        Announcements.Clear();

        if (result is { Success: true, Content: not null })
        {
            TryParse(result.Content);
            SourceState = result.FromCache ? $"离线模式：{result.Message}" : result.Message;
            return;
        }

        if (!LoadBundled($"无法加载远程公告（{result.Message}），显示随包公告。"))
        {
            SourceState = $"无法加载公告：{result.Message}";
        }
    }

    /// <summary>
    /// Shows the announcements that ship with the app. Returns false when there are none to show, so the
    /// caller can still report the remote failure instead of pretending the list is empty on purpose.
    /// </summary>
    private bool LoadBundled(string state)
    {
        var path = BundledAnnouncementsPath();
        if (!File.Exists(path))
        {
            return false;
        }

        Announcements.Clear();
        TryParse(File.ReadAllText(path));

        if (Announcements.Count == 0)
        {
            return false;
        }

        SourceState = state;
        return true;
    }

    /// <summary>
    /// Where the bundled list lives: beside the executable when the app is published, and up the tree
    /// from the test binaries when the tests run.
    /// </summary>
    private static string BundledAnnouncementsPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "assets", "announcements", "announcements.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(AppContext.BaseDirectory, "assets", "announcements", "announcements.json");
    }

    private void TryParse(string json)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("announcements", out var array) || array.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return;
            }

            foreach (var entry in array.EnumerateArray())
            {
                Announcements.Add(new Announcement(
                    entry.TryGetProperty("version", out var v) ? v.GetString() ?? "-" : "-",
                    entry.TryGetProperty("date", out var d) ? d.GetString() ?? "-" : "-",
                    entry.TryGetProperty("title", out var t) ? t.GetString() ?? "-" : "-",
                    entry.TryGetProperty("body", out var b) ? b.GetString() ?? string.Empty : string.Empty));
            }
        }
        catch (System.Text.Json.JsonException)
        {
            SourceState = "公告数据格式无法解析。";
        }
    }
}
