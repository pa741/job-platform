using JobPlatform.Core.Model;
using Xunit;

namespace JobPlatform.Core.Tests;

/// <summary>
/// The one rule that decides how old a posting is.
/// </summary>
/// <remarks>
/// Small, and it earns its place on the asymmetry. Believing the stated date and falling back to
/// first-seen looks like a detail until a search term is added: the scrape then delivers hundreds
/// of postings that are new to this system and weeks old to the market, and which of those two
/// columns is asked decides whether a daily run treats them as today's work. Both directions are
/// pinned here, and the relational spellings in <c>PostingRecency</c> are pinned against this one.
/// </remarks>
public sealed class PostingAgeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 3, 30, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Yesterday = Now.AddDays(-1);

    [Fact]
    public void The_stated_date_is_believed_where_the_board_published_one()
    {
        // First seen this morning, and the board says it went up three weeks ago. It is three
        // weeks old: a scrape learning about a posting is not the posting appearing.
        Assert.False(PostingAge.PostedSince(
            new DateOnly(2026, 8, 18), firstSeenUtc: Now, cutoff: Yesterday));

        Assert.True(PostingAge.PostedSince(
            new DateOnly(2026, 9, 8), firstSeenUtc: Now, cutoff: Yesterday));
    }

    [Fact]
    public void Silence_falls_back_to_when_this_system_first_read_the_posting()
    {
        // Three postings in five say nothing about when they were posted. Treating that silence
        // as "old" would empty the filter that matters most; treating it as "new" would let a
        // re-scrape make the whole corpus today's work. First-seen is the fact held first-hand.
        Assert.True(PostingAge.PostedSince(null, firstSeenUtc: Now, cutoff: Yesterday));
        Assert.False(PostingAge.PostedSince(null, firstSeenUtc: Now.AddDays(-9), cutoff: Yesterday));
    }

    [Fact]
    public void The_bound_is_inclusive_on_the_day_and_on_the_instant()
    {
        // A date compared against a cutoff instant is compared as a date, so the whole of the
        // cutoff's day is inside the window. That is why the daily window is three days and not
        // one: a day is the smallest unit the stated column can be compared in at all.
        Assert.True(PostingAge.PostedSince(
            DateOnly.FromDateTime(Yesterday.UtcDateTime),
            firstSeenUtc: Now,
            cutoff: Yesterday));

        Assert.True(PostingAge.PostedSince(null, firstSeenUtc: Yesterday, cutoff: Yesterday));
    }

    [Fact]
    public void The_posted_date_is_the_stated_one_or_the_day_it_was_first_seen()
    {
        Assert.Equal(
            new DateOnly(2026, 8, 18),
            PostingAge.PostedOn(new DateOnly(2026, 8, 18), Now));

        Assert.Equal(new DateOnly(2026, 9, 8), PostingAge.PostedOn(null, Now));
    }

    [Fact]
    public void A_window_that_runs_backwards_is_read_as_no_window_rather_than_as_an_error()
    {
        // The callers refuse a negative window before they get here - a run told "nothing today"
        // cannot tell a mistyped argument from an empty market. This is the floor under that, so
        // that one path being sloppy cannot produce a cutoff in the future that hides everything.
        Assert.Equal(Now, PostingAge.Cutoff(Now, -5));
        Assert.Equal(Now, PostingAge.Cutoff(Now, 0));
        Assert.Equal(Now.AddDays(-3), PostingAge.Cutoff(Now, PostingAge.DailyWindowDays));
    }
}
