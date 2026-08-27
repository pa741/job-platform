using JobPlatform.Ingestion;
using Xunit;

namespace JobPlatform.Ingestion.Tests;

/// <summary>
/// The loop bound on the reprocess endpoint, which is the thing that has actually broken.
/// </summary>
/// <remarks>
/// This is the category that has caused real incidents here more than once: the batch collector
/// returned 504 three times before it was bounded, and the first version of this walk was itself
/// found by a 504 in production while re-enriching the corpus. Every test below is a shape that
/// happened rather than one that could.
///
/// Nothing here touches Azure. <c>BoundedWalk</c> takes pages and a clock, so the bound can be
/// asserted exactly - which is why it exists apart from the function at all.
/// </remarks>
public sealed class BoundedWalkTests
{
    /// <summary>A clock the test moves by hand, so timings are exact rather than slept for.</summary>
    private sealed class FakeClock
    {
        public TimeSpan Now { get; private set; }

        public TimeSpan Elapsed() => Now;

        public void Advance(TimeSpan by) => Now += by;
    }

    private static async IAsyncEnumerable<WalkPage<string>> Pages(params string[][] pages)
    {
        for (var i = 0; i < pages.Length; i++)
        {
            // The last page carries no token, which is how a listing says it is exhausted.
            var token = i == pages.Length - 1 ? null : $"token-{i + 1}";
            yield return new WalkPage<string>(pages[i], token);
            await Task.Yield();
        }
    }

    [Fact]
    public async Task A_page_costing_more_than_the_whole_budget_is_interrupted_part_way()
    {
        // The production shape exactly. Pages of five blobs took 4s, 11s, 12s, 47s and 151s; the
        // check at the end of the fourth passed at 74s, and the fifth then ran the call to ~225s
        // where the gateway gave up. The budget could not act because the only place it was read
        // was a boundary, and the expensive work sat between two of them.
        var clock = new FakeClock();
        var seen = new List<string>();

        var costs = new Queue<TimeSpan>(new[]
        {
            // Four cheap pages, leaving the clock at 120s of a 150s budget.
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30),
            // Then the page whose items cost more than the budget has left.
            TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60),
        });

        var outcome = await BoundedWalk.RunAsync(
            Pages(["a"], ["b"], ["c"], ["d"], ["e1", "e2", "e3"], ["f"]),
            startToken: null,
            limit: 100,
            budget: TimeSpan.FromSeconds(150),
            clock.Elapsed,
            (item, _) =>
            {
                seen.Add(item);
                clock.Advance(costs.Count > 0 ? costs.Dequeue() : TimeSpan.FromSeconds(1));
                return Task.CompletedTask;
            });

        Assert.False(outcome.Exhausted);
        Assert.True(outcome.StoppedMidPage);

        // It stopped inside the expensive page rather than running it out.
        Assert.Equal(["a", "b", "c", "d", "e1"], seen);

        // The resume point is the boundary *before* that page, so nothing is skipped. e1 is
        // handed over again next time, which ingestion's idempotency makes free - that is the
        // trade this design accepts, and the reason it can stop between items at all.
        Assert.Equal("token-4", outcome.ResumeToken);

        // The guarantee, stated exactly: the overshoot past the budget is one item, not one
        // page. That is the whole difference. The old walk was committed to five more blobs
        // every time the check passed, which is how 149s became 225s and then a 504; this one
        // is committed to whichever blob it is already inside.
        Assert.Equal(TimeSpan.FromSeconds(180), clock.Now);
    }

    [Fact]
    public async Task A_single_item_slower_than_the_budget_is_still_not_interruptible()
    {
        // The residual limit, pinned so it is a known quantity rather than a surprise. The walk
        // stops between items, so an item that alone outlasts the budget still overshoots by its
        // own duration. Bounding that would mean cancelling mid-blob and giving up the
        // idempotent-write property that makes resuming safe. Left as is deliberately: the
        // margin between the 150s budget and the gateway's ~230s is what absorbs it.
        var clock = new FakeClock();

        var outcome = await BoundedWalk.RunAsync(
            Pages(["a"], ["b"], ["c"]),
            startToken: null,
            limit: 100,
            budget: TimeSpan.FromSeconds(150),
            clock.Elapsed,
            (_, _) =>
            {
                clock.Advance(TimeSpan.FromSeconds(400));
                return Task.CompletedTask;
            });

        Assert.False(outcome.Exhausted);
        Assert.Equal(TimeSpan.FromSeconds(400), clock.Now);
        Assert.Equal("token-1", outcome.ResumeToken);
    }

    [Fact]
    public async Task The_first_page_is_finished_even_when_it_blows_the_budget()
    {
        // Stopping here would hand back the token the call arrived with, and the next call -
        // with a fresh clock - would stop in the same place. Forever. Where one page cannot fit
        // in the budget the only useful thing to do is finish it.
        var clock = new FakeClock();
        var seen = new List<string>();

        var outcome = await BoundedWalk.RunAsync(
            Pages(["a1", "a2", "a3"], ["b"]),
            startToken: "where-we-left-off",
            limit: 100,
            budget: TimeSpan.FromSeconds(150),
            clock.Elapsed,
            (item, _) =>
            {
                seen.Add(item);
                clock.Advance(TimeSpan.FromSeconds(200));
                return Task.CompletedTask;
            });

        Assert.Equal(["a1", "a2", "a3"], seen);
        Assert.False(outcome.StoppedMidPage);

        // Progress was made, so the caller advances rather than repeating itself.
        Assert.Equal("token-1", outcome.ResumeToken);
        Assert.NotEqual("where-we-left-off", outcome.ResumeToken);
        Assert.False(outcome.Exhausted);
    }

    [Fact]
    public async Task Exhausting_the_listing_is_the_only_thing_that_reports_done()
    {
        var clock = new FakeClock();

        var outcome = await BoundedWalk.RunAsync(
            Pages(["a"], ["b"]),
            startToken: null,
            limit: 100,
            budget: TimeSpan.FromSeconds(150),
            clock.Elapsed,
            (_, _) => Task.CompletedTask);

        Assert.True(outcome.Exhausted);
        Assert.Null(outcome.ResumeToken);
        Assert.Equal(2, outcome.Processed);
    }

    [Fact]
    public async Task A_truncated_walk_is_never_reported_as_done()
    {
        // The distinction the caller's whole loop rests on. A null token means "start from the
        // beginning" as well as "nothing left", so a caller that infers completion from the token
        // being absent stops early and silently leaves the container half processed.
        var clock = new FakeClock();

        var outcome = await BoundedWalk.RunAsync(
            Pages(["a"], ["b"], ["c"]),
            startToken: null,
            limit: 1,
            budget: TimeSpan.FromSeconds(150),
            clock.Elapsed,
            (_, _) => Task.CompletedTask);

        Assert.False(outcome.Exhausted);
        Assert.Equal("token-1", outcome.ResumeToken);
        Assert.Equal(1, outcome.Processed);
    }

    [Fact]
    public async Task The_count_bound_stops_the_walk_on_a_boundary()
    {
        var clock = new FakeClock();
        var seen = new List<string>();

        var outcome = await BoundedWalk.RunAsync(
            Pages(["a1", "a2"], ["b1", "b2"], ["c1", "c2"]),
            startToken: null,
            limit: 2,
            budget: TimeSpan.FromSeconds(150),
            clock.Elapsed,
            (item, _) =>
            {
                seen.Add(item);
                return Task.CompletedTask;
            });

        Assert.Equal(["a1", "a2"], seen);
        Assert.False(outcome.StoppedMidPage);
        Assert.Equal("token-1", outcome.ResumeToken);
        Assert.False(outcome.Exhausted);
    }

    [Fact]
    public async Task Resuming_from_each_token_covers_every_item_exactly_once()
    {
        // What the caller's "keep calling until done" loop actually has to guarantee. Driven the
        // way the real one is, against a budget that interrupts, the walk must cover the whole
        // listing - with repeats allowed, never gaps.
        var all = new[] { "a1", "a2", "b1", "b2", "c1", "c2", "d1", "d2" };
        var covered = new List<string>();
        string? token = null;
        var calls = 0;

        while (calls++ < 20)
        {
            var clock = new FakeClock();

            var outcome = await BoundedWalk.RunAsync(
                Skip(Pages(["a1", "a2"], ["b1", "b2"], ["c1", "c2"], ["d1", "d2"]), token),
                token,
                limit: 100,
                budget: TimeSpan.FromSeconds(10),
                clock.Elapsed,
                (item, _) =>
                {
                    covered.Add(item);
                    clock.Advance(TimeSpan.FromSeconds(6));
                    return Task.CompletedTask;
                });

            token = outcome.ResumeToken;

            if (outcome.Exhausted)
            {
                break;
            }
        }

        Assert.True(calls < 20, "the walk never reported done");
        Assert.Equal(all, covered.Distinct().ToArray());
    }

    /// <summary>Replays a listing from the page a token names, the way a real listing does.</summary>
    private static async IAsyncEnumerable<WalkPage<string>> Skip(
        IAsyncEnumerable<WalkPage<string>> pages, string? token)
    {
        if (token is null)
        {
            await foreach (var page in pages)
            {
                yield return page;
            }

            yield break;
        }

        // "token-N" resumes at the page after N-1, which is page N.
        var index = int.Parse(token.Split('-')[1], System.Globalization.CultureInfo.InvariantCulture);
        var current = 0;

        await foreach (var page in pages)
        {
            if (current++ >= index)
            {
                yield return page;
            }
        }
    }
}
