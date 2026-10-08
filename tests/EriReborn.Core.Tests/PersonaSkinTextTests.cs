using EriReborn.App.Shared;
using EriReborn.App.Shared.Services;
using EriReborn.App.Shared.ViewModels;
using EriReborn.Cloud;
using EriReborn.Core.Logging;
using EriReborn.Core.Tests.TestSupport;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// A skin may reword a persona line, and the interface has to say the reworded line.
///
/// <para>
/// The two systems are separate on purpose (spec 57): the persona is how the application speaks, the
/// skin is how it looks. The override therefore rides in the skin's own texts under a persona. prefix,
/// which means editing a line changes the <em>skin</em> while the persona pack stays exactly the same.
/// Re-activating an unchanged pack returns early and raises nothing, so a page that only listened to the
/// persona kept showing the old wording: the edit was written to disk and never spoken.
/// </para>
/// </summary>
public sealed class PersonaSkinTextTests
{
    private const string EditedLine = "我的问候";

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "assets", "skins")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("找不到仓库根目录。");
    }

    private static Task<AppHost> StartAsync(string userData)
        => AppHost.CreateAsync(
            AppPaths.Detect(assetsOverride: Path.Combine(RepoRoot(), "assets"), userDataOverride: userData),
            new TestPlatform(
                new TestFileSystemService(userData),
                new TestNetworkService(new HttpClient()),
                new InMemoryCredentialStore()),
            new CloudProviderRegistry(Array.Empty<ICloudProvider>(), AppLog.For("Test")),
            AppLog.For("Test"));

    private static string NewUserData()
        => Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Editing_a_persona_line_changes_what_the_page_says()
    {
        var userData = NewUserData();
        var host = await StartAsync(userData);
        var shell = new MainViewModel(host, new NavigationService());

        // The shell's own workshop: it is the one whose requests the shell listens to.
        var workshop = shell.Workshop;

        workshop.SelectedSkin = workshop.Skins.Single(entry => entry.Id == "eri_windows");
        workshop.BeginCreateSkinCommand.Execute(null);
        workshop.NewSkinName = "会说新台词的皮肤";
        workshop.CreateSkinCommand.Execute(null);

        // The override is a skin override, so it is the user's skin that has to be in force for it to
        // be the wording the interface reads.
        workshop.ActivateSkinCommand.Execute(workshop.SelectedSkin);

        Assert.NotEqual(EditedLine, shell.Home.PersonaGreeting);

        // Exactly what the editor offers and what the user does: change the line, press 保存.
        var row = workshop.PersonaTextEntries.Single(entry => entry.Key == "persona.overview.greeting");
        row.Value = EditedLine;
        workshop.SaveAllCommand.Execute(null);

        // The save must have landed — otherwise this test would pass by asserting nothing.
        Assert.Equal(EditedLine, workshop.PersonaTextEntries.Single(
            entry => entry.Key == "persona.overview.greeting").Value);

        Assert.Equal(EditedLine, shell.Home.PersonaGreeting);
    }
}
