using System.Security.Claims;
using JobPlatform.Api.Endpoints;
using JobPlatform.Api.Infrastructure;
using JobPlatform.Core.Applications;
using JobPlatform.Core.Enrichment;
using JobPlatform.Core.Matching;
using JobPlatform.Core.Model;
using JobPlatform.Data.Sql;
using Microsoft.AspNetCore.Mvc;

namespace JobPlatform.Api.Features.Matches;

/// <summary>
/// The caller's own matches.
/// </summary>
/// <remarks>
/// Read-only. Nothing here scores anything or calls a model: the arithmetic runs in the nightly
/// sweep and the judgement runs behind it, so by the time a candidate opens this page the work
/// is done and this is a query. That is the whole reason the sweep exists on a timer rather
/// than being triggered by the page - a shortlist that costs model calls to look at is one
/// nobody can afford to browse.
///
/// Authenticated unconditionally, like the profile, and scoped to the caller's own profile id
/// resolved from their token. The posting data returned is public, but which postings a
/// particular person matches, and by how much, is not.
/// </remarks>
public sealed class MatchEndpoints : IEndpointGroup
{
    /// <summary>Hard ceiling regardless of what a caller asks for. Mirrors the posting search.</summary>
    private const int MaxLimit = 100;

    public void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/matches")
            .WithTags("Matches")
            .RequireAuthorization(AuthSetup.AuthenticatedPolicy)
            .RequireRateLimiting(RateLimitSetup.ReadPolicy);

        group.MapGet("/", ListAsync)
            .WithName("ListMatches")
            .WithSummary("The calling principal's scored matches, best first.");

        group.MapGet("/{postingId:long}", GetAsync)
            .WithName("GetMatch")
            .WithSummary("One match in full, including the breakdown behind the score.");

        group.MapPut("/{postingId:long}/dismissed", SetDismissedAsync)
            .WithName("SetMatchDismissed")
            .WithSummary("Marks a match as not interesting, or takes that back.");

        group.MapGet("/{postingId:long}/cv", CurriculumVitaeAsync)
            .WithName("GetMatchCurriculumVitae")
            .WithSummary("Which of the candidate's own CVs goes with this posting, and why.");

        group.MapPut("/{postingId:long}/cv", SetCurriculumVitaeAsync)
            .WithName("SetMatchCurriculumVitae")
            .WithSummary("Chooses the CV to send for this posting, or hands the choice back.");

        group.MapGet("/skill-gap", SkillGapAsync)
            .WithName("GetSkillGap")
            .WithSummary("What the candidate's matched band asks for that their profile lacks.");
    }

    /// <summary>
    /// Which CV goes with this posting, run as arithmetic and nothing else.
    /// </summary>
    /// <remarks>
    /// <b>This exists because the dashboard was rendering a document that is no longer written.</b>
    /// A draft's <c>curriculumVitaeMarkdown</c> is null for everything generated since the CV
    /// library replaced per-posting generation, so the page showed an empty CV panel next to a
    /// perfectly good cover letter - which reads as a failure rather than as the design.
    ///
    /// <b>It runs the selector and stops there.</b> <c>get_submission_pack</c> does two more
    /// things with the same selection: it puts a genuine tie to a model, and it parks the posting
    /// when nothing in the library fits. Neither may happen behind a page load - the first spends
    /// money on a route a client can call repeatedly, and the second is a write that would put a
    /// posting down because somebody expanded a row. So a tie is reported as a tie and an
    /// abstention as an abstention, and the pack still decides at send time.
    ///
    /// <b>Read off the stored match, not off <c>PostingConcepts</c>.</b>
    /// <c>CvVariantSelector.DemandsOf</c> is shared with the pack for exactly that reason: the CV
    /// is chosen against the same requirement set the breakdown on the same page was computed
    /// from, and a re-extraction cannot make the two disagree.
    ///
    /// Two queries, neither of which reads a document: the match row - which does not project the
    /// advert - and the library as facts, which carries ids, labels and concept keys and never
    /// markdown.
    /// </remarks>
    private static async Task<IResult> CurriculumVitaeAsync(
        ClaimsPrincipal user,
        long postingId,
        [FromServices] CandidateProfileRepository profiles,
        [FromServices] JobMatchRepository matches,
        [FromServices] CvVariantRepository variants,
        CancellationToken ct)
    {
        if (!user.TryGetSubjectId(out var subjectId, out var error))
        {
            return error;
        }

        var profileId = await profiles.GetIdAsync(subjectId, ct);

        if (profileId is null)
        {
            return TypedResults.NotFound();
        }

        // The pair, not the posting. A posting this candidate was never scored against has no
        // demands to choose a CV over, and answering anything for it would be answering about
        // somebody else's shortlist.
        var row = await matches.GetDetailAsync(profileId.Value, postingId, ct);

        if (row is null)
        {
            return TypedResults.NotFound();
        }

        var library = await variants.ListSelectableFactsAsync(profileId.Value, ct);

        var selection = CvVariantSelector.Select(
            CvVariantSelector.DemandsOf(
                row.Read<ConceptMatch>(row.MatchedJson),
                row.Read<ConceptGap>(row.GapsJson)),
            library);

        // What the candidate settled themselves, where they did. Read after the selection rather
        // than instead of it: the page shows their choice AND what the arithmetic made of the
        // field, because the second is why the first was worth making and what they would switch
        // between if they changed their mind.
        //
        // The variant is fetched rather than looked up in the scored library, because a chosen
        // one may not be in it - archiving a CV removes it from selection and not from a decision
        // somebody already made about it, and "the CV you chose can no longer be sent" is the one
        // thing this panel must not fail to say.
        var picked = await matches.GetChosenCvAsync(profileId.Value, postingId, ct);

        var chosenByCandidate = picked is { } choice
            ? await variants.GetAsync(profileId.Value, choice.VariantId, ct) is { } variant
                ? new CandidateCvChoice(
                    variant.Id,
                    variant.Label,
                    choice.AtUtc,
                    variant.IsSendable,
                    selection.Scores
                        .Where(score => score.VariantId == variant.Id)
                        .Select(score => (int?)score.Score)
                        .FirstOrDefault())
                : null
            : null;

        return TypedResults.Ok(new CvChoiceResponse
        {
            PostingId = postingId,
            Outcome = selection.Outcome.ToString(),
            Chosen = selection.Chosen is { } chosen ? ToChoice(chosen) : null,
            Tied = [.. selection.Tied.Select(ToChoice)],
            Missing = [.. selection.Missing.Select(gap =>
                new CvChoiceGap(gap.RequiredKey, GapLabel(gap.RequiredKey)))],
            Rationale = selection.Rationale,

            // The candidate's own decision outranks the arithmetic's, here as at send time: it is
            // reported as theirs whatever the scores made of the field.
            DecidedBy = chosenByCandidate is not null
                ? "candidate"
                : selection.Outcome is CvSelectionOutcome.Chosen ? "arithmetic" : null,
            ChosenByCandidate = chosenByCandidate,
            Considered = library.Count,
        });

        static CvChoiceVariant ToChoice(CvVariantScore score)
            => new(score.VariantId, score.Label, score.Score, score.Answered);
    }

    /// <summary>The vocabulary's name for a key, or the key where it knows none.</summary>
    /// <remarks>
    /// A raw key printed to a person is bad and a requirement silently dropped from a list of what
    /// to write next is worse, so an unknown key prints as itself. The same fallback the selector's
    /// own rationale uses.
    /// </remarks>
    private static string GapLabel(string key)
        => ConceptGraph.Default.TryGet(key, out var concept) ? concept.Label : key;

    /// <summary>Default score floor for the gap. The band worth taking advice from.</summary>
    private const int GapMinimumScore = 40;

    /// <summary>How many gaps to return. A list nobody scrolls is a list nobody acts on.</summary>
    private const int GapLimit = 12;

    /// <summary>
    /// The join, run backwards: what this candidate's matched band asks for and their profile
    /// does not hold.
    /// </summary>
    /// <remarks>
    /// <b>Reads Azure SQL to answer an aggregate question</b>, which the architecture otherwise
    /// reserves for Cosmos. Allowed on the terms <c>GetSourceCompositionAsync</c> set, with one
    /// difference that matters: this is per-principal, so it carries no output cache and the
    /// usual mitigation is unavailable. It is bounded instead - the expensive half is scoped to
    /// one profile and one score floor so it lands on the <c>(ProfileId, Score)</c> index, and
    /// the corpus figures are looked up only for the concepts that band already names rather
    /// than aggregated over the whole vocabulary.
    ///
    /// <para>
    /// It must therefore never be on a bootstrap or polling path. It is loaded when the market
    /// page renders and not before.
    /// </para>
    /// </remarks>
    private static async Task<IResult> SkillGapAsync(
        ClaimsPrincipal user,
        [FromServices] CandidateProfileRepository profiles,
        [FromServices] JobMatchRepository matches,
        [FromServices] JobPostingQueryRepository postings,
        CancellationToken ct,
        string? searchTerm = null,
        int minScore = GapMinimumScore)
    {
        if (!user.TryGetSubjectId(out var subjectId, out var error))
        {
            return error;
        }

        var profileId = await profiles.GetIdAsync(subjectId, ct);

        if (profileId is null)
        {
            return TypedResults.Problem(
                detail: "No profile exists for this principal, so nothing has been matched yet.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var inBand = await matches.GetInBandConceptDemandAsync(
            profileId.Value, Math.Clamp(minScore, 0, 100), ct);

        // The keys the band names bound the corpus query. Without that this is the 222-row
        // aggregate over the whole assertion table that GetConceptDemandAsync exists to refuse.
        var corpus = inBand.Count == 0
            ? new Dictionary<string, int>(StringComparer.Ordinal)
            : await postings.GetConceptDemandAsync([.. inBand.Keys], searchTerm, ct);

        var held = await profiles.GetAssertionsAsync(profileId.Value, ct);

        var gaps = SkillGapAnalysis.Compute(
            inBand,
            corpus,
            [.. held.Select(a => a.ConceptKey).Distinct(StringComparer.Ordinal)],
            ConceptGraph.Default,
            GapLimit);

        return TypedResults.Ok(new SkillGapResponse
        {
            MinScore = Math.Clamp(minScore, 0, 100),
            SearchTerm = searchTerm,
            Items = [.. gaps.Select(ToGapResponse)],
        });
    }

    private static SkillGapItem ToGapResponse(SkillGap gap)
    {
        var label = ConceptGraph.Default.TryGet(gap.ConceptKey, out var concept)
            ? concept.Label
            : gap.ConceptKey;

        string? heldLabel = null;

        if (gap.HeldKey is { } heldKey)
        {
            heldLabel = ConceptGraph.Default.TryGet(heldKey, out var heldConcept)
                ? heldConcept.Label
                : heldKey;
        }

        return new SkillGapItem
        {
            Concept = gap.ConceptKey,
            Label = label,
            Kind = concept.Kind.ToString(),
            MatchPostings = gap.MatchPostings,
            CorpusPostings = gap.CorpusPostings,
            Held = gap.HeldKey,
            HeldLabel = heldLabel,
            Relation = gap.Relation?.ToString(),
            Credit = gap.Credit,
        };
    }

    /// <param name="postedWithinDays">
    /// Only postings posted within this many days. Omitted for the whole scored corpus.
    /// </param>
    /// <remarks>
    /// <b>A window in days rather than an instant, and the server holds the clock.</b> The
    /// question this control asks is "what is new", which is relative to now; a client that
    /// resolves it against its own clock asks a slightly different question from the one on the
    /// screen, and a bookmarked filter asks yesterday's. Answered by <c>PostingAge</c>: the
    /// board's stated posted date where there is one, first-seen where there is not, because two
    /// postings in five state a date and a filter that believed only those would hide the rest of
    /// the shortlist.
    /// </remarks>
    private static async Task<IResult> ListAsync(
        ClaimsPrincipal user,
        [FromServices] CandidateProfileRepository profiles,
        [FromServices] JobMatchRepository matches,
        TimeProvider clock,
        CancellationToken ct,
        int minScore = 0,
        bool assessedOnly = false,
        int limit = 25,
        int offset = 0,
        bool dismissed = false,
        int? postedWithinDays = null)
    {
        if (!user.TryGetSubjectId(out var subjectId, out var error))
        {
            return error;
        }

        if (offset < 0)
        {
            return TypedResults.Problem(
                detail: "offset must not be negative.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (postedWithinDays is < 0)
        {
            return TypedResults.Problem(
                detail: "postedWithinDays must not be negative.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var profileId = await profiles.GetIdAsync(subjectId, ct);

        // No profile means no matches, and saying so plainly beats an empty list: the client
        // needs to send the person to the form rather than tell them nothing matched.
        if (profileId is null)
        {
            return TypedResults.Problem(
                detail: "No profile exists for this principal, so nothing has been matched yet.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var rows = await matches.ListAsync(
            profileId.Value,
            Math.Clamp(minScore, 0, 100),
            assessedOnly,
            Math.Clamp(limit, 1, MaxLimit),
            offset,
            dismissed,
            postedWithinDays is { } days ? PostingAge.Cutoff(clock.GetUtcNow(), days) : null,
            ct);

        return TypedResults.Ok(new { items = rows.Select(ToSummary).ToList(), offset });
    }

    /// <summary>
    /// Records that this candidate is not interested in a posting, or takes it back.
    /// </summary>
    /// <remarks>
    /// The only write on this group, and the one that keeps the shortlist a worklist. Without
    /// it every role the candidate has already rejected is back at the top tomorrow, and the
    /// nightly budget keeps spending judgements on postings they have said no to.
    ///
    /// <para>
    /// A PUT rather than a POST because it sets a state rather than appending to a log, and
    /// because it has to be safe to repeat: a client retrying a dismissal it is unsure landed
    /// must not get a different answer the second time.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Settles which CV goes with this posting, by the one party who is not guessing.
    /// </summary>
    /// <remarks>
    /// <b>The tie is common and the tie-break is a model, and neither of those is a reason to keep
    /// a person out of the decision.</b> An advert that states little ties every CV that covers
    /// it, and the pack then asks a model which of two documents reads better against the advert -
    /// a defensible default that nobody asked the candidate about. This is the route that lets
    /// them answer, and their answer stands at send time: <c>get_submission_pack</c> reads it
    /// before it reads the arithmetic.
    ///
    /// <b>A write, so it is theirs alone.</b> This surface authenticates a person; the agent
    /// surface has no equivalent tool and must not - a client choosing the CV would be exactly the
    /// second spelling of selection that living in the pack exists to prevent, and a model
    /// overruling a person's stated choice is the failure this route was built to end.
    ///
    /// <b>Null clears it and hands the decision back</b> rather than freezing the last pick. A
    /// person who changes their mind and wants the system to decide again has no other way to say
    /// so, and a choice that could only be replaced would quietly outlive the library it was made
    /// against.
    ///
    /// 404 covers both "not your posting" and "not your CV", deliberately: the two are the same
    /// answer to a caller that should be naming neither.
    /// </remarks>
    private static async Task<IResult> SetCurriculumVitaeAsync(
        ClaimsPrincipal user,
        long postingId,
        [FromBody] SetChosenCvRequest request,
        [FromServices] CandidateProfileRepository profiles,
        [FromServices] JobMatchRepository matches,
        TimeProvider clock,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!user.TryGetSubjectId(out var subjectId, out var error))
        {
            return error;
        }

        var profileId = await profiles.GetIdAsync(subjectId, ct);

        if (profileId is null)
        {
            return TypedResults.NotFound();
        }

        var stored = await matches.SetChosenCvAsync(
            profileId.Value, postingId, request.VariantId, clock.GetUtcNow(), ct);

        return stored ? TypedResults.NoContent() : TypedResults.NotFound();
    }

    private static async Task<IResult> SetDismissedAsync(
        ClaimsPrincipal user,
        long postingId,
        [FromBody] SetDismissedRequest request,
        [FromServices] CandidateProfileRepository profiles,
        [FromServices] JobMatchRepository matches,
        TimeProvider clock,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!user.TryGetSubjectId(out var subjectId, out var error))
        {
            return error;
        }

        var profileId = await profiles.GetIdAsync(subjectId, ct);

        if (profileId is null)
        {
            return TypedResults.NotFound();
        }

        var when = request.Dismissed ? clock.GetUtcNow() : (DateTimeOffset?)null;
        var found = await matches.SetDismissedAsync(profileId.Value, postingId, when, ct);

        return found
            ? TypedResults.Ok(new SetDismissedResponse { PostingId = postingId, DismissedAtUtc = when })
            : TypedResults.NotFound();
    }

    private static async Task<IResult> GetAsync(
        ClaimsPrincipal user,
        long postingId,
        [FromServices] CandidateProfileRepository profiles,
        [FromServices] JobMatchRepository matches,
        CancellationToken ct)
    {
        if (!user.TryGetSubjectId(out var subjectId, out var error))
        {
            return error;
        }

        var profileId = await profiles.GetIdAsync(subjectId, ct);

        if (profileId is null)
        {
            return TypedResults.NotFound();
        }

        var row = await matches.GetDetailAsync(profileId.Value, postingId, ct);

        return row is null ? TypedResults.NotFound() : TypedResults.Ok(ToDetail(row));
    }

    private static MatchSummary ToSummary(MatchRow row)
        => Fill(new MatchSummary
        {
            PostingId = row.PostingId,
            Title = row.Title,
            Score = row.Score,
        }, row);

    private static MatchDetail ToDetail(MatchRow row)
    {
        var graph = ConceptGraph.Default;

        var detail = new MatchDetail
        {
            PostingId = row.PostingId,
            Title = row.Title,
            Score = row.Score,
            HasApplication = row.HasApplication,
            Components = row.Read<MatchComponent>(row.ComponentsJson)
                .Select(c => new MatchComponentResponse(c.Name, c.Score, c.Weight))
                .ToList(),
            Matched = row.Read<ConceptMatch>(row.MatchedJson)
                .Select(m => new ConceptMatchResponse(
                    m.RequiredKey,
                    Label(graph, m.RequiredKey),
                    m.HeldKey,
                    Label(graph, m.HeldKey),
                    m.Relation.ToString(),
                    m.Credit,
                    m.Demand.ToString()))
                .ToList(),
            Gaps = row.Read<ConceptGap>(row.GapsJson)
                .Select(g => new ConceptGapResponse(
                    g.RequiredKey,
                    Label(graph, g.RequiredKey),
                    g.Demand.ToString(),
                    g.YearsMin))
                .ToList(),
            Strengths = row.Read<string>(row.StrengthsJson),
            AssessmentGaps = row.Read<string>(row.AssessmentGapsJson),
            Emphasise = row.Read<string>(row.EmphasiseJson),
        };

        return (MatchDetail)Fill(detail, row);
    }

    /// <summary>
    /// The fields the summary and the detail share.
    /// </summary>
    /// <remarks>
    /// A <c>with</c> expression on the base record, so <see cref="MatchDetail"/> keeps the
    /// properties it set before this runs. Writing the shared fields twice is how the two
    /// responses drift apart.
    /// </remarks>
    private static MatchSummary Fill(MatchSummary summary, MatchRow row)
        => summary with
        {
            // Recomputed from the components rather than stored: it is a pure function of
            // them, so a column would be a second copy that could drift from the first.
            Coverage = Math.Clamp(row.Read<MatchComponent>(row.ComponentsJson).Sum(c => c.Weight), 0, 1),
            Company = row.Company,
            Location = row.Location,
            AnnualSalaryMin = row.AnnualSalaryMin,
            AnnualSalaryMax = row.AnnualSalaryMax,
            AnnualSalaryCurrency = row.AnnualSalaryCurrency,
            WorkArrangement = row.WorkArrangement.ToString(),
            Seniority = row.Seniority.ToString(),
            DatePosted = row.DatePosted,
            RequiredGapCount = row.RequiredGapCount,

            // Null rather than "Unknown" where the sweep has not been here. A client has to be
            // able to tell "the model has not looked at this yet" from "the model looked and
            // could not say", and a default enum name collapses the two.
            Verdict = row.Verdict?.ToString(),
            AssessmentScore = row.AssessmentScore,
            Rationale = row.Rationale,
            Similarity = row.Similarity,
            RankScore = row.RankScore,
            ScoredAtUtc = row.ScoredAtUtc,
            AssessedAtUtc = row.AssessedAtUtc,
            DismissedAtUtc = row.DismissedAtUtc,
        };

    private static string Label(ConceptGraph graph, string key)
        => graph.TryGet(key, out var concept) ? concept.Label : key;
}
