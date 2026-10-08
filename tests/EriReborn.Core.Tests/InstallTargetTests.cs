using EriReborn.App.Shared;
using EriReborn.App.Shared.Services;
using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Core.Paths;
using EriReborn.Core.Tests.TestSupport;
using EriReborn.Engine.Software;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// Where a piece of software is going, said before anything is installed.
///
/// <para>
/// The confirmation card used to name the directory and nothing else, and it only appeared after
/// 「准备安装」 — so the two things a person actually needs to decide ("does this fit on that disk" and
/// "is something already there that I would be overwriting") were not on screen at all. These tests pin
/// the readings, pin that a reading which could not be taken stays unknown rather than becoming zero,
/// and pin that an entry can be pointed at a directory of the user's own choosing and back again.
/// </para>
/// </summary>
public sealed class InstallTargetTests
{
    private static SoftwareDefinition Software() => new()
    {
        Id = "dummy_tool",
        Name = "Dummy Tool",
        CategoryId = "Development",
        DirectoryName = "DummyTool",
        Sources = new[]
        {
            new SoftwareSource { Kind = SourceKind.HttpUrl, Url = "https://example.test/dummy.exe", SizeBytes = 1_200_000_000 },
        },
    };

    private static (SoftwareEngine Engine, TestFileSystemService Files) BuildEngine(string scratch)
    {
        var files = new TestFileSystemService(scratch);
        var platform = new TestPlatform(files, new TestNetworkService(new HttpClient()), new InMemoryCredentialStore());

        var engine = new SoftwareEngine(
            platform,
            new PathResolver(),
            new InstallationRegistry(Path.Combine(scratch, "i.json"), AppLog.For("Test")),
            new DetectionHintStore(Path.Combine(scratch, "h.json"), AppLog.For("Test")),
            AppLog.For("Test"));

        return (engine, files);
    }

    [Fact]
    public void The_plan_carries_free_space_whether_the_directory_exists_and_the_declared_size()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        var (engine, files) = BuildEngine(scratch);
        files.FreeSpaceBytes = 5_000_000_000;

        var context = new EnvironmentContext { RootPath = scratch, IncludeSubcategoryFolder = false };
        var target = new PathResolver().ResolveSoftwareDirectory(context, Software());

        // A folder that is already there is what "this will write over things" means.
        Directory.CreateDirectory(target);

        try
        {
            var plan = engine.Plan(Software(), context);

            Assert.Equal(target, plan.TargetDirectory);
            Assert.Equal(5_000_000_000, plan.FreeSpaceBytes);
            Assert.True(plan.TargetExists);
            Assert.Equal(1_200_000_000, plan.ExpectedSizeBytes);
            Assert.False(plan.FreeSpaceInsufficient);
            Assert.True(plan.PathValid, plan.PathProblem);
        }
        finally
        {
            TryDelete(scratch);
        }
    }

    [Fact]
    public void A_folder_that_is_not_there_yet_is_reported_as_such()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        var (engine, _) = BuildEngine(scratch);

        var context = new EnvironmentContext { RootPath = Path.Combine(scratch, "never-created"), IncludeSubcategoryFolder = false };

        try
        {
            Assert.False(engine.Plan(Software(), context).TargetExists);
        }
        finally
        {
            TryDelete(scratch);
        }
    }

    [Fact]
    public void An_unreadable_free_space_is_not_reported_as_no_space()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        var (engine, files) = BuildEngine(scratch);

        // The platform cannot tell, so the plan must not claim the disk is full.
        files.FreeSpaceBytes = null;

        var context = new EnvironmentContext { RootPath = scratch, IncludeSubcategoryFolder = false };

        try
        {
            var plan = engine.Plan(Software(), context);

            Assert.Null(plan.FreeSpaceBytes);
            Assert.False(plan.FreeSpaceInsufficient);
        }
        finally
        {
            TryDelete(scratch);
        }
    }

    [Fact]
    public void A_download_bigger_than_the_volume_is_flagged()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        var (engine, files) = BuildEngine(scratch);
        files.FreeSpaceBytes = 100;

        var context = new EnvironmentContext { RootPath = scratch, IncludeSubcategoryFolder = false };

        try
        {
            Assert.True(engine.Plan(Software(), context).FreeSpaceInsufficient);
        }
        finally
        {
            TryDelete(scratch);
        }
    }

    [Fact]
    public void A_size_nobody_declared_stays_unknown_rather_than_being_guessed()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        var (engine, files) = BuildEngine(scratch);
        files.FreeSpaceBytes = 10;

        var software = Software() with
        {
            Sources = new[] { new SoftwareSource { Kind = SourceKind.HttpUrl, Url = "https://example.test/x.exe" } },
        };

        var context = new EnvironmentContext { RootPath = scratch, IncludeSubcategoryFolder = false };

        try
        {
            var plan = engine.Plan(software, context);

            Assert.Null(plan.ExpectedSizeBytes);

            // Without a size there is no evidence of a problem, so nothing is flagged.
            Assert.False(plan.FreeSpaceInsufficient);
        }
        finally
        {
            TryDelete(scratch);
        }
    }

    [Fact]
    public void One_entry_can_be_pointed_at_its_own_directory()
    {
        var context = new EnvironmentContext
        {
            RootPath = @"E:\EriReborn",
            IncludeSubcategoryFolder = false,
            SoftwarePathOverrides = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["dummy_tool"] = @"D:\MyTools\DummyTool",
            },
        };

        var resolver = new PathResolver();

        // The chosen directory replaces the whole resolved path for that one id...
        Assert.Equal(@"D:\MyTools\DummyTool", resolver.ResolveSoftwareDirectory(context, Software()));

        // ...and nothing else moves.
        var other = Software() with { Id = "other_tool", DirectoryName = "OtherTool" };
        Assert.Equal(@"E:\EriReborn\Development\OtherTool", resolver.ResolveSoftwareDirectory(context, other));
    }

    [Fact]
    public void A_chosen_directory_is_remembered_and_can_be_taken_back()
    {
        var userData = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));

        try
        {
            var paths = AppPaths.Detect(userDataOverride: userData);
            var first = new UserConfigService(paths, AppLog.For("Test"));
            first.Load();

            first.SetSoftwarePathOverride("dummy_tool", @"D:\MyTools\DummyTool");

            // A fresh service reads the file again, as a restart would.
            var second = new UserConfigService(paths, AppLog.For("Test"));
            var reloaded = second.Load();

            Assert.Equal(@"D:\MyTools\DummyTool", reloaded.SoftwarePathOverrides["dummy_tool"]);

            // Blank clears it, which is what 「恢复默认位置」 means.
            var cleared = first.SetSoftwarePathOverride("dummy_tool", "   ");

            Assert.False(cleared.SoftwarePathOverrides.ContainsKey("dummy_tool"));

            var third = new UserConfigService(paths, AppLog.For("Test"));
            Assert.False(third.Load().SoftwarePathOverrides.ContainsKey("dummy_tool"));
        }
        finally
        {
            TryDelete(userData);
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Best effort: a leftover temp directory must not fail a test.
        }
    }
}
