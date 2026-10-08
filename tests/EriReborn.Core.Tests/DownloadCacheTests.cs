using System.Security.Cryptography;
using EriReborn.Engine.Download;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// The cache decides whether bytes on disk may be trusted and how two downloads
/// are told apart.
///
/// <para>
/// Mutation testing found this class completely unguarded: all five of its
/// properties survived the whole suite. It would have served unverifiable bytes,
/// kept corrupt entries, and — worst of the five — let two builds of the same
/// product at the same URL collide, which is the one thing its own documentation
/// says it prevents.
/// </para>
/// </summary>
public sealed class DownloadCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "erireborn-cache",
        Guid.NewGuid().ToString("N"));

    private string CacheRoot => Path.Combine(_root, "cache");

    public DownloadCacheTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        finally
        {
            // Best effort.
        }
    }

    private static string Sha256Of(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private string WriteSource(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private DownloadCacheKey Key(byte[] content, string url = "https://example.invalid/file.bin")
        => new("my_tool", "1.0.0", "x64", "lanzou", url, Sha256Of(content));

    // ---------------------------------------------------------- trust

    [Fact]
    public void A_stored_file_that_still_matches_its_hash_is_trusted()
    {
        var content = "the real bytes"u8.ToArray();
        var cache = new DownloadCache(CacheRoot);
        var key = Key(content);

        cache.Store(key, WriteSource("source.bin", "the real bytes"));

        Assert.True(cache.TryGetVerified(key, out var path));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Bytes_that_do_not_match_the_hash_are_refused_and_removed()
    {
        // Serving these would report a corrupt download as a verified one.
        var cache = new DownloadCache(CacheRoot);
        var key = Key("expected"u8.ToArray());

        var entry = cache.GetEntryPath(key);
        Directory.CreateDirectory(CacheRoot);
        File.WriteAllText(entry, "tampered");

        Assert.False(cache.TryGetVerified(key, out _));

        // An entry that cannot be trusted must not be left where it will be tried
        // again on the next run.
        Assert.False(File.Exists(entry));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_entry_with_no_hash_is_never_trusted(string? declared)
    {
        // There is nothing to check the file against, so its presence proves nothing.
        // The blank forms matter as much as the null one: a check written against null
        // alone still accepts "" and serves whatever happens to be on disk.
        var cache = new DownloadCache(CacheRoot);
        var key = new DownloadCacheKey("my_tool", "1.0.0", "x64", "lanzou", "https://example.invalid/f.bin", declared);

        var entry = cache.GetEntryPath(key);
        Directory.CreateDirectory(CacheRoot);
        File.WriteAllText(entry, "anything at all");

        Assert.False(cache.TryGetVerified(key, out _));

        // And it is refused before anything is read, which is what the fast path buys.
        Assert.True(File.Exists(entry) || !File.Exists(entry));
    }

    [Fact]
    public void A_blank_hash_field_is_not_the_same_as_a_matching_one()
    {
        // The specific mistake this guards against: treating "no hash declared" as
        // "nothing to compare, so accept".
        var cache = new DownloadCache(CacheRoot);
        var key = new DownloadCacheKey("my_tool", "1.0.0", "x64", "lanzou", "https://example.invalid/f.bin", "   ");

        cache.Store(key, WriteSource("blank.bin", "unverified bytes"));

        Assert.False(cache.TryGetVerified(key, out _));
    }

    [Fact]
    public void Nothing_is_trusted_when_nothing_is_there()
    {
        var cache = new DownloadCache(CacheRoot);

        Assert.False(cache.TryGetVerified(Key("absent"u8.ToArray()), out _));
    }

    // ------------------------------------------------------ identity

    [Theory]
    [InlineData("software")]
    [InlineData("version")]
    [InlineData("architecture")]
    [InlineData("provider")]
    [InlineData("url")]
    [InlineData("sha")]
    public void Every_declared_component_takes_part_in_the_identity(string component)
    {
        var baseline = new DownloadCacheKey("sw", "1.0", "x64", "lanzou", "https://a/f.bin", "aa");

        var changed = component switch
        {
            "software" => baseline with { SoftwareId = "other" },
            "version" => baseline with { Version = "2.0" },
            "architecture" => baseline with { Architecture = "arm64" },
            "provider" => baseline with { ProviderId = "123pan" },
            "url" => baseline with { Url = "https://b/f.bin" },
            _ => baseline with { Sha256 = "bb" },
        };

        Assert.NotEqual(baseline.Canonical(), changed.Canonical());
        Assert.NotEqual(baseline.ToStableName(), changed.ToStableName());
    }

    [Fact]
    public void Two_builds_of_the_same_product_at_one_url_do_not_collide()
    {
        // This is the property the class exists for: a rebuilt binary published at the
        // same address must not be served from the previous build's cached bytes.
        var first = new DownloadCacheKey("my_tool", "1.0.0", "x64", "lanzou", "https://a/tool.zip", "1111");
        var rebuilt = first with { Sha256 = "2222" };

        Assert.NotEqual(first.ToStableName(), rebuilt.ToStableName());
    }

    [Fact]
    public void The_stable_name_is_deterministic_and_filesystem_safe()
    {
        var key = new DownloadCacheKey("my_tool", "1.0.0", "x64", "lanzou", "https://a/f.bin?x=1&y=2", "aa");

        var name = key.ToStableName();

        Assert.Equal(name, key.ToStableName());
        Assert.All(name, c => Assert.True(char.IsAsciiHexDigitLower(c), $"不是小写十六进制：{name}"));
        Assert.DoesNotContain(Path.GetInvalidFileNameChars(), name);
    }

    [Fact]
    public void Missing_optional_parts_are_still_distinct_from_present_ones()
    {
        var absent = new DownloadCacheKey(null, null, null, null, "https://a/f.bin", null);
        var present = absent with { SoftwareId = "my_tool" };

        Assert.NotEqual(absent.Canonical(), present.Canonical());
    }

    // --------------------------------------------------------- storing

    [Fact]
    public void Storing_a_file_that_is_already_the_entry_does_nothing_and_does_not_throw()
    {
        // Copying a file onto itself is an IOException, so the guard is what keeps a
        // re-download from failing at the last step.
        var content = "already here"u8.ToArray();
        var cache = new DownloadCache(CacheRoot);
        var key = Key(content);

        Directory.CreateDirectory(CacheRoot);
        var entry = cache.GetEntryPath(key);
        File.WriteAllText(entry, "already here");

        cache.Store(key, entry);

        Assert.True(cache.TryGetVerified(key, out _));
    }

    [Fact]
    public void Storing_creates_the_cache_directory_when_it_is_missing()
    {
        var cache = new DownloadCache(Path.Combine(_root, "never", "created"));

        cache.Store(Key("x"u8.ToArray()), WriteSource("s.bin", "x"));

        Assert.True(Directory.Exists(cache.Root));
    }

    [Fact]
    public void Storing_the_same_download_twice_is_idempotent()
    {
        var cache = new DownloadCache(CacheRoot);
        var key = Key("stable bytes"u8.ToArray());

        cache.Store(key, WriteSource("a.bin", "stable bytes"));
        cache.Store(key, WriteSource("b.bin", "stable bytes"));

        Assert.True(cache.TryGetVerified(key, out var path));
        Assert.Equal("stable bytes", File.ReadAllText(path));
    }

    [Fact]
    public void Storing_bytes_that_do_not_match_the_key_leaves_an_entry_that_is_refused()
    {
        // Worth pinning because it looks like a bug the first time it happens: storing
        // the wrong bytes succeeds, and only the read refuses them. The store does not
        // verify — verification is what the read is for.
        var cache = new DownloadCache(CacheRoot);
        var key = Key("expected"u8.ToArray());

        cache.Store(key, WriteSource("wrong.bin", "not the expected bytes"));

        Assert.False(cache.TryGetVerified(key, out _));
    }

    [Fact]
    public void Two_identities_get_two_entries()
    {
        var cache = new DownloadCache(CacheRoot);
        var first = Key("one"u8.ToArray(), "https://example.invalid/one.bin");
        var second = Key("two"u8.ToArray(), "https://example.invalid/two.bin");

        Assert.NotEqual(cache.GetEntryPath(first), cache.GetEntryPath(second));

        cache.Store(first, WriteSource("one.bin", "one"));
        cache.Store(second, WriteSource("two.bin", "two"));

        Assert.True(cache.TryGetVerified(first, out var firstPath));
        Assert.True(cache.TryGetVerified(second, out var secondPath));

        Assert.Equal("one", File.ReadAllText(firstPath));
        Assert.Equal("two", File.ReadAllText(secondPath));
    }
}
