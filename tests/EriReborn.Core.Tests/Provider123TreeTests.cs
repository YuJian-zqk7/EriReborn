using System.Net;
using System.Net.Http;
using EriReborn.Cloud;
using EriReborn.Cloud.Providers;
using EriReborn.Core.Domain;
using EriReborn.Core.Tests.TestSupport;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// 123 is the only provider the official catalogue's eight entries share, and they all live in one
/// share. Until this, <see cref="Provider123.ListShareFolderAsync"/> fell back to "cannot open that
/// folder", so a plugin that named a folder in the share could never be reached. These tests drive
/// the real provider against its own documented response shape (a stub stands in for the network,
/// so no live account is touched) — the only step left is a round trip against a real share.
/// </summary>
public sealed class Provider123TreeTests
{
    internal const string ShareUrl = "https://www.123pan.com/s/abc-def";

    private static Provider123 Provider(StubHandler handler)
        => new(new TestNetworkService(new HttpClient(handler)), new InMemoryCredentialStore());

    /// <summary>Shared with the cloud-page tests, which need a share that really walks.</summary>
    internal static Dictionary<string, string> TreeResponses() => new()
    {
        // ParentFileId=0 is the share root; every other key is a folder's own id.
        ["0"] = InfoList(("A", 1, true, 0), ("B", 2, true, 0)),
        ["1"] = InfoList(("a1", 11, true, 0)),
        ["11"] = InfoList(("b1", 111, true, 0)),
        ["111"] = InfoList(("ResourceA.zip", 1111, false, 500), ("ResourceB.zip", 1112, false, 600)),
        ["2"] = InfoList(("ResourceC.zip", 21, false, 700)),
    };

    private static string InfoList(params (string Name, long Id, bool Folder, long Size)[] items)
    {
        var entries = string.Join(",", items.Select(item =>
            $"{{\"FileName\":\"{item.Name}\",\"FileId\":{item.Id},\"Type\":{(item.Folder ? 1 : 0)},\"Size\":{item.Size}}}"));
        return "{\"code\":0,\"data\":{\"InfoList\":[" + entries + "]}}";
    }

    [Fact]
    public async Task ListShareFolderAsync_descends_the_share_tree()
    {
        var provider = Provider(new StubHandler(TreeResponses()));

        var root = await provider.ListShareFolderAsync(ShareUrl, "0", null);
        Assert.True(root.Success);
        var a = Assert.Single(root.Files, file => file.Name == "A");
        Assert.True(a.IsFolder);

        var underA = await provider.ListShareFolderAsync(ShareUrl, a.Id, null);
        var a1 = Assert.Single(underA.Files, file => file.Name == "a1");

        var underA1 = await provider.ListShareFolderAsync(ShareUrl, a1.Id, null);
        var b1 = Assert.Single(underA1.Files, file => file.Name == "b1");

        var leaves = await provider.ListShareFolderAsync(ShareUrl, b1.Id, null);
        Assert.Contains(leaves.Files, file => file.Name == "ResourceA.zip" && !file.IsFolder);
        Assert.Contains(leaves.Files, file => file.Name == "ResourceB.zip" && !file.IsFolder);
    }

    [Fact]
    public async Task ResolveInShareAsync_walks_to_a_nested_file_and_gets_its_link()
    {
        var provider = Provider(new StubHandler(TreeResponses()));

        var resolved = await provider.ResolveInShareAsync(
            ShareUrl,
            new ResourceLocator { Path = "A/a1/b1/ResourceA.zip" },
            null);

        Assert.True(resolved.Success, resolved.Message);
        // The link comes from the located item's own id, not a root re-listing that could not see it.
        Assert.Equal("http://dl.test/1111", resolved.Handle?.DownloadUrl);
    }

    [Fact]
    public async Task ResolveInShareAsync_missing_folder_is_reported_by_name()
    {
        var provider = Provider(new StubHandler(TreeResponses()));

        var resolved = await provider.ResolveInShareAsync(
            ShareUrl,
            new ResourceLocator { Path = "A/nope/b1/ResourceA.zip" },
            null);

        Assert.False(resolved.Success);
        Assert.Equal(CloudErrorKind.NotFound, resolved.Error);
        Assert.Contains("nope", resolved.Message);
    }

    /// <summary>Stands in for 123's API: answers /api/share/get with the canned tree and the download endpoint with a link.</summary>
    internal sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _folders;

        public StubHandler(Dictionary<string, string> folders) => _folders = folders;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            string json;

            if (request.Method == HttpMethod.Post && uri.AbsolutePath.Contains("download_info"))
            {
                var body = request.Content is not null
                    ? await request.Content.ReadAsStringAsync(cancellationToken)
                    : string.Empty;
                json = "{\"code\":0,\"data\":{\"DownloadUrl\":\"http://dl.test/" + Field(body, "fileId") + "\"}}";
            }
            else
            {
                var parent = QueryValue(uri.Query, "ParentFileId") ?? "0";
                json = _folders.TryGetValue(parent, out var canned)
                    ? canned
                    : "{\"code\":0,\"data\":{\"InfoList\":[]}}";
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            };
        }

        private static string QueryValue(string query, string name)
        {
            foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var equals = pair.IndexOf('=');
                if (equals > 0 && pair[..equals] == name)
                {
                    return pair[(equals + 1)..];
                }
            }

            return string.Empty;
        }

        private static string Field(string json, string name)
        {
            var start = json.IndexOf("\"" + name + "\":", StringComparison.Ordinal);
            if (start < 0)
            {
                return "0";
            }

            var colon = json.IndexOf(':', start) + 1;
            var end = json.IndexOfAny(new[] { ',', '}' }, colon);
            return json[colon..end].Trim();
        }
    }
}
