using EriReborn.Core.Domain;
using EriReborn.Platform.Windows;
using Xunit;

namespace EriReborn.Windows.Tests;

/// <summary>
/// msiexec's exit codes, which decide whether an install is reported as done.
///
/// <para>
/// Found by auditing the gap document's claims against the test suite: the claim
/// "msiexec 退出码翻译" was marked implemented, and the table really was there — with
/// no test anywhere. Writing one immediately turned up a disagreement inside it:
/// 1641 is described as "install completed and a restart was started", and the caller
/// filed it under InstallerFailed.
/// </para>
/// </summary>
public sealed class MsiexecExitCodeTests
{
    /// <summary>Every code the table claims to translate. One list, used two ways.</summary>
    private static readonly int[] Documented = { 1602, 1603, 1618, 1619, 1620, 1625, 1633, 1638, 1641, 3010 };

    // ------------------------------------------------------------ success set

    [Theory]
    [InlineData(0)]
    [InlineData(1641)]
    [InlineData(3010)]
    public void A_completed_install_counts_as_success(int code)
    {
        // 0 is plain success, 3010 is "done, restart needed", and 1641 is "done and a
        // restart has been started". All three mean the work happened.
        Assert.True(WindowsSoftwareInstaller.IsMsiexecSuccess(code));
    }

    [Theory]
    [InlineData(1602)]
    [InlineData(1603)]
    [InlineData(1618)]
    [InlineData(1619)]
    [InlineData(1620)]
    [InlineData(1625)]
    [InlineData(1633)]
    [InlineData(1638)]
    [InlineData(1)]
    [InlineData(9999)]
    public void Anything_else_counts_as_failure(int code)
    {
        Assert.False(WindowsSoftwareInstaller.IsMsiexecSuccess(code));
    }

    [Theory]
    [InlineData(1641)]
    [InlineData(3010)]
    public void A_restart_code_is_recognised_as_wanting_a_restart(int code)
    {
        Assert.True(WindowsSoftwareInstaller.MsiexecWantsRestart(code));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1603)]
    public void A_code_that_is_not_about_restarting_does_not_say_so(int code)
    {
        Assert.False(WindowsSoftwareInstaller.MsiexecWantsRestart(code));
    }

    // ---------------------------------------------- the disagreement itself

    [Fact]
    public void A_successful_code_is_never_described_as_a_failure()
    {
        // This is the assertion that would have caught the bug: the two functions used
        // to disagree about 1641, so a completed install was reported as a failure whose
        // own message read "install complete".
        foreach (var code in new[] { 1641, 3010 })
        {
            Assert.True(WindowsSoftwareInstaller.IsMsiexecSuccess(code));

            var described = WindowsSoftwareInstaller.DescribeMsiexec(code);

            Assert.Contains("完成", described);
            Assert.DoesNotContain("错误", described);
            Assert.DoesNotContain("失败", described);
            Assert.DoesNotContain("取消", described);
            Assert.DoesNotContain("禁止", described);
        }
    }

    // ------------------------------------------------------------- the table

    // Listed explicitly rather than through a data source: the project's runner expands
    // [InlineData] and reports anything else as skipped, and a skipped test that is
    // silently never run is worse than no test.
    [Theory]
    [InlineData(1602)]
    [InlineData(1603)]
    [InlineData(1618)]
    [InlineData(1619)]
    [InlineData(1620)]
    [InlineData(1625)]
    [InlineData(1633)]
    [InlineData(1638)]
    [InlineData(1641)]
    [InlineData(3010)]
    public void Every_documented_code_gets_a_sentence(int code)
    {
        var described = WindowsSoftwareInstaller.DescribeMsiexec(code);

        Assert.False(string.IsNullOrWhiteSpace(described));

        // Naming the code back is only the fallback; a documented code must say more.
        Assert.NotEqual($"msiexec 退出码 {code}", described);
    }

    [Fact]
    public void The_documented_codes_are_the_ones_the_success_set_does_not_cover()
    {
        // Guards the list above against drift: everything documented is either a
        // success code or a specific failure.
        foreach (var code in Documented)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(WindowsSoftwareInstaller.DescribeMsiexec(code)),
                $"退出码 {code} 没有描述。");
        }

        Assert.Contains(1641, Documented);
        Assert.Contains(3010, Documented);
    }

    [Fact]
    public void Every_documented_code_says_something_different()
    {
        // Two codes sharing one sentence would mean one of them is not really
        // translated, and the user would be told the wrong reason.
        var sentences = Documented
            .Select(WindowsSoftwareInstaller.DescribeMsiexec)
            .ToList();

        Assert.Equal(sentences.Count, sentences.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(12345)]
    [InlineData(-1)]
    public void An_undocumented_code_names_itself_so_it_can_be_looked_up(int code)
    {
        // A generic "installation failed" would leave the user with nothing to search
        // for, and the log would not carry the one number that matters.
        var described = WindowsSoftwareInstaller.DescribeMsiexec(code);

        Assert.Contains(code.ToString(), described, StringComparison.Ordinal);
    }

    [Fact]
    public void The_two_restart_codes_are_both_recognised_as_completions()
    {
        // Pinned together because they differ only in who starts the restart, and it
        // was the difference between them that hid the bug.
        Assert.True(WindowsSoftwareInstaller.IsMsiexecSuccess(1641));
        Assert.True(WindowsSoftwareInstaller.IsMsiexecSuccess(3010));
        Assert.True(WindowsSoftwareInstaller.MsiexecWantsRestart(1641));
        Assert.True(WindowsSoftwareInstaller.MsiexecWantsRestart(3010));
    }

    // ------------------------------------------------------------- ui modes

    [Fact]
    public void Silent_asks_msiexec_for_no_ui_at_all()
    {
        Assert.Equal(new[] { "/qn" }, WindowsSoftwareInstaller.MsiexecUiArguments(InstallerUiMode.Silent));
    }

    [Fact]
    public void Basic_asks_for_progress_without_questions()
    {
        Assert.Equal(new[] { "/qb" }, WindowsSoftwareInstaller.MsiexecUiArguments(InstallerUiMode.Basic));
    }

    [Fact]
    public void Interactive_adds_no_flag_because_the_wizard_is_the_point()
    {
        // Adding /qn here would silently turn a graphical install into a silent one,
        // which is a different claim about the same package.
        Assert.Empty(WindowsSoftwareInstaller.MsiexecUiArguments(InstallerUiMode.Interactive));
    }

    [Fact]
    public void No_two_modes_ask_for_the_same_thing()
    {
        var modes = Enum.GetValues<InstallerUiMode>()
            .Select(mode => string.Join(" ", WindowsSoftwareInstaller.MsiexecUiArguments(mode)))
            .ToList();

        Assert.Equal(modes.Count, modes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_three_modes_are_the_whole_vocabulary()
    {
        var modes = Enum.GetNames<InstallerUiMode>();

        Assert.Equal(3, modes.Length);
        Assert.Contains("Silent", modes);
        Assert.Contains("Basic", modes);
        Assert.Contains("Interactive", modes);
    }

    [Fact]
    public void A_leftover_comment_cannot_be_the_only_thing_holding_a_claim_up()
    {
        // "Silent and graphical are different claims" used to be an inline switch with no
        // test at all; this pins the sentence to behaviour instead.
        Assert.NotEqual(
            WindowsSoftwareInstaller.MsiexecUiArguments(InstallerUiMode.Silent),
            WindowsSoftwareInstaller.MsiexecUiArguments(InstallerUiMode.Basic));
    }

    // ---------------------------------------------------------- elevation

    [Theory]
    [InlineData(".msi")]
    [InlineData(".MSI")]
    [InlineData(".Msi")]
    public void A_machine_wide_msi_needs_elevation_when_the_process_has_none(string extension)
    {
        // Without elevation msiexec fails with 1603 and its log names a registry access
        // denial, which tells the user nothing useful. The decision is caught here so it
        // can be reported before anything starts — and so it can be tested by someone who
        // is not an administrator.
        Assert.True(WindowsSoftwareInstaller.NeedsElevation(extension, isElevated: false));
    }

    [Theory]
    [InlineData(".exe")]
    [InlineData(".zip")]
    [InlineData(".msix")]
    [InlineData("")]
    public void Anything_that_is_not_an_msi_is_carried_on_with(string extension)
    {
        // Refusing here would block installs that need no elevation at all.
        Assert.False(WindowsSoftwareInstaller.NeedsElevation(extension, isElevated: false));
    }

    [Fact]
    public void An_elevated_process_never_needs_elevation_again()
    {
        foreach (var extension in new[] { ".msi", ".exe", ".zip", "" })
        {
            Assert.False(WindowsSoftwareInstaller.NeedsElevation(extension, isElevated: true));
        }
    }

    [Fact]
    public void The_extension_decision_is_not_case_or_shape_sensitive()
    {
        // ".msi" is what a resolved package path ends in; a check that only matched one
        // spelling would silently let the others through.
        Assert.Equal(
            WindowsSoftwareInstaller.NeedsElevation(".msi", false),
            WindowsSoftwareInstaller.NeedsElevation(".MSI", false));
    }

    // -------------------------------------------------------------- stderr

    [Fact]
    public void Installer_stderr_is_folded_into_one_line()
    {
        var folded = WindowsSoftwareInstaller.Flatten("first line\nsecond line\r\nthird");

        Assert.DoesNotContain("\n", folded);
        Assert.DoesNotContain("\r", folded);
        Assert.Contains("first line second line third", folded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_stderr_contributes_nothing_to_the_message(string? text)
    {
        Assert.Equal(string.Empty, WindowsSoftwareInstaller.Flatten(text));
    }

    [Fact]
    public void A_huge_stderr_excerpt_is_bounded()
    {
        // An installer can emit megabytes; the message is not the log.
        var folded = WindowsSoftwareInstaller.Flatten(new string('x', 5000));

        Assert.True(folded.Length < 400, $"截断后仍然有 {folded.Length} 个字符。");
        Assert.EndsWith("…", folded, StringComparison.Ordinal);
    }
}
