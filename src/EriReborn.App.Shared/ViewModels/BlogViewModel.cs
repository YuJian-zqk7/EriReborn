using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EriReborn.App.Shared.Services;

namespace EriReborn.App.Shared.ViewModels;

public sealed record BlogPost(string Title, string Date, string Summary, string? Url);

/// <summary>
/// Blog page (spec 63). Content is fetched from JSON with a cache; the page is
/// a reading surface, not a social feed.
/// </summary>
public sealed partial class BlogViewModel : ViewModelBase
{
    private readonly RemoteContentService _remote;

    public BlogViewModel(RemoteContentService remote)
    {
        _remote = remote;
        Title = "博客";
        RemoteUrl = Environment.GetEnvironmentVariable("ERIREBORN_BLOG_URL");
        LoadCommand.Execute(null);
    }

    public ObservableCollection<BlogPost> Posts { get; } = new();

    [ObservableProperty]
    private string? _remoteUrl;

    [ObservableProperty]
    private string _sourceState = string.Empty;

    [RelayCommand]
    private async Task LoadAsync()
    {
        var result = await _remote.FetchAsync(RemoteUrl, "blog").ConfigureAwait(true);
        Posts.Clear();

        if (result is { Success: true, Content: not null })
        {
            TryParse(result.Content);
            SourceState = result.FromCache ? $"离线模式：{result.Message}" : result.Message;
            return;
        }

        SourceState = $"无法加载博客内容：{result.Message}";
    }

    // The blog page used to carry its own "open the marketplace" action and the address it needed. The
    // marketplace has a page of its own, so that was a second way to the same place, with a second address
    // to configure — removed rather than left behind as a command nothing calls.

    private void TryParse(string json)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("posts", out var array) || array.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return;
            }

            foreach (var entry in array.EnumerateArray())
            {
                Posts.Add(new BlogPost(
                    entry.TryGetProperty("title", out var t) ? t.GetString() ?? "-" : "-",
                    entry.TryGetProperty("date", out var d) ? d.GetString() ?? "-" : "-",
                    entry.TryGetProperty("summary", out var s) ? s.GetString() ?? string.Empty : string.Empty,
                    entry.TryGetProperty("url", out var u) ? u.GetString() : null));
            }
        }
        catch (System.Text.Json.JsonException)
        {
            SourceState = "博客数据格式无法解析。";
        }
    }
}
