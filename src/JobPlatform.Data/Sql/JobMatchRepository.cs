using System.Linq.Expressions;
using System.Text.Json;
using JobPlatform.Core.Applications;
using JobPlatform.Core.Dedup;
using JobPlatform.Core.Enrichment;
using JobPlatform.Core.Matching;
using JobPlatform.Core.Submissions;
using JobPlatform.Data.Sql.Entities;
using Microsoft.EntityFrameworkCore;

namespace JobPlatform.Data.Sql;

/// <summary>
/// One stored match, flat, as every read of this table projects it.
/// </summary>
/// <remarks>
/// A positional record rather than an entity, so a query never materialises a
/// <c>JobPostingEntity</c> - which would drag an unbounded description across for every row of
/// a fifty-match page. The JSON columns come back as strings and are parsed by the caller,
/// because a list response does not need them and paying to deserialise them there would undo
/// the point of projecting.
/// </remarks>
public sealed record MatchRow(
    long PostingId,
    string Title,
    string? Company,
    string? Location,
    decimal? AnnualSalaryMin,
    decimal? AnnualSalaryMax,
    string? AnnualSalaryCurrency,
    WorkArrangement WorkArrangement,
    Seniority Seniority,
    DateOnly? DatePosted,
    int Score,
    int RequiredGapCount,
    CandidacyVerdict? Verdict,
    int? AssessmentScore,
    string? Rationale,
    DateTimeOffset ScoredAtUtc,
    DateTimeOffset? AssessedAtUtc,
    DateTimeOffset? DismissedAtUtc,
    string? ComponentsJson,
    string? MatchedJson,
    string? GapsJson,
    string? StrengthsJson,
    string? AssessmentGapsJson,
    string? EmphasiseJson,
    int ScorerVersion,
    double? Similarity,
    double RankScore,
    bool HasApplication)
{
    /// <summary>Reads one of the JSON columns back. Never throws - see the repository.</summary>
    public IReadOnlyList<T> Read<T>(string? json) => JobMatchRepository.Read<T>(json);
}

/// <summary>
/// One posting worth applying to, as the agent surface sees it.
/// </summary>
/// <remarks>
/// Deliberately narrow. This is a work queue, not a browse response: enough to decide which
/// posting to act on and where to act on it, and nothing more. The advert body and the generated
/// documents are a second call, so a shortlist of fifty is a page rather than megabytes.
///
/// <b>Nothing on this row is stored as it appears here.</b> The channel, the apply URL and its
/// provenance are projected from the posting's columns; the vendor is read off the URL after the
/// query has run, because a static call over a column has no SQL; the cluster is grouped in
/// memory. A queue row is an answer as of this call rather than a record - which is what lets a
/// re-scrape change every one of those fields without anything having to be migrated.
/// </remarks>
/// <param name="Channel">
/// Where the application is made, projected from <c>JobUrlDirect</c> rather than stored. Present
/// means the employer's own system; absent on a board posting means the board hosts it.
/// </param>
/// <param name="ApplyUrl">
/// Where to go. The direct link where there is one, the board's own posting URL otherwise -
/// resolved here so no caller has to re-derive the rule that decides which.
/// </param>
/// <param name="ApplyUrlSource">
/// Where <paramref name="ApplyUrl"/> came from, because one of the three is an inference and a
/// caller acting on it deserves to know which.
/// </param>
/// <param name="AtsVendor">
/// Whose application system sits at the end of <paramref name="ApplyUrl"/>, read off the URL.
/// <c>Aggregator</c> is the value worth acting on: a "direct" link into another job board is
/// another board rather than an employer's form, and the loop should skip it.
/// </param>
/// <param name="AssessedAtUtc">
/// When the model judged this pair. The queue admits nothing unassessed, so this dates the
/// judgement rather than testing for one - which is what tells a verdict formed this morning
/// from one formed against an advert that has been rewritten since.
/// </param>
/// <param name="FirstSeenUtc">
/// When the posting first entered the corpus, so a run can ask what is new since the last one.
/// Not <c>LastSeenUtc</c>: a re-scrape moves that on every live posting there is.
/// </param>
/// <param name="HasDocuments">Whether a tailored CV and cover letter already exist for this pair.</param>
/// <param name="DedupeKey">
/// The cross-board identity every listing of this job shares, or null where the posting has
/// none. Null is not an empty key - see <paramref name="AlternatePostings"/>.
/// </param>
/// <param name="AlternatePostings">
/// The other listings of this same job that are also in the queue, best first by the comparison
/// that chose this row. Empty for a posting with no duplicate and empty for one with no
/// <paramref name="DedupeKey"/>, and those are different facts: a null key means this posting has
/// no cross-board identity, never that it shares the empty one with every other unlocated row.
/// </param>
public sealed record ApplyableRow(
    long PostingId,
    string Title,
    string? Company,
    string? Location,
    SubmissionChannel Channel,
    string? ApplyUrl,
    ApplyUrlSource ApplyUrlSource,
    AtsVendor AtsVendor,
    int Score,
    int? AssessmentScore,
    CandidacyVerdict? Verdict,
    string? Rationale,
    double RankScore,
    DateTimeOffset? AssessedAtUtc,
    DateTimeOffset FirstSeenUtc,
    bool HasDocuments,
    string? DedupeKey,
    IReadOnlyList<ClusterMember> AlternatePostings);

/// <summary>What orders the apply queue.</summary>
/// <remarks>
/// <b>The default is the ranking key rather than either score</b>, for the finding
/// <c>MatchRanker</c> exists for: the arithmetic score orders the corpus well and inverts inside
/// its own top band, and this queue is that band. The other two are offered because a run may
/// have reason to argue with the ranker - ordering by the model's judgement is what a person
/// reading the queue expects - and they are not the default because the measurement says they
/// are worse.
///
/// <see cref="Rank"/> is zero so that the unset value is the default order. A queue has no
/// unordered state for a zero member to mean instead.
/// </remarks>
public enum ApplyableSort
{
    /// <summary>The fused ranking key, and the default.</summary>
    Rank = 0,

    /// <summary>The deterministic score.</summary>
    Score = 1,

    /// <summary>The model's assessment, with anything it scored no number for last.</summary>
    AssessmentScore = 2,
}

/// <summary>
/// Everything the apply queue can be narrowed by.
/// </summary>
/// <remarks>
/// <b>A record rather than seven more parameters</b>, the way <see cref="PostingSearchCriteria"/>
/// already is. Two of these are nullable instants and two are nullable numbers, so a positional
/// list is a signature in which transposing a pair of arguments compiles and quietly answers a
/// different question - and this one is called with values a model chose.
///
/// <b>Every filter here is applied in SQL, before the bound.</b> A filter applied after
/// <c>Take</c> is not a filter, it is a silent reduction of the limit, which this codebase has
/// paid for three times. <see cref="MinAssessmentScore"/> is the one that matters most: it is
/// enforced here rather than trusted to the caller, because the caller is a prompt and a
/// prompt-level slip fires applications at bad matches.
/// </remarks>
public sealed record ApplyableQuery
{
    /// <summary>Where the application is made. Null for all three.</summary>
    public SubmissionChannel? Channel { get; init; }

    /// <summary>
    /// Restrict to apply links of one provenance. Null for all three.
    /// </summary>
    /// <remarks>
    /// How a run asks for postings it can actually apply through: <c>Posting</c> is the
    /// employer's link as the board published it, and the other two are an inference and a board
    /// page. Filtered on the same expression the projection uses, and held to it by
    /// <c>The_apply_url_source_filter_agrees_with_the_projection_and_filters_before_the_bound</c>.
    /// </remarks>
    public ApplyUrlSource? ApplyUrlSource { get; init; }

    /// <summary>Only postings first seen at or after this instant.</summary>
    /// <remarks>
    /// <c>JobPostings.FirstSeenUtc</c>, deliberately not <c>LastSeenUtc</c>. This answers "what
    /// has arrived since my last run", and a re-scrape moves the last-seen date on every live
    /// posting in the corpus - so the same question asked against that column returns everything,
    /// every time, which is an answer a run cannot act on.
    /// </remarks>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Only pairs the model judged at or after this instant.</summary>
    /// <remarks>
    /// A different question from <see cref="Since"/>, and both exist because the two dates move
    /// independently: the nightly pass spends its budget on postings that arrived weeks ago, so a
    /// run asking only for new postings never sees the judgements it was waiting for.
    /// </remarks>
    public DateTimeOffset? AssessedSince { get; init; }

    /// <summary>
    /// True for pairs that already have generated documents, false for those that do not.
    /// </summary>
    /// <remarks>
    /// Three-valued rather than a flag, because "only what I can apply to properly" and "what is
    /// still waiting on a draft" are both real questions and a bare true answers only the first.
    /// Measured on 2026-09-02, exactly one posting in the database had documents - so a run that
    /// asks for true and gets nothing has learnt the true thing about where this loop is stuck.
    /// </remarks>
    public bool? DocumentsReady { get; init; }

    /// <summary>
    /// The floor on the model's assessment, enforced in the query.
    /// </summary>
    /// <remarks>
    /// <b>A pair the model scored no number for does not clear a floor.</b> Letting those through
    /// on the grounds that nothing has judged them reads as "unjudged is good enough", which is
    /// the opposite of what asking for a floor means.
    /// </remarks>
    public int? MinAssessmentScore { get; init; }

    /// <summary>How the queue is ordered. <see cref="ApplyableSort.Rank"/> unless asked otherwise.</summary>
    public ApplyableSort Sort { get; init; } = ApplyableSort.Rank;

    /// <summary>
    /// How many jobs to return.
    /// </summary>
    /// <remarks>
    /// Jobs rather than rows: duplicate listings of one job are collapsed after they are read, so
    /// this bounds what comes back and the query reads further than it to fill it. See
    /// <see cref="JobMatchRepository.ListApplyableAsync(long, ApplyableQuery, CancellationToken)"/>.
    /// </remarks>
    public int Limit { get; init; } = 20;
}

/// <summary>Where an application for one matched posting would be made.</summary>
/// <param name="Channel">Projected from the apply link, never stored on the posting.</param>
/// <param name="ApplyUrl">The direct link where there is one, the board's posting URL otherwise.</param>
public sealed record ApplyTarget(string Title, SubmissionChannel Channel, string? ApplyUrl);

/// <summary>
/// Everything the match pipeline reads and writes.
/// </summary>
/// <remarks>
/// The scoring sweep is the one path in this system that reads a large slice of the posting
/// corpus, so every query here is written to project rather than to materialise entities: a
/// posting row carries an unbounded description and pulling ten thousand of them across to
/// compute an arithmetic score would cost more in transfer than the scoring costs in compute.
/// <see cref="GetPostingFactsAsync"/> selects the dozen columns the scorer actually reads, and
/// the description is fetched only later, for the handful of postings the model will see.
/// </remarks>
public sealed class JobMatchRepository(JobsDbContext db)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// How many ordered rows the apply queue reads for each job it returns.
    /// </summary>
    /// <remarks>
    /// Duplicate listings are collapsed after materialisation, so filling a page of <c>limit</c>
    /// jobs takes more than <c>limit</c> rows whenever any of them are the same job twice. Three
    /// is the shape of the corpus with room to spare - the live duplicates are pairs, and reading
    /// sixty index rows to answer a twenty-row queue is nothing against a database billed by
    /// wall-clock time.
    ///
    /// <b>It is a window and not a guarantee.</b> A queue whose head is all duplicates still
    /// comes back short, and <see cref="ListApplyableAsync(long, ApplyableQuery, CancellationToken)"/>
    /// says so rather than pretending the limit was ever a row count.
    /// </remarks>
    private const int ClusterWindow = 3;

    /// <summary>
    /// The postings worth scoring against a profile, as the flat facts the scorer needs.
    /// </summary>
    /// <remarks>
    /// Bounded by recency rather than by relevance, deliberately. Filtering to postings that
    /// already look promising would need the score this exists to compute, and a cheap
    /// pre-filter on, say, one required skill would drop exactly the roles a candidate is
    /// qualified for by a route the pre-filter cannot see. Recency is a limit that biases
    /// nothing about the match.
    /// </remarks>
    public async Task<IReadOnlyList<PostingFacts>> GetPostingFactsAsync(
        DateTimeOffset since, int limit, CancellationToken ct = default)
    {
        var rows = await db.JobPostings
            .AsNoTracking()
            .Where(p => p.LastSeenUtc >= since)
            .OrderByDescending(p => p.LastSeenUtc)
            .Take(limit)
            .Select(p => new
            {
                p.Id,
                p.Seniority,
                p.YearsExperienceMin,
                p.YearsExperienceMax,
                p.WorkArrangement,
                p.HybridDaysInOffice,
                p.AnnualSalaryMin,
                p.AnnualSalaryMax,
                p.AnnualSalaryCurrency,
                p.SalaryFromText,
                p.LocationCity,
                p.LocationCountry,
            })
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            return [];
        }

        var ids = rows.Select(r => r.Id).ToList();

        // One query for every assertion across the whole slice, grouped in memory. A
        // correlated Include would be one round trip per posting, and this runs over thousands.
        var assertions = await db.PostingConcepts
            .AsNoTracking()
            .Where(c => ids.Contains(c.PostingId))
            .Select(c => new
            {
                c.PostingId,
                c.Concept!.ConceptKey,
                c.Source,
                c.Polarity,
                c.YearsMin,
                c.YearsMax,
                c.EvidenceText,
                c.Confidence,
            })
            .ToListAsync(ct);

        var byPosting = assertions
            .GroupBy(a => a.PostingId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<ConceptAssertion>)g
                    .Select(a => new ConceptAssertion(
                        a.ConceptKey, a.Source, a.Polarity, a.YearsMin, a.YearsMax, a.EvidenceText, a.Confidence))
                    .ToList());

        return rows
            .Select(r => new PostingFacts
            {
                PostingId = r.Id,
                Concepts = byPosting.TryGetValue(r.Id, out var concepts) ? concepts : [],
                Seniority = r.Seniority,
                YearsExperienceMin = r.YearsExperienceMin,
                YearsExperienceMax = r.YearsExperienceMax,
                WorkArrangement = r.WorkArrangement,
                HybridDaysInOffice = r.HybridDaysInOffice,
                AnnualSalaryMin = r.AnnualSalaryMin,
                AnnualSalaryMax = r.AnnualSalaryMax,
                SalaryCurrency = r.AnnualSalaryCurrency,
                SalaryFromText = r.SalaryFromText,
                LocationCity = r.LocationCity,
                LocationCountry = r.LocationCountry,
            })
            .ToList();
    }

    /// <summary>
    /// Writes a night's scores, replacing whatever was there for the same pairs.
    /// </summary>
    /// <remarks>
    /// Upsert rather than insert, because a posting is re-scored whenever the profile changes
    /// or the scorer's version moves, and a row per attempt would grow without bound. The
    /// assessment columns are deliberately left alone on an update: a re-score is arithmetic,
    /// and discarding a model's judgement because a weight changed would mean paying for it
    /// again on the next sweep for no reason.
    ///
    /// A score that actually moved does clear the assessment, because then the judgement was
    /// made against different arithmetic and is genuinely stale. An edited advert reaches this
    /// the same way: its assertions are re-extracted, the score moves, and the stale judgement
    /// goes with it - which is why no separate content hash is tracked here.
    ///
    /// <b>A rank that moved clears nothing.</b> The ordering key is derived from the score and
    /// the embedding and says where to look first; the assessment was made about the posting, and
    /// re-sorting the page is not a reason to pay for that judgement twice.
    /// </remarks>
    /// <param name="ranking">
    /// What <c>MatchRanker</c> made of the same pass, correlated by posting id rather than by
    /// position. Pairs absent from it keep whatever rank they had - which is right for a partial
    /// pass and, being a dictionary lookup rather than a parallel index, cannot silently file one
    /// posting's ordering against another.
    /// </param>
    public async Task<int> UpsertScoresAsync(
        long profileId,
        IReadOnlyList<(PostingFacts Posting, MatchResult Result)> scores,
        IReadOnlyList<RankedMatch> ranking,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scores);
        ArgumentNullException.ThrowIfNull(ranking);

        if (scores.Count == 0)
        {
            return 0;
        }

        var ranked = ranking.ToDictionary(r => r.PostingId);

        var postingIds = scores.Select(s => s.Posting.PostingId).ToList();

        var existing = await db.JobMatches
            .Where(m => m.ProfileId == profileId && postingIds.Contains(m.PostingId))
            .ToDictionaryAsync(m => m.PostingId, ct);

        var written = 0;

        foreach (var (posting, result) in scores)
        {
            if (!existing.TryGetValue(posting.PostingId, out var entity))
            {
                entity = new JobMatchEntity
                {
                    ProfileId = profileId,
                    PostingId = posting.PostingId,
                };

                db.JobMatches.Add(entity);
            }
            else if (entity.Score == result.Score
                && entity.ScorerVersion == result.Version
                && entity.RankerVersion == MatchRanker.CurrentVersion
                && Unmoved(entity, posting.PostingId))
            {
                // Nothing moved. Skipping the write keeps the sweep from touching every row it
                // looked at, which on a database billed by wall-clock time is the difference
                // between a nightly job and a nightly bill. The rank is included in "nothing
                // moved" rather than exempted from it, which is why MatchRanker rounds: at full
                // precision a night's new postings nudge every key in the pool and this test
                // would never pass again.
                continue;
            }
            else if (entity.Score != result.Score)
            {
                // The arithmetic moved, so everything the model concluded from the old
                // arithmetic is stale. DismissedAtUtc is deliberately NOT in this list: the
                // candidate's "no" is a fact about them and the posting, not a conclusion
                // drawn from a number, and clearing it here would put every dismissed posting
                // back at the top of the shortlist on the first night its score shifted by a
                // point. Pinned by A_re_scored_pair_stays_dismissed.
                entity.Verdict = null;
                entity.AssessmentScore = null;
                entity.Rationale = null;
                entity.StrengthsJson = null;
                entity.AssessmentGapsJson = null;
                entity.EmphasiseJson = null;
                entity.AssessmentModel = null;
                entity.AssessmentVersion = null;
                entity.AssessedAtUtc = null;
                entity.AssessmentPayloadJson = null;
            }

            entity.Score = result.Score;
            entity.ComponentsJson = JsonSerializer.Serialize(result.Components, Json);
            entity.MatchedJson = JsonSerializer.Serialize(result.Matched, Json);
            entity.GapsJson = JsonSerializer.Serialize(result.Gaps, Json);
            entity.RequiredGapCount = result.RequiredGapCount;
            entity.ScorerVersion = result.Version;
            entity.ScoredAtUtc = now;

            if (ranked.TryGetValue(posting.PostingId, out var rank))
            {
                entity.Similarity = rank.Similarity;
                entity.RankScore = rank.RankScore;
                entity.RankerVersion = MatchRanker.CurrentVersion;
            }
            else
            {
                // Scored but not ranked. Falling back to the score keeps the row orderable
                // against ranked ones instead of sinking it to the bottom of the page, and
                // leaving the version behind is what gets it re-ranked next time.
                entity.Similarity = null;
                entity.RankScore = result.Score;
                entity.RankerVersion = 0;
            }

            written++;
        }

        if (written > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return written;

        bool Unmoved(JobMatchEntity entity, long postingId)
            => ranked.TryGetValue(postingId, out var rank)
                && entity.RankScore == rank.RankScore
                && Nullable.Equals(entity.Similarity, rank.Similarity);
    }

    /// <summary>
    /// The shortlist the nightly model pass should spend its budget on.
    /// </summary>
    /// <remarks>
    /// Above the threshold, not yet assessed, best first, and capped. Every one of those four is
    /// a cost control: the threshold is what stops the model reading obvious rejections, "not
    /// yet assessed" is what stops it re-reading last night's, the ordering is what makes the
    /// cap fall on the least promising rows, and the cap is what makes the bill a decision
    /// rather than a function of how many postings the scraper happened to find.
    ///
    /// The description comes across here, unlike in the scoring query, because this is the point
    /// at which something actually has to read the advert.
    /// </remarks>
    /// <param name="maximumScore">
    /// Upper bound on the score, for drawing a sample from one band rather than off the top.
    /// </param>
    /// <remarks>
    /// <b>A band changes the ordering as well as the filter, and it has to.</b> Without a band
    /// this takes the highest-scoring unassessed pairs, which is right: the model budget should
    /// go where the arithmetic is most hopeful. With one, taking the top of the band would
    /// reproduce the same restriction one level down - ask for 80-89 and get forty 89s - so the
    /// order falls back to posting id, which is scrape order and uncorrelated with score.
    ///
    /// This exists because every measurement made against this system's assessments so far
    /// describes only the top decile: the 70 pairs judged were all selected by score, which is
    /// the textbook shape of pooling bias. A band is what makes a stratified sample reachable,
    /// and a stratified sample is what makes those numbers statements about the corpus.
    /// </remarks>
    public async Task<IReadOnlyList<CandidacyRequest>> GetUnassessedAsync(
        long profileId,
        int minimumScore,
        int limit,
        int? maximumScore = null,
        CancellationToken ct = default)
    {
        var query = db.JobMatches
            .AsNoTracking()
            .Where(m => m.ProfileId == profileId
                && m.Score >= minimumScore
                && (m.AssessedAtUtc == null || m.AssessmentVersion != CandidacyAssessment.CurrentVersion)
                // In the query rather than after the Take, and the difference is not cosmetic.
                // A posting with no description cannot be assessed, so filtering afterwards means
                // asking for five rows and getting none - and because a band is ordered by posting
                // id, the same unusable rows sit at the head of it forever. They are never
                // assessed, so they never leave the unassessed set, so the next draw fetches them
                // again. Measured on 2026-08-30: the 60-69 band returned nothing at a limit of
                // five and five usable rows at a limit of ten, from the same starved head.
                //
                // These rows concentrate in the low bands, which is what made this expensive: a
                // posting with no description resolves no concepts, so it cannot clear the concept
                // floor, so it scores low. The stratified sample lives exactly where they are.
                && m.Posting!.Description != null
                && m.Posting.Description != ""
                // Dismissed pairs, in the query for the same reason and with the same failure
                // if they are not: a band ordered by posting id would keep drawing the same
                // dismissed rows into every sample and getting nothing usable back. This is
                // also the whole point of the column - the budget is forty judgements a night,
                // and spending one on a posting the candidate has already said no to is a
                // judgement not spent on a posting they have not seen.
                && m.DismissedAtUtc == null);

        if (maximumScore is { } ceiling)
        {
            query = query.Where(m => m.Score <= ceiling);
        }

        var ordered = maximumScore is null
            ? query.OrderByDescending(m => m.Score)
            : query.OrderBy(m => m.PostingId);

        var rows = await ordered
            .Take(limit)
            .Select(m => new
            {
                m.PostingId,
                m.Score,
                m.ComponentsJson,
                m.MatchedJson,
                m.GapsJson,
                m.RequiredGapCount,
                m.ScorerVersion,
                m.Posting!.Title,
                m.Posting.Company,
                m.Posting.Description,
            })
            .ToListAsync(ct);

        return rows
            // Belt and braces: the query excludes null and empty, this also excludes whitespace,
            // which SQL Server's comparison semantics would not. It can no longer starve a band,
            // because a row of pure whitespace is rare where an empty one is not.
            .Where(r => !string.IsNullOrWhiteSpace(r.Description))
            .Select(r => new CandidacyRequest(
                r.PostingId,
                r.Title,
                r.Company,
                r.Description!,
                Rebuild(r.Score, r.ComponentsJson, r.MatchedJson, r.GapsJson, r.ScorerVersion)))
            .ToList();
    }

    /// <summary>Writes what the model concluded, leaving the arithmetic half untouched.</summary>
    public async Task<int> ApplyAssessmentsAsync(
        long profileId,
        IReadOnlyList<(long PostingId, CandidacyAssessment Assessment)> assessments,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assessments);

        if (assessments.Count == 0)
        {
            return 0;
        }

        var postingIds = assessments.Select(a => a.PostingId).ToList();

        var rows = await db.JobMatches
            .Where(m => m.ProfileId == profileId && postingIds.Contains(m.PostingId))
            .ToDictionaryAsync(m => m.PostingId, ct);

        var written = 0;

        foreach (var (postingId, assessment) in assessments)
        {
            if (!rows.TryGetValue(postingId, out var entity))
            {
                // The score row went away between the shortlist and the answer. Dropping the
                // assessment is right: there is nothing to hang it on, and inventing a row
                // would produce a match with a judgement and no arithmetic behind it.
                continue;
            }

            entity.Verdict = assessment.Verdict;
            entity.AssessmentScore = assessment.Score;
            entity.Rationale = assessment.Rationale;
            entity.StrengthsJson = JsonSerializer.Serialize(assessment.Strengths, Json);
            entity.AssessmentGapsJson = JsonSerializer.Serialize(assessment.Gaps, Json);
            entity.EmphasiseJson = JsonSerializer.Serialize(assessment.Emphasise, Json);
            entity.AssessmentModel = assessment.Model;
            entity.AssessmentVersion = assessment.Version;
            entity.AssessedAtUtc = now;
            entity.AssessmentPayloadJson = assessment.PayloadJson;

            written++;
        }

        if (written > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return written;
    }

    /// <summary>
    /// A candidate's scored matches, best first.
    /// </summary>
    /// <remarks>
    /// <b>Ordered by the ranking key, not by the score and not by the model's verdict.</b> The
    /// verdict is null for everything the nightly sweep has not reached, so ordering by it would
    /// put this morning's best new posting at the bottom of the page. The score orders the corpus
    /// well and inverts inside its own top band, which is the thing <c>MatchRanker</c> exists to
    /// fix. All three numbers are returned, so a client can re-sort once it knows which rows
    /// carry a verdict.
    ///
    /// <c>RankScore</c> falls back to the score for any row the ranker has not reached, so a
    /// freshly scored pair sorts sensibly among ranked ones rather than sinking to the bottom.
    ///
    /// The description is excluded. This is a list, and it lands on the
    /// <c>(ProfileId, RankScore)</c> index precisely so that showing fifty matches does not read
    /// fifty unbounded text columns.
    /// </remarks>
    public async Task<IReadOnlyList<MatchRow>> ListAsync(
        long profileId,
        int minimumScore,
        bool assessedOnly,
        int limit,
        int offset,
        bool dismissed = false,
        CancellationToken ct = default)
    {
        var query = db.JobMatches
            .AsNoTracking()
            .Where(m => m.ProfileId == profileId && m.Score >= minimumScore);

        if (assessedOnly)
        {
            query = query.Where(m => m.AssessedAtUtc != null);
        }

        // Either the shortlist or the dismissed pile, never both. A list that mixes them puts
        // the postings you have already said no to back among the ones you have not seen,
        // which is the state this column exists to end.
        query = dismissed
            ? query.Where(m => m.DismissedAtUtc != null)
            : query.Where(m => m.DismissedAtUtc == null);

        return await query
            .OrderByDescending(m => m.RankScore)
            // Deterministic beyond the key itself. Rounding the rank to two places makes ties
            // possible, and a page whose contents shuffle between two identical requests is a
            // bug nobody can reproduce.
            .ThenBy(m => m.PostingId)
            .Skip(offset)
            .Take(limit)
            .Select(m => new MatchRow(
                m.PostingId,
                m.Posting!.Title,
                m.Posting.Company,
                m.Posting.LocationRaw,
                m.Posting.AnnualSalaryMin,
                m.Posting.AnnualSalaryMax,
                m.Posting.AnnualSalaryCurrency,
                m.Posting.WorkArrangement,
                m.Posting.Seniority,
                m.Posting.DatePosted,
                m.Score,
                m.RequiredGapCount,
                m.Verdict,
                m.AssessmentScore,
                m.Rationale,
                m.ScoredAtUtc,
                m.AssessedAtUtc,
                m.DismissedAtUtc,
                m.ComponentsJson,
                m.MatchedJson,
                m.GapsJson,
                m.StrengthsJson,
                m.AssessmentGapsJson,
                m.EmphasiseJson,
                m.ScorerVersion,
                m.Similarity,
                m.RankScore,
                false))
            .ToListAsync(ct);
    }

    /// <summary>
    /// The queue with nothing asked of it beyond a channel.
    /// </summary>
    /// <remarks>
    /// The common read, and the one every caller made before the queue learnt to be narrowed:
    /// every other filter defaults to off. It builds an <see cref="ApplyableQuery"/> and calls the
    /// overload, so there is one predicate rather than two spellings of it.
    /// </remarks>
    public Task<IReadOnlyList<ApplyableRow>> ListApplyableAsync(
        long profileId, SubmissionChannel? channel, int limit, CancellationToken ct = default)
        => ListApplyableAsync(profileId, new ApplyableQuery { Channel = channel, Limit = limit }, ct);

    /// <summary>
    /// What this candidate should apply to next: judged worth it, not already applied to, and not
    /// still blocked by whatever stopped the last attempt.
    /// </summary>
    /// <remarks>
    /// <b>Gated on the model's verdict, not on a score cut, and that is the whole point.</b> The
    /// deterministic score is a good filter and a bad final sort - measured at -0.051 inside its
    /// own top band, on fresh labels - so a threshold over it would hand an agent the rows
    /// <c>MatchRanker</c> exists because the score gets wrong. <c>ICandidacyAssessor</c> is the
    /// half of the design that knows what a role <i>is</i>, and this is the one query where that
    /// judgement is load-bearing enough to require.
    ///
    /// <b>Its threshold is its own constant and must not be merged with either of the two that
    /// already exist.</b> <c>MatchRanker.FusionFloor</c> is where the embedding earns its weight;
    /// <c>MatchSweepFunction.AssessmentThreshold</c> is where buying a model judgement is worth
    /// it. "Worth applying to" is a third question, and briefly collapsing the first two into one
    /// constant was already a mistake once.
    ///
    /// <b><c>Unknown</c> is excluded and so is unassessed.</b> Unknown means the model answered
    /// and said nothing usable; unassessed means it has not run. Neither is a recommendation, and
    /// a rule keyed on one of them disagrees with a rule keyed on the other on exactly those rows.
    ///
    /// <b>Exclusion is on what a submission row says, never on one existing.</b> This used to ask
    /// only whether any submission existed for the pair, and that single clause made parking
    /// impossible in the way that matters most: a park has to write a row to park against, and the
    /// instant that row existed the posting left the queue for good - so "come back to this once
    /// the captcha is gone" and "never show me this again" were the same operation. Four clauses
    /// replace it and each holds a posting back for a different reason: a <i>live</i> application
    /// - one never parked, or parked and since let back in - on this posting, and one on any other
    /// listing of the same job; a <i>permanent</i> park, the reasons <c>ParkReasonPolicy</c>
    /// classifies as never returning; and a park awaiting an answer, held while an answer this
    /// candidate owes an advert is still outstanding. Everything else - a captcha, a login wall,
    /// a spent daily quota - comes back on the next run, which is what parking was for.
    ///
    /// <b>Only a live application suppresses the other listings of the same job.</b> Applying
    /// twice to one vacancy is worse than not applying at all and the recruiter sees both, so an
    /// application against any member of a cross-board cluster takes the whole cluster out. A
    /// permanent park is about one listing and does not: an <c>Expired</c> means that board's page
    /// is gone rather than that the twin is, and a <c>Duplicate</c> means an application exists
    /// somewhere - which is a live row on a sibling, already excluded by the first clause.
    /// Suppressing a cluster on the strength of another board's 404 would hide a live vacancy for
    /// good.
    ///
    /// <b>The cross-listing clause guards against a null key explicitly, and must keep doing
    /// so.</b> EF gives <c>==</c> C# semantics, so two null <c>CrossBoardKey</c>s compare
    /// <i>equal</i> in the SQL it generates. Without the guard every posting whose employer or
    /// city is unknown would be one enormous cluster, and a single application would suppress all
    /// of them - which is exactly the collision <c>JobFingerprint.CrossBoardKey</c> answers null
    /// to prevent, arriving through the query instead.
    ///
    /// <b>The awaiting-an-answer clause asks whether an answer is outstanding, never whether the
    /// outstanding question names this posting, and narrowing it back to the posting is the loop
    /// it exists to prevent.</b> It was written as <c>q.PostingId == m.PostingId</c> and the
    /// deduplication defeats it: <c>OpenQuestions</c> holds one unanswered row per
    /// <c>(ProfileId, QuestionHash)</c>, so when a second advert asks what a first already asked,
    /// one row exists and it names the first. The second posting was parked
    /// <see cref="ParkReason.MissingAnswer"/>, its clause found no row naming it, and it was
    /// offered again on the very next run - which parked it again, for the same missing answer,
    /// every run, forever. That is both halves of the promise broken at once: the loop this
    /// clause is for, and "a parked posting is re-served once its question is answered" holding
    /// for the first advert to ask and no other.
    ///
    /// <b>Neither side carries the link, and that is a limit of the schema rather than a
    /// preference.</b> The park is the side that should carry it if either did: it is already the
    /// row per posting, and the caller writing it is holding the question row it has just opened
    /// or converged onto, so one nullable <c>AwaitingQuestionId</c> would state exactly what this
    /// clause infers. The question side cannot hold it in a column at all - one question serves
    /// many adverts, so it would take a child table to say which. Both are DDL, the apply-loop
    /// migration is already applied to the live database, and a schema change here is a
    /// two-step deploy in which every read of this table answers 500 in between. The predicate is
    /// reachable without one, so it is written without one.
    ///
    /// <b>Every finer rule the existing columns allow loops, and the two obvious ones loop in
    /// opposite directions.</b> "Held while something that was already waiting when it was parked
    /// is unanswered" fails because <c>SubmissionRepository.ParkAsync</c> is idempotent by state:
    /// re-parking for the same reason does not move <c>ParkedAtUtc</c>, so a posting released by
    /// its first answer and parked again on a question raised since is bounded by a timestamp
    /// older than the question it is now waiting for, and loops exactly as before. "Held while
    /// something raised since it was parked is unanswered" fails the case above, where the
    /// question predates the park. Their union is "held while anything is unanswered", which is
    /// this rule, and it is the only one of the three that cannot loop under any interleaving.
    ///
    /// <b>What it costs is a delay and never a duplicate, which is the right way round.</b> A
    /// posting whose own answer has arrived waits for the rest of the candidate's queue to drain,
    /// because nothing here can tell which of the outstanding questions was its. The queue is
    /// meant to be drained - it is what <c>list_open_questions</c> is for, a dismissal closes a
    /// question as surely as an answer does, and <c>list_applyable</c> reports its depth so a run
    /// can see why a park has not come back. The alternative error is an agent meeting the same
    /// form and parking on the same missing answer on every run for the rest of time, having
    /// spent a page load each time to learn nothing.
    ///
    /// <b><c>ParkReasonPolicy</c> is read as lists here rather than called.</b> <c>Permanent</c>
    /// and <c>AwaitingAnswer</c> exist so that <c>Contains</c> becomes an <c>IN</c>;
    /// <c>Retryable</c> is a static call over a column and has no SQL at all. The policy is still
    /// written once - those lists are derived from the same function - which is the difference
    /// between this pair and the channel filter below, where there was no way to avoid a second
    /// spelling. <c>AtsVendorDetector.Detect</c> cannot be translated either, and so runs after
    /// materialisation rather than in the projection.
    ///
    /// <b>Dismissed pairs were being returned, and this was the only match query that let
    /// them.</b> <see cref="ListAsync"/> and <see cref="GetUnassessedAsync"/> both exclude them;
    /// this one did not, so a posting the candidate had said no to on the dashboard came back to
    /// the agent on every run - and the agent had no way to know it had been refused. A dismissal
    /// is the candidate's decision about a job, and it outranks the model's verdict.
    ///
    /// <b>Every filter runs before the bound, and each needs its own test.</b> The channel filter
    /// and the projection are written out twice because EF translates one and materialises the
    /// other; the apply-URL provenance filter has the same shape and the same hazard. A filter
    /// applied after <c>Take</c> is a silent reduction of the limit, three times over in this
    /// codebase now, so it is asserted rather than assumed.
    ///
    /// <b>The bound counts jobs, not rows.</b> Duplicate listings collapse after materialisation -
    /// the grouping is over a persisted key and the choice between members is arithmetic Core
    /// owns, neither of which belongs in this SQL - so the query reads <see cref="ClusterWindow"/>
    /// times the limit and returns at most <c>limit</c> jobs. Taking the limit first and
    /// collapsing afterwards would have been the third bound in this file that a later step
    /// quietly shrank, so the over-read is what keeps a full page full. It is still a window and
    /// not a guarantee: a queue whose head is all duplicates comes back short, which is said here
    /// rather than papered over.
    ///
    /// <b>The cluster itself does not depend on that window, and it must not.</b> A second query
    /// fetches the remaining listings for the keys the page holds, under the same filters, because
    /// a primary that changed with the page size would be a different answer to the same question:
    /// ask for one row and the window cuts the pair in half, leaving the twin that has only a
    /// board page - exactly the row <c>PostingCluster</c> exists to avoid handing over. It costs
    /// one indexed read on <c>CrossBoardKey</c>, and it is skipped where the page holds no keyed
    /// row at all.
    /// </remarks>
    public async Task<IReadOnlyList<ApplyableRow>> ListApplyableAsync(
        long profileId, ApplyableQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Limit <= 0)
        {
            return [];
        }

        var matches = db.JobMatches
            .AsNoTracking()
            .Where(m => m.ProfileId == profileId
                && m.Verdict != null
                && m.Verdict >= CandidacyVerdict.Possible
                && m.DismissedAtUtc == null
                // A live application on this posting: never parked, or parked and let back in.
                && !db.Submissions.Any(s => s.ProfileId == profileId
                    && s.PostingId == m.PostingId
                    && (s.ParkedReason == null || s.UnparkedAtUtc != null))
                // Or on any other listing of the same job. The null test is load-bearing: EF
                // compiles `==` with C# semantics, so without it two unknown keys would match.
                && !(m.Posting!.CrossBoardKey != null
                    && db.Submissions.Any(s => s.ProfileId == profileId
                        && s.Posting!.CrossBoardKey == m.Posting.CrossBoardKey
                        && (s.ParkedReason == null || s.UnparkedAtUtc != null)))
                // A park that never returns the posting. `Contains` over the policy's own list,
                // because a call to ParkReasonPolicy.Retryable has no SQL.
                && !db.Submissions.Any(s => s.ProfileId == profileId
                    && s.PostingId == m.PostingId
                    && s.ParkedReason != null
                    && s.UnparkedAtUtc == null
                    && ParkReasonPolicy.Permanent.Contains(s.ParkedReason.Value))
                // A park waiting on an answer, while this candidate still owes one. The two
                // EXISTS are deliberately not nested: what holds the posting is that it is
                // parked for an answer and that an answer is outstanding somewhere, never that
                // the outstanding question names this posting. Nesting them again is the loop -
                // see the remarks.
                && !(db.Submissions.Any(s => s.ProfileId == profileId
                        && s.PostingId == m.PostingId
                        && s.ParkedReason != null
                        && s.UnparkedAtUtc == null
                        && ParkReasonPolicy.AwaitingAnswer.Contains(s.ParkedReason.Value))
                    // Raised by an advert, never from the dashboard. A note somebody wrote
                    // themselves is not what any application is waiting for, and reading it as
                    // one would empty this queue every time the dashboard was used.
                    && db.OpenQuestions.Any(q => q.ProfileId == profileId
                        && q.PostingId != null
                        && q.AnsweredAtUtc == null)));

        if (query.Since is { } since)
        {
            matches = matches.Where(m => m.Posting!.FirstSeenUtc >= since);
        }

        if (query.AssessedSince is { } assessedSince)
        {
            matches = matches.Where(m => m.AssessedAtUtc != null && m.AssessedAtUtc >= assessedSince);
        }

        if (query.MinAssessmentScore is { } floor)
        {
            // A row the model scored no number for does not clear a floor. Reading a null as
            // "not judged, so let it through" turns a safety rail into a way past one.
            matches = matches.Where(m => m.AssessmentScore != null && m.AssessmentScore >= floor);
        }

        if (query.DocumentsReady is { } ready)
        {
            // Written as two branches rather than as a comparison against the flag, so the SQL is
            // an EXISTS or a NOT EXISTS - the same expression the projection reads for
            // HasDocuments, which is what the two of them are tested against each other on.
            matches = ready
                ? matches.Where(m => db.ApplicationDocuments.Any(
                    d => d.ProfileId == profileId && d.PostingId == m.PostingId))
                : matches.Where(m => !db.ApplicationDocuments.Any(
                    d => d.ProfileId == profileId && d.PostingId == m.PostingId));
        }

        // Filtered in the query, before the bound, so asking for ten board-hosted postings
        // returns ten rather than however many of the top ten happened to be board-hosted. It
        // mirrors the projection exactly, in the same precedence, so what comes back matches what
        // each row says its channel is - written out twice rather than shared, because EF
        // translates one and materialises the other and a helper serving both would have to be an
        // expression tree nobody can read. That is what
        // The_channel_is_projected_from_the_apply_link_and_filters_before_the_bound holds
        // together, and it has already caught the two diverging once.
        matches = query.Channel switch
        {
            SubmissionChannel.Ats => matches.Where(m =>
                m.Posting!.JobUrlDirect != null
                || m.Posting.OffsiteApply == true
                || (m.Posting.OffsiteApply == null
                    && m.Posting.LocationCity != null
                    && db.JobPostings.Any(other =>
                        other.Title == m.Posting.Title
                        && other.Company == m.Posting.Company
                        && other.LocationCity == m.Posting.LocationCity
                        && other.Site != m.Posting.Site
                        && other.JobUrlDirect != null))),
            SubmissionChannel.Board => matches.Where(m =>
                m.Posting!.JobUrlDirect == null && m.Posting.OffsiteApply == false),
            SubmissionChannel.Unknown => matches.Where(m =>
                m.Posting!.JobUrlDirect == null
                && m.Posting.OffsiteApply == null
                && !(m.Posting.LocationCity != null && db.JobPostings.Any(other =>
                        other.Title == m.Posting.Title
                        && other.Company == m.Posting.Company
                        && other.LocationCity == m.Posting.LocationCity
                        && other.Site != m.Posting.Site
                        && other.JobUrlDirect != null))),
            _ => matches,
        };

        // The second filter-and-projection pair, and the same hazard as the channel: this decides
        // which rows come back and the projection decides what each one calls itself, and nothing
        // but a test stops the two drifting apart. Precedence is the projection's, exactly - a
        // published link first, and a link borrowed from another board only where the posting's
        // own board did not say it hosts the application.
        matches = query.ApplyUrlSource switch
        {
            ApplyUrlSource.Posting => matches.Where(m => m.Posting!.JobUrlDirect != null),
            ApplyUrlSource.MatchedOnAnotherBoard => matches.Where(m =>
                m.Posting!.JobUrlDirect == null
                && m.Posting.OffsiteApply != false
                && m.Posting.LocationCity != null
                && db.JobPostings.Any(other =>
                    other.Title == m.Posting.Title
                    && other.Company == m.Posting.Company
                    && other.LocationCity == m.Posting.LocationCity
                    && other.Site != m.Posting.Site
                    && other.JobUrlDirect != null)),
            ApplyUrlSource.BoardPosting => matches.Where(m =>
                m.Posting!.JobUrlDirect == null
                && !(m.Posting.OffsiteApply != false
                    && m.Posting.LocationCity != null
                    && db.JobPostings.Any(other =>
                        other.Title == m.Posting.Title
                        && other.Company == m.Posting.Company
                        && other.LocationCity == m.Posting.LocationCity
                        && other.Site != m.Posting.Site
                        && other.JobUrlDirect != null))),
            _ => matches,
        };

        // The projection is written once and handed to two queries: the page itself, and the
        // rows that complete a cluster the page cut in half. It is an expression rather than a
        // method because EF has to translate it - the same reason the channel rule below is
        // written out twice instead of shared.
        Expression<Func<JobMatchEntity, QueueRow>> project = m => new QueueRow(
            m.PostingId,
            m.Posting!.Title,
            m.Posting.Company,
            m.Posting.LocationRaw,
            // Precedence, strongest evidence first: the posting's own published link, then
            // what its own board said about itself, and only then the same job seen
            // elsewhere. A board saying it hosts the application beats a title match on
            // another board, because it is talking about this listing rather than one that
            // resembles it.
            m.Posting.JobUrlDirect != null
            || m.Posting.OffsiteApply == true
            || (m.Posting.OffsiteApply == null
                && m.Posting.LocationCity != null
                && db.JobPostings.Any(other =>
                    other.Title == m.Posting.Title
                    && other.Company == m.Posting.Company
                    && other.LocationCity == m.Posting.LocationCity
                    && other.Site != m.Posting.Site
                    && other.JobUrlDirect != null))
                ? SubmissionChannel.Ats
                : m.Posting.OffsiteApply == false
                    ? SubmissionChannel.Board
                    : SubmissionChannel.Unknown,
            m.Posting.JobUrlDirect
                ?? (m.Posting.OffsiteApply == false || m.Posting.LocationCity == null
                    ? null
                    : db.JobPostings
                        .Where(other =>
                            other.Title == m.Posting.Title
                            && other.Company == m.Posting.Company
                            && other.LocationCity == m.Posting.LocationCity
                            && other.Site != m.Posting.Site
                            && other.JobUrlDirect != null)
                        .Select(other => other.JobUrlDirect)
                        .FirstOrDefault())
                ?? m.Posting.JobUrl,
            m.Posting.JobUrlDirect != null
                ? ApplyUrlSource.Posting
                : m.Posting.OffsiteApply != false
                    && m.Posting.LocationCity != null
                    && db.JobPostings.Any(other =>
                        other.Title == m.Posting.Title
                        && other.Company == m.Posting.Company
                        && other.LocationCity == m.Posting.LocationCity
                        && other.Site != m.Posting.Site
                        && other.JobUrlDirect != null)
                    ? ApplyUrlSource.MatchedOnAnotherBoard
                    : ApplyUrlSource.BoardPosting,
            m.Score,
            m.AssessmentScore,
            m.Verdict,
            m.Rationale,
            m.RankScore,
            m.AssessedAtUtc,
            m.Posting.FirstSeenUtc,
            db.ApplicationDocuments.Any(d => d.ProfileId == profileId && d.PostingId == m.PostingId),
            m.Posting.CrossBoardKey);

        var ordered = query.Sort switch
        {
            ApplyableSort.Score => matches
                .OrderByDescending(m => m.Score)
                .ThenByDescending(m => m.RankScore),
            // Coalesced rather than left to the engine's idea of where a null sorts, and to -1
            // rather than 0 for the reason PostingCluster gives: a genuine assessment of zero is
            // a judgement, and it sorts above one that was never made.
            ApplyableSort.AssessmentScore => matches
                .OrderByDescending(m => m.AssessmentScore ?? -1)
                .ThenByDescending(m => m.RankScore),
            _ => matches.OrderByDescending(m => m.RankScore),
        };

        // Deterministic beyond the key itself. Rounding the rank to two places makes ties
        // possible, and a page whose contents shuffle between two identical requests is a bug
        // nobody can reproduce.
        var rows = await ordered
            .ThenBy(m => m.PostingId)
            .Take((int)Math.Min((long)query.Limit * ClusterWindow, int.MaxValue))
            .Select(project)
            .ToListAsync(ct);

        var keys = rows
            .Select(Key)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // The rest of each job, whether or not the window reached it. Without this the primary
        // would depend on the page size: asking for one row would read three, cut the Cloudflare
        // pair in half, and hand back the twin that has only a board page - which is precisely
        // the choice PostingCluster exists to get right. One extra query against the
        // CrossBoardKey index, and only for the keys the page actually holds.
        //
        // It runs the same filters as the page did, so a sibling that was dismissed, applied to
        // or filtered out cannot become the primary or appear as an alternate.
        var siblings = keys.Count == 0
            ? []
            : await matches
                .Where(m => m.Posting!.CrossBoardKey != null && keys.Contains(m.Posting.CrossBoardKey))
                .Select(project)
                .ToListAsync(ct);

        return Collapse(rows, siblings, query.Limit);
    }

    /// <summary>
    /// Reduces the read window to one row per job, keeping the order it arrived in.
    /// </summary>
    /// <remarks>
    /// <b>In memory rather than in SQL, and that is a decision rather than a shortcut.</b> Which
    /// of two listings an agent should act on is <see cref="PostingCluster.Choose"/> - apply-URL
    /// strength first, then the assessment - and it lives in Core so it can be asserted against
    /// the live pairs without a database. Expressing that ordering as a window function would put
    /// the rule in two places, in the one form nothing can test cheaply.
    ///
    /// <b>Membership is drawn from the queue and never from the whole corpus.</b> Both the page
    /// and the siblings that complete it have already survived every exclusion, so a listing that
    /// was dismissed, applied to or permanently parked can neither become the primary nor appear
    /// as an alternate. Handing back a row a filter had just removed, because a duplicate of it
    /// ranked higher, would be the exclusions leaking out through the deduplication.
    ///
    /// <b>A cluster ranks where its best-ranked member ranks, not where its primary does.</b> The
    /// first member encountered fixes the position and the primary fills it, which is the
    /// Cloudflare pair exactly: the row with the only direct apply URL is assessed lower than its
    /// twin, so choosing it must not also demote the job.
    /// </remarks>
    private static IReadOnlyList<ApplyableRow> Collapse(
        IReadOnlyList<QueueRow> rows, IReadOnlyList<QueueRow> siblings, int limit)
    {
        // The page and the rows that complete its clusters, with a row that is in both counted
        // once. Order comes from the page alone; membership from the pair.
        var pool = rows
            .Concat(siblings)
            .GroupBy(row => row.PostingId)
            .Select(group => group.First())
            .ToList();

        var byId = pool.ToDictionary(row => row.PostingId);

        var clusters = pool
            .Where(row => Key(row) != null)
            .GroupBy(row => Key(row)!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => PostingCluster.From(
                    group.Key,
                    [.. group.Select(row => new ClusterMember(
                        row.PostingId, row.ApplyUrlSource, row.AssessmentScore, row.RankScore, row.HasDocuments))]),
                StringComparer.Ordinal);

        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var queue = new List<ApplyableRow>(Math.Min(limit, rows.Count));

        foreach (var row in rows)
        {
            if (queue.Count == limit)
            {
                break;
            }

            var key = Key(row);

            if (key is null)
            {
                // No cross-board identity, so it is its own job. Grouping these together is the
                // collision the key answers null to prevent.
                queue.Add(Project(row, []));
            }
            else if (emitted.Add(key))
            {
                var cluster = clusters[key];

                queue.Add(Project(byId[cluster.Primary.PostingId], cluster.AlternatePostings));
            }
        }

        return queue;
    }

    /// <summary>The cluster a row belongs to, or null where it has no cross-board identity.</summary>
    /// <remarks>
    /// Trimmed because the column is fixed-length: SQL Server pads <c>nchar</c> on the way out,
    /// and a padded key would not match the same key read through a different projection. A blank
    /// one is treated as absent rather than as a cluster of its own, since a whitespace key is a
    /// write that went wrong and grouping on it would merge unrelated postings.
    /// </remarks>
    private static string? Key(QueueRow row)
        => string.IsNullOrWhiteSpace(row.DedupeKey) ? null : row.DedupeKey.Trim();

    /// <summary>
    /// One queue row as the caller sees it, with the vendor read off the apply URL.
    /// </summary>
    /// <remarks>
    /// The vendor is derived here and not in the projection because
    /// <c>AtsVendorDetector.Detect</c> is a static call over a string and has no SQL. It never
    /// throws, which is what makes it safe to run over every row of a page: the input is a URL a
    /// scraper lifted off somebody's page, and one exception would lose every other row with it.
    /// </remarks>
    private static ApplyableRow Project(QueueRow row, IReadOnlyList<ClusterMember> alternates)
        => new(
            row.PostingId,
            row.Title,
            row.Company,
            row.Location,
            row.Channel,
            row.ApplyUrl,
            row.ApplyUrlSource,
            AtsVendorDetector.Detect(row.ApplyUrl),
            row.Score,
            row.AssessmentScore,
            row.Verdict,
            row.Rationale,
            row.RankScore,
            row.AssessedAtUtc,
            row.FirstSeenUtc,
            row.HasDocuments,
            Key(row),
            alternates);

    /// <summary>
    /// One eligible row as SQL can answer it, before the vendor and the cluster are derived.
    /// </summary>
    /// <remarks>
    /// Private and flat for the reason <c>MatchRow</c> is: the alternative is materialising a
    /// <c>JobPostingEntity</c> per row and dragging an unbounded description across for a queue
    /// that shows a title. It exists separately from <see cref="ApplyableRow"/> because two of
    /// that record's fields cannot be computed in SQL at all.
    /// </remarks>
    private sealed record QueueRow(
        long PostingId,
        string Title,
        string? Company,
        string? Location,
        SubmissionChannel Channel,
        string? ApplyUrl,
        ApplyUrlSource ApplyUrlSource,
        int Score,
        int? AssessmentScore,
        CandidacyVerdict? Verdict,
        string? Rationale,
        double RankScore,
        DateTimeOffset? AssessedAtUtc,
        DateTimeOffset FirstSeenUtc,
        bool HasDocuments,
        string? DedupeKey);

    /// <summary>
    /// Where an application for this pair would go, or null where the pair is not matched.
    /// </summary>
    /// <remarks>
    /// <b>Requiring a match is the same guarantee <c>applications</c> enforces</b>, for a related
    /// reason. There, a document written without a gap list has nothing stopping it inventing
    /// skills; here, a submission recorded against an arbitrary posting id is a pipeline entry
    /// nothing in this system ever chose, and the ids will be supplied by a model. The corpus is
    /// public text and shared; a candidate's pipeline is neither.
    ///
    /// The apply link is resolved here rather than at the call site so exactly one place decides
    /// that a missing <c>JobUrlDirect</c> means the board hosts it.
    /// </remarks>
    public Task<ApplyTarget?> ResolveApplyTargetAsync(
        long profileId, long postingId, CancellationToken ct = default)
        => db.JobMatches
            .AsNoTracking()
            .Where(m => m.ProfileId == profileId && m.PostingId == postingId)
            .Select(m => new ApplyTarget(
                m.Posting!.Title,
                m.Posting.OffsiteApply == true || m.Posting.JobUrlDirect != null
                    ? SubmissionChannel.Ats
                    : m.Posting.OffsiteApply == false
                        ? SubmissionChannel.Board
                        : SubmissionChannel.Unknown,
                m.Posting.JobUrlDirect ?? m.Posting.JobUrl))
            .FirstOrDefaultAsync(ct);

    /// <summary>One match in full, with whether a document has already been generated for it.</summary>
    public async Task<MatchRow?> GetDetailAsync(
        long profileId, long postingId, CancellationToken ct = default)
        => await db.JobMatches
            .AsNoTracking()
            .Where(m => m.ProfileId == profileId && m.PostingId == postingId)
            .Select(m => new MatchRow(
                m.PostingId,
                m.Posting!.Title,
                m.Posting.Company,
                m.Posting.LocationRaw,
                m.Posting.AnnualSalaryMin,
                m.Posting.AnnualSalaryMax,
                m.Posting.AnnualSalaryCurrency,
                m.Posting.WorkArrangement,
                m.Posting.Seniority,
                m.Posting.DatePosted,
                m.Score,
                m.RequiredGapCount,
                m.Verdict,
                m.AssessmentScore,
                m.Rationale,
                m.ScoredAtUtc,
                m.AssessedAtUtc,
                m.DismissedAtUtc,
                m.ComponentsJson,
                m.MatchedJson,
                m.GapsJson,
                m.StrengthsJson,
                m.AssessmentGapsJson,
                m.EmphasiseJson,
                m.ScorerVersion,
                m.Similarity,
                m.RankScore,
                db.ApplicationDocuments.Any(d => d.ProfileId == profileId && d.PostingId == postingId)))
            .FirstOrDefaultAsync(ct);

    /// <summary>Every profile with something to score. Small by construction.</summary>
    public Task<List<long>> GetProfileIdsAsync(CancellationToken ct = default)
        => db.CandidateProfiles
            .AsNoTracking()
            .Where(p => p.ExtractedAtUtc != null || p.Concepts.Any())
            .Select(p => p.Id)
            .ToListAsync(ct);

    /// <summary>
    /// What this candidate's own matched band asks for, by concept.
    /// </summary>
    /// <remarks>
    /// The supply half of the skills gap. Scoped to one profile and one score floor, so it
    /// lands on the <c>(ProfileId, Score)</c> index rather than reading the assertion table
    /// whole - which is what <see cref="JobPostingQueryRepository.GetConceptDemandAsync"/>
    /// exists to avoid and why that one takes a bounded key list. The keys this returns are
    /// that list: the corpus figure is context for concepts the candidate's band already
    /// names, never an aggregate over all 222.
    ///
    /// <para>
    /// Counts distinct postings rather than assertion rows, for the same reason the corpus
    /// query does: a concept the board tagged and the description also mentioned is two rows
    /// for one posting, and counting both would make thoroughly-recorded concepts look more
    /// in demand than they are.
    /// </para>
    ///
    /// <para>
    /// Dismissed pairs are excluded. A concept only asked for by postings the candidate has
    /// already said no to is not a gap in their profile; it is a gap in a job they do not
    /// want, and putting it at the top of the list would be advice to chase work they have
    /// just rejected.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, int>> GetInBandConceptDemandAsync(
        long profileId, int minimumScore, CancellationToken ct = default)
    {
        var rows = await db.JobMatches
            .AsNoTracking()
            .Where(m => m.ProfileId == profileId
                && m.Score >= minimumScore
                && m.DismissedAtUtc == null)
            .SelectMany(m => m.Posting!.Concepts)
            .GroupBy(c => c.Concept!.ConceptKey)
            .Select(g => new { g.Key, Count = g.Select(c => c.PostingId).Distinct().Count() })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.Key, r => r.Count, StringComparer.Ordinal);
    }

    /// <summary>
    /// Records that the candidate is not interested in a posting, or takes it back.
    /// </summary>
    /// <remarks>
    /// Idempotent, and deliberately not an event log. A dismissal has no history worth
    /// keeping: the question it answers is "is this pair on the shortlist", and the second
    /// dismissal of the same posting says nothing the first did not. Submissions are an
    /// append-only log because what was sent and when is a record somebody may need to
    /// defend; what you scrolled past is not.
    ///
    /// <para>
    /// Returns false where the pair does not exist for this profile, which the endpoint turns
    /// into a 404. Silently succeeding on a posting that was never scored would let a client
    /// believe it had suppressed something it had not.
    /// </para>
    /// </remarks>
    public async Task<bool> SetDismissedAsync(
        long profileId, long postingId, DateTimeOffset? dismissedAtUtc, CancellationToken ct = default)
    {
        var entity = await db.JobMatches
            .FirstOrDefaultAsync(m => m.ProfileId == profileId && m.PostingId == postingId, ct);

        if (entity is null)
        {
            return false;
        }

        // Only when it changes. The sweep already avoids touching rows that did not move, for
        // the same reason: this database is billed by wall-clock time.
        if (entity.DismissedAtUtc != dismissedAtUtc)
        {
            entity.DismissedAtUtc = dismissedAtUtc;
            await db.SaveChangesAsync(ct);
        }

        return true;
    }

    /// <summary>
    /// One pair in full, for the writing pass.
    /// </summary>
    /// <remarks>
    /// Returns the match and the advert together because the writer needs both and fetching
    /// them separately would wake the database twice for one request.
    /// </remarks>
    public async Task<(MatchResult Match, CandidacyAssessment? Assessment, PostingBrief Posting)?> GetForWritingAsync(
        long profileId, long postingId, CancellationToken ct = default)
    {
        var row = await db.JobMatches
            .AsNoTracking()
            .Where(m => m.ProfileId == profileId && m.PostingId == postingId)
            .Select(m => new
            {
                m.Score,
                m.ComponentsJson,
                m.MatchedJson,
                m.GapsJson,
                m.ScorerVersion,
                m.Verdict,
                m.AssessmentScore,
                m.Rationale,
                m.StrengthsJson,
                m.AssessmentGapsJson,
                m.EmphasiseJson,
                m.AssessmentModel,
                m.AssessmentVersion,
                m.Posting!.Title,
                m.Posting.Company,
                m.Posting.Description,
                m.Posting.Site,
            })
            .FirstOrDefaultAsync(ct);

        if (row is null)
        {
            return null;
        }

        var match = Rebuild(
            row.Score, row.ComponentsJson, row.MatchedJson, row.GapsJson, row.ScorerVersion);

        var assessment = row.AssessmentVersion is null
            ? null
            : new CandidacyAssessment
            {
                Verdict = row.Verdict ?? CandidacyVerdict.Unknown,
                Score = row.AssessmentScore ?? 0,
                Rationale = row.Rationale,
                Strengths = Read<string>(row.StrengthsJson),
                Gaps = Read<string>(row.AssessmentGapsJson),
                Emphasise = Read<string>(row.EmphasiseJson),
                Model = row.AssessmentModel,
                Version = row.AssessmentVersion ?? CandidacyAssessment.CurrentVersion,
            };

        return (match, assessment, new PostingBrief(
            postingId, row.Title, row.Company, row.Description ?? string.Empty, row.Site));
    }

    /// <summary>
    /// Reassembles a stored match from its JSON columns.
    /// </summary>
    /// <remarks>
    /// <see cref="MatchResult.Coverage"/> is recomputed from the components rather than stored.
    /// It is a pure function of them - the share of the nominal weight the posting answered -
    /// so a column would be a second copy of a derived value that could drift from the first,
    /// and it would need a migration to add. Rows written before coverage existed rebuild with
    /// the right value for free.
    /// </remarks>
    private static MatchResult Rebuild(
        int score, string? componentsJson, string? matchedJson, string? gapsJson, int version)
    {
        var components = Read<MatchComponent>(componentsJson);

        return new MatchResult
        {
            Score = score,
            Coverage = Math.Clamp(components.Sum(c => c.Weight), 0, 1),
            Components = components,
            Matched = Read<ConceptMatch>(matchedJson),
            Gaps = Read<ConceptGap>(gapsJson),
            Version = version,
        };
    }

    /// <summary>
    /// Reading a stored JSON column back.
    /// </summary>
    /// <remarks>
    /// Never throws. These columns are written by this repository, so malformed content means
    /// something has gone badly wrong elsewhere - and the right response is a match that
    /// displays without its breakdown, not an endpoint that answers 500 for one bad row.
    ///
    /// Public because <see cref="MatchRow"/> hands its callers the same reader: a list response
    /// leaves these columns as strings, and whoever does parse one should parse it the same way
    /// the repository does.
    /// </remarks>
    public static IReadOnlyList<T> Read<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<T>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

