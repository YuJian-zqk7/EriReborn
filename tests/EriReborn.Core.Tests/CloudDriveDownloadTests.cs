using System.Net;
using System.Text;
using EriReborn.App.Shared;
using EriReborn.App.Shared.Services;
using EriReborn.App.Shared.ViewModels;
using EriReborn.Cloud;
using EriReborn.Cloud.Providers;
using EriReborn.Core.Jobs;
using EriReborn.Core.Logging;
using EriReborn.Core.Tests.TestSupport;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// Drives the cloud page the way the two buttons drive it (「我的网盘」 and the per-row 「下载」)
/// against a scripted HTTP endpoint, and insists the bytes really land on disk. This is the
/// repeatable half of the manual UI check (spec 72/73).
/// </summary>
public sealed class CloudDriveDownloadTests
{
    private const string FileName = "astral-windows-x64-setup.exe";
    private const string DownloadUrl = "https://download.example.test/" + FileName;

    private static byte[] Payload()
    {
        var bytes = new byte[4096];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        for (var i = 2; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i % 251);
        }

        return bytes;
    }

    /// <summary>Answers only the two Quark drive endpoints plus the file itself; everything else fails politely.</summary>
    private sealed class ScriptedQuark : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;

            if (url.Contains("/file/sort", StringComparison.Ordinal))
            {
                var json = "{\"code\":0,\"message\":\"ok\",\"data\":{\"list\":["
                    + "{\"fid\":\"dir-share\",\"file_name\":\"来自：分享\",\"dir\":true,\"size\":0},"
                    + "{\"fid\":\"dir-other\",\"file_name\":\"bandizip免费专业版\",\"dir\":true,\"size\":0},"
                    + "{\"fid\":\"file-1\",\"file_name\":\"" + FileName + "\",\"dir\":false,\"size\":4096}]}}";
                return Task.FromResult(Json(json));
            }

            if (url.Contains("/file/download", StringComparison.Ordinal))
            {
                var json = "{\"code\":0,\"message\":\"ok\",\"data\":[{\"fid\":\"file-1\",\"file_name\":\"" + FileName
                    + "\",\"download_url\":\"" + DownloadUrl + "\"}]}";
                return Task.FromResult(Json(json));
            }

            if (url.StartsWith(DownloadUrl, StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Payload()),
                });
            }

            // Every other platform is deliberately left unprovisioned: it must fail, not throw.
            return Task.FromResult(Json("{\"code\":-1,\"message\":\"stub: 未配置该平台\"}"));
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    private static async Task<(AppHost Host, string UserData, InMemoryCredentialStore Credentials)> CreateHostAsync()
    {
        var userData = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        var paths = AppPaths.Detect(userDataOverride: userData);

        var network = new TestNetworkService(new HttpClient(new ScriptedQuark()));
        var credentials = new InMemoryCredentialStore();
        var files = new TestFileSystemService(userData);
        var platform = new TestPlatform(files, network, credentials);

        ICloudProvider[] providers =
        {
            new Provider123(network, credentials),
            new ProviderBaidu(network, credentials),
            new ProviderQuark(network, credentials),
            new ProviderLanzou(network, credentials),
            new ProviderXunlei(network, credentials),
        };

        var registry = new CloudProviderRegistry(providers, AppLog.For("Test"));
        var host = await AppHost.CreateAsync(paths, platform, registry, AppLog.For("Test"));
        return (host, userData, credentials);
    }

    [Fact]
    public async Task Browsing_the_drive_lists_folders_and_files()
    {
        var (host, userData, credentials) = await CreateHostAsync();
        try
        {
            await credentials.SetAsync("cloud/quark/cookie", "SESSION=stub", CancellationToken.None);

            var view = new CloudViewModel(host);
            await view.OpenDriveCommand.ExecuteAsync(null);

            var folders = view.ShareFiles.Where(row => row.IsFolder).Select(row => row.Text).ToArray();
            var files = view.ShareFiles.Where(row => !row.IsFolder).Select(row => row.Text).ToArray();

            Assert.Contains(folders, text => text.Contains("来自：分享", StringComparison.Ordinal));
            Assert.Contains(files, text => text.Contains(FileName, StringComparison.Ordinal));
            Assert.Contains("我的网盘", view.ShareStatus, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task The_row_download_button_writes_the_real_bytes_to_disk()
    {
        var (host, userData, credentials) = await CreateHostAsync();
        try
        {
            await credentials.SetAsync("cloud/quark/cookie", "SESSION=stub", CancellationToken.None);

            var view = new CloudViewModel(host);
            await view.OpenDriveCommand.ExecuteAsync(null);

            var row = Assert.Single(view.ShareFiles, r => !r.IsFolder && r.Text.Contains(FileName, StringComparison.Ordinal));

            // The button now starts a real job instead of downloading inside the page, so the bytes
            // are on disk once the job reaches a terminal state — which is also what the task list
            // watches. The page must not have written anything itself.
            await row.DownloadCommand.ExecuteAsync(null);

            var job = view.LastDownloadJob;
            Assert.NotNull(job);
            await job!.Completion;

            var landed = Path.Combine(userData, "downloads", FileName);
            Assert.True(File.Exists(landed), "状态: " + view.ShareStatus + " / 任务: " + (job.Error ?? job.State.ToString()));
            Assert.Equal(Payload(), await File.ReadAllBytesAsync(landed, CancellationToken.None));
            Assert.Equal(JobState.Completed, job.State);
            Assert.Contains("已加入任务中心", view.ShareStatus, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public void A_locked_profile_root_falls_back_next_to_the_executable()
    {
        var root = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var blocker = Path.Combine(root, "blocker");
            File.WriteAllText(blocker, "x");
            var appDirectory = Path.Combine(root, "app");
            Directory.CreateDirectory(appDirectory);

            // A directory below a plain file can never be created, so this stands in for a locked profile.
            var locked = Path.Combine(blocker, "EriReborn");
            var resolved = AppPaths.ResolveWritableUserData(locked, appDirectory);

            Assert.Equal(Path.Combine(appDirectory, "EriReborn-data"), resolved);
            Assert.True(AppPaths.IsWritableDirectory(resolved));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void A_writable_profile_root_is_kept_as_is()
    {
        var root = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var preferred = Path.Combine(root, "EriReborn");
            Assert.Equal(preferred, AppPaths.ResolveWritableUserData(preferred, Path.Combine(root, "app")));
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static void Cleanup(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
        catch (IOException)
        {
            // A leftover temp folder must never fail a test.
        }
    }
}
