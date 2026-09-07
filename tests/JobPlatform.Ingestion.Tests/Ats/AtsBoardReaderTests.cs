using JobPlatform.Core.Applications;
using JobPlatform.Ingestion.Ats;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JobPlatform.Ingestion.Tests.Ats;

/// <summary>
/// The promises a whole pass makes to somebody else's API.
/// </summary>
/// <remarks>
/// A board is asked for once however many postings named it, no more than a handful of requests
/// are in flight at any moment, and a vendor nobody has written a reader for costs no request at
/// all. None of the three can be asserted on a single client, which is why they are asserted here.
/// </remarks>
public sealed class AtsBoardReaderTests
{
    [Fact]
    public async Task One_board_is_fetched_once_however_many_postings_named_it()
    {
        // The measured shape: 309 link-less postings, 122 of them at an employer whose board is
        // already known - so one board is named by many postings. Cloudflare's answers 333 jobs in
        // one request, and asking it per posting would be 333 requests for those same bytes.
        var handler = new StubHandler(_ => RecordedBoards.Ok(RecordedBoards.GreenhouseBoard));
        var reader = Reader(Greenhouse(handler));

        var board = new AtsBoard(AtsVendor.Greenhouse, "cloudflare");

        var reads = await reader.ReadAsync(
            [board, board, new AtsBoard(AtsVendor.Greenhouse, "cloudflare")]);

        Assert.Single(reads);
        Assert.Single(handler.Requests);
        Assert.Equal(AtsBoardReadOutcome.Read, reads[board].Outcome);
    }

    [Fact]
    public async Task Two_spellings_of_one_token_are_two_boards_and_cost_two_requests()
    {
        // AtsBoard preserves a token's case because SmartRecruiters keys on a case-sensitive
        // company id. The consequence is deliberate and stated there: two spellings look like two
        // boards, which costs a duplicate request and never a wrong answer.
        var handler = new StubHandler(_ => RecordedBoards.Ok(RecordedBoards.SmartRecruitersBoard));
        var reader = Reader(SmartRecruiters(handler));

        var reads = await reader.ReadAsync(
        [
            new AtsBoard(AtsVendor.SmartRecruiters, "BlueOptima"),
            new AtsBoard(AtsVendor.SmartRecruiters, "blueoptima"),
        ]);

        Assert.Equal(2, reads.Count);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task No_more_than_the_configured_number_of_requests_are_ever_in_flight()
    {
        // The bound protects the vendor rather than us - the same argument
        // AtsBoardCandidates.MaxCandidates makes for its own number. It is asserted by holding every
        // request open until the test releases them, so the observed peak is the semaphore's doing
        // rather than a race that happened to finish quickly.
        var release = new TaskCompletionSource();
        var inFlight = 0;
        var peak = 0;

        var handler = new StubHandler(async (_, _) =>
        {
            RaiseTo(ref peak, Interlocked.Increment(ref inFlight));

            await release.Task;

            Interlocked.Decrement(ref inFlight);

            return RecordedBoards.Ok(RecordedBoards.GreenhouseEmptyBoard);
        });

        var reader = Reader(3, Greenhouse(handler));

        var boards = Enumerable
            .Range(0, 12)
            .Select(index => new AtsBoard(AtsVendor.Greenhouse, $"employer-{index}"))
            .ToArray();

        var pass = reader.ReadAsync(boards);

        await WaitUntil(() => Volatile.Read(ref inFlight) == 3);

        Assert.Equal(3, Volatile.Read(ref peak));

        release.SetResult();

        var reads = await pass;

        Assert.Equal(12, reads.Count);
        Assert.Equal(12, handler.Requests.Count);
        Assert.Equal(3, Volatile.Read(ref peak));
    }

    [Fact]
    public async Task A_vendor_with_no_reader_is_unavailable_and_never_not_a_board()
    {
        // Workable is the fifth vendor AtsBoard admits and the one with no reader. "Nobody here can
        // read Workable" is a fact about this repository; reported as "no such board" it would tell
        // a caller the employer's token was wrong, which is a conclusion it might store.
        var handler = new StubHandler(_ => RecordedBoards.Ok(RecordedBoards.GreenhouseBoard));
        var reader = Reader(Greenhouse(handler));

        var read = await reader.ReadAsync(new AtsBoard(AtsVendor.Workable, "someemployer"));

        Assert.Equal(AtsBoardReadOutcome.Unavailable, read.Outcome);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void CanRead_says_which_vendors_are_worth_spending_a_probe_budget_on()
    {
        // Asking costs nothing and answers the same thing four round trips would. Without it a
        // Workable employer costs AtsBoardCandidates.MaxCandidates requests to learn that nobody is
        // listening.
        var handler = new StubHandler(_ => RecordedBoards.Ok(RecordedBoards.GreenhouseBoard));

        var reader = Reader(
            Greenhouse(handler),
            Ashby(handler),
            Lever(handler),
            SmartRecruiters(handler));

        Assert.True(reader.CanRead(AtsVendor.Greenhouse));
        Assert.True(reader.CanRead(AtsVendor.Ashby));
        Assert.True(reader.CanRead(AtsVendor.Lever));
        Assert.True(reader.CanRead(AtsVendor.SmartRecruiters));

        // The two absences, and they are different kinds. Workable serves a public board and has no
        // reader yet; Workday has no clean public board listing at all, so AtsBoard's own
        // constructor refuses it and it can never reach here.
        Assert.False(reader.CanRead(AtsVendor.Workable));
        Assert.False(reader.CanRead(AtsVendor.Workday));
    }

    [Fact]
    public async Task One_vendor_being_down_costs_links_rather_than_the_whole_pass()
    {
        // A pass reads hundreds of boards in one invocation. The employers after the failing one
        // are what an exception would have cost, and nothing would have said so.
        var handler = new StubHandler(url => url.Contains("broken", StringComparison.Ordinal)
            ? throw new HttpRequestException("Connection refused.")
            : RecordedBoards.Ok(RecordedBoards.GreenhouseBoard));

        var reader = Reader(Greenhouse(handler));

        var working = new AtsBoard(AtsVendor.Greenhouse, "cloudflare");
        var broken = new AtsBoard(AtsVendor.Greenhouse, "broken");

        var reads = await reader.ReadAsync([broken, working]);

        Assert.Equal(AtsBoardReadOutcome.Unavailable, reads[broken].Outcome);
        Assert.Equal(AtsBoardReadOutcome.Read, reads[working].Outcome);
        Assert.NotEmpty(reads[working].Listings);
    }

    [Fact]
    public async Task A_cancelled_pass_answers_rather_than_throwing()
    {
        // Stopping is not a fact about the employer, and a caller that cancelled already knows it
        // did. What it must not do is lose the boards a longer pass had already paid for in
        // requests to somebody else.
        var handler = new StubHandler(_ => RecordedBoards.Ok(RecordedBoards.GreenhouseEmptyBoard));
        var reader = Reader(Greenhouse(handler));

        using var cancellation = new CancellationTokenSource();

        await cancellation.CancelAsync();

        var reads = await reader.ReadAsync(
            [new AtsBoard(AtsVendor.Greenhouse, "cloudflare")],
            cancellation.Token);

        Assert.Equal(AtsBoardReadOutcome.Unavailable, Assert.Single(reads).Value.Outcome);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_reader_that_throws_does_not_take_the_pass_with_it()
    {
        // Defence in depth, and the layer that matters when a reader added later forgets the
        // interface's one rule. AtsBoardClient is the first net; this is the one that keeps
        // Task.WhenAll safe to await without a handler of its own.
        var reader = Reader(new ContractBreakingClient());

        var board = new AtsBoard(AtsVendor.Greenhouse, "cloudflare");

        var reads = await reader.ReadAsync([board]);

        Assert.Equal(AtsBoardReadOutcome.Unavailable, reads[board].Outcome);
        Assert.Equal(AtsBoardReadOutcome.Unavailable, (await reader.ReadAsync(board)).Outcome);
    }

    [Fact]
    public async Task Nothing_is_asked_of_anybody_when_there_is_nothing_to_ask_about()
    {
        var handler = new StubHandler(_ => RecordedBoards.Ok(RecordedBoards.GreenhouseBoard));
        var reader = Reader(Greenhouse(handler));

        Assert.Empty(await reader.ReadAsync([]));
        Assert.Empty(handler.Requests);
    }

    private static GreenhouseBoardClient Greenhouse(StubHandler handler)
        => new(new StubHttpClientFactory(handler), NullLogger<GreenhouseBoardClient>.Instance);

    private static AshbyBoardClient Ashby(StubHandler handler)
        => new(new StubHttpClientFactory(handler), NullLogger<AshbyBoardClient>.Instance);

    private static LeverBoardClient Lever(StubHandler handler)
        => new(new StubHttpClientFactory(handler), NullLogger<LeverBoardClient>.Instance);

    private static SmartRecruitersBoardClient SmartRecruiters(StubHandler handler)
        => new(new StubHttpClientFactory(handler), NullLogger<SmartRecruitersBoardClient>.Instance);

    private static AtsBoardReader Reader(params IAtsBoardClient[] clients)
        => Reader(maxConcurrentFetches: 4, clients);

    private static AtsBoardReader Reader(int maxConcurrentFetches, params IAtsBoardClient[] clients)
        => new(
            clients,
            Options.Create(new AtsBoardOptions { MaxConcurrentFetches = maxConcurrentFetches }),
            NullLogger<AtsBoardReader>.Instance);

    /// <summary>Raises <paramref name="target"/> to <paramref name="value"/> if it is higher.</summary>
    private static void RaiseTo(ref int target, int value)
    {
        var seen = Volatile.Read(ref target);

        while (value > seen)
        {
            var previous = Interlocked.CompareExchange(ref target, value, seen);

            if (previous == seen)
            {
                return;
            }

            seen = previous;
        }
    }

    /// <summary>Waits for a condition rather than for a duration, so the test is not a race.</summary>
    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition was never reached.");

            await Task.Delay(5);
        }
    }

    /// <summary>A reader that throws, which the interface forbids and the facade survives.</summary>
    private sealed class ContractBreakingClient : IAtsBoardClient
    {
        public AtsVendor Vendor => AtsVendor.Greenhouse;

        public Task<AtsBoardRead> ReadAsync(
            AtsBoard board,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("A reader that ignores its own contract.");
    }
}
