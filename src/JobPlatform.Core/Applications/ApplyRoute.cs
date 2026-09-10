using JobPlatform.Core.Dedup;
using JobPlatform.Core.Submissions;

namespace JobPlatform.Core.Applications;

/// <summary>
/// Whether an employer's own application form is reachable from a row, by a client that drives a
/// browser.
/// </summary>
/// <remarks>
/// <b>This is one function because it has two readers, and this codebase already knows what a rule
/// with two spellings costs.</b> The channel filter is written out twice because EF translates one
/// and materialises the other; the queue predicate and <c>ParkReasonPolicy</c> avoided that by
/// deriving both readers from a single definition. Both callers here run after materialisation -
/// <c>GenerateApplicationsFunction</c> decides whether to spend a model call, and the same pass
/// counts what it is waiting on - so there is no translation problem and therefore no excuse.
///
/// <b>It asks two facts about the row and never which board the row came from.</b>
/// <see cref="SubmissionChannel.Ats"/> is the board saying the application happens on the
/// employer's own system, about <i>this</i> listing rather than one that resembles it, and
/// <see cref="ApplyUrlSource.BoardPosting"/> beside it means no address for that system is held,
/// so the only URL on the row is the board's own posting page. On this corpus that pair is
/// LinkedIn and nothing else, and it is still worth writing as the pair rather than as a site
/// name, which would go wrong on the day a second board starts withholding. Measured 2026-09-10 -
/// 1,322 LinkedIn postings read it, and every freehire and Indeed posting carries its own link.
///
/// <b>The provenance is what separates the two, and the vendor cannot.</b> A published link into
/// WhatJobs also reads <see cref="SubmissionChannel.Ats"/> - the channel is <c>Ats</c> the moment
/// any direct link exists, whoever is at the end of it - and it also reads
/// <see cref="AtsVendor.Aggregator"/>. So a rule written on the channel and the vendor admits
/// exactly the row this skip was invented for. <see cref="ApplyUrlSource"/> is the only field that
/// tells "the board withheld the address" from "the board published one and it goes to another
/// board", which is why it is the term here. <c>GenerateApplicationsTests</c> carries that row as
/// a fixture and it is the reason this is written the way it is.
///
/// <b>The pair is admitted because the board publishes the destination to the client that is going
/// to apply.</b> LinkedIn's offsite apply anchor is a
/// <c>/safety/go/?url=&lt;the employer's form&gt;</c> wrapper, so the address is in the page the
/// browser has already loaded and no request resolves it. It is rendered to a signed-in client
/// only, which is why this is a fact about what a browser-driving client can reach and not about
/// what this repository can fetch: nothing on the server side may read this predicate as
/// permission to go and look. See <c>mcp_handoff.md</c> 3.2b.
///
/// <b>What it deliberately does not admit.</b> A published link that leads to another job board -
/// WhatJobs, Adzuna, Reed, Indeed's own re-listing, 646 rows on 2026-09-10 - carries
/// <see cref="ApplyUrlSource.Posting"/>, and stays out: following one spends a day's cap arriving
/// at a second search results page, which is the skip <see cref="AtsVendors.IsEmployerAts"/>
/// exists for. <see cref="SubmissionChannel.Board"/> - Easy Apply - stays out too, and that one is
/// a decision rather than a consequence: it is driveable by an authenticated browser, and it is
/// costed and deferred in 3.2b for reasons that have nothing to do with whether the form can be
/// filled in.
/// </remarks>
public static class ApplyRoute
{
    /// <summary>
    /// Whether a run that drives a browser could reach an employer's own form from this row.
    /// </summary>
    /// <remarks>
    /// <b>True in two ways, and they are different claims.</b> A vendor that is an employer's own
    /// system means the address in hand already points at the form. A board-page address on an
    /// offsite listing means the form is one documented hop away, through an apply link the board
    /// publishes to the client that is going to use it. Both end at an employer; only the first
    /// ends there without opening the board first.
    ///
    /// <b>It answers about the route and never about the fit.</b> Nothing here may reach a score,
    /// a ranking or a threshold - the same boundary <c>PostingReachability</c> is written under,
    /// and for the same reason: how a board publishes its adverts is a fact about the market
    /// rather than about whether the candidate suits the job.
    /// </remarks>
    /// <param name="channel">Where the application is made, as the board stated it.</param>
    /// <param name="source">Where the address held for this row came from.</param>
    /// <param name="vendor">Whose software is at the end of that address.</param>
    public static bool ReachesAnEmployer(
        SubmissionChannel channel,
        ApplyUrlSource source,
        AtsVendor vendor)
        => vendor.IsEmployerAts() || FollowsBoardApplyLink(channel, source);

    /// <summary>
    /// Whether reaching the employer means opening the board's posting page and following its
    /// apply link.
    /// </summary>
    /// <remarks>
    /// Exposed beside <see cref="ReachesAnEmployer"/> rather than folded into it because a caller
    /// that is planning work wants the two apart: this arm costs a page load and a redirect before
    /// the form appears, and a run that is choosing what to open next may reasonably prefer the
    /// rows that do not. Nothing depends on that today - both callers ask the wider question - and
    /// it is here so the distinction has a name rather than being re-derived from an enum pair.
    /// </remarks>
    /// <param name="channel">Where the application is made, as the board stated it.</param>
    /// <param name="source">Where the address held for this row came from.</param>
    public static bool FollowsBoardApplyLink(SubmissionChannel channel, ApplyUrlSource source)
        => channel == SubmissionChannel.Ats && source == ApplyUrlSource.BoardPosting;
}
