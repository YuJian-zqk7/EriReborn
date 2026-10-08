using System.Net;
using EriReborn.Engine.Download;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// What to do with a partial file when the server answers a range request.
///
/// <para>
/// Found the same way as the msiexec table: the gap document claimed "206 append / 200
/// restart from zero" as implemented, and the decision was a private method no test
/// could reach. Getting it wrong is not a visible error — it produces a file with the
/// beginning written twice, which downloads fine and then fails its hash.
/// </para>
/// </summary>
public sealed class RangeDecisionTests
{
    [Fact]
    public void With_nothing_downloaded_there_is_nothing_to_resume()
    {
        // No partial file means the question does not arise, whatever the server said.
        Assert.Equal(RangeDecision.NoResumePossible, HttpDownloader.DecideRange(0, HttpStatusCode.PartialContent));
        Assert.Equal(RangeDecision.NoResumePossible, HttpDownloader.DecideRange(0, HttpStatusCode.OK));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-1024)]
    public void A_nonsense_partial_size_is_not_treated_as_progress(long existing)
    {
        Assert.Equal(RangeDecision.NoResumePossible, HttpDownloader.DecideRange(existing, HttpStatusCode.PartialContent));
    }

    [Fact]
    public void A_server_that_honours_the_range_lets_the_download_continue()
    {
        Assert.Equal(RangeDecision.ResumeAccepted, HttpDownloader.DecideRange(1024, HttpStatusCode.PartialContent));
    }

    [Fact]
    public void A_server_that_ignores_the_range_makes_the_download_start_over()
    {
        // The bug this exists to prevent: 200 carries the whole body from byte zero, and
        // appending it to a partial file duplicates the beginning. The file then downloads
        // successfully and fails its hash, which is the most confusing shape a failure can
        // take.
        Assert.Equal(RangeDecision.RestartRequired, HttpDownloader.DecideRange(1024, HttpStatusCode.OK));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public void Anything_else_means_the_partial_file_is_not_usable(HttpStatusCode status)
    {
        Assert.Equal(RangeDecision.NoResumePossible, HttpDownloader.DecideRange(1024, status));
    }

    [Fact]
    public void The_three_outcomes_are_distinct()
    {
        var decisions = new[]
        {
            HttpDownloader.DecideRange(0, HttpStatusCode.OK),
            HttpDownloader.DecideRange(1024, HttpStatusCode.PartialContent),
            HttpDownloader.DecideRange(1024, HttpStatusCode.OK),
        };

        Assert.Equal(3, decisions.Distinct().Count());
    }

    [Fact]
    public void The_decision_does_not_depend_on_how_much_was_downloaded()
    {
        // Only "some or none" matters. A size-dependent rule would be a rule nobody could
        // reason about.
        foreach (var existing in new long[] { 1, 1024, 1024L * 1024 * 1024 })
        {
            Assert.Equal(RangeDecision.ResumeAccepted, HttpDownloader.DecideRange(existing, HttpStatusCode.PartialContent));
            Assert.Equal(RangeDecision.RestartRequired, HttpDownloader.DecideRange(existing, HttpStatusCode.OK));
        }
    }
}
