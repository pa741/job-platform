using System.Text.Json;
using JobPlatform.Core.Applications;
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

/// <summary>Where an apply URL came from, and therefore how much to trust it.</summary>
/// <remarks>
/// <b>Two of these are facts and one is an inference.</b> Keeping them apart is what stops a
/// matched link being presented as the employer's own, which matters because the match is on
/// title, employer and city rather than on anything either board guarantees.
/// </remarks>
public enum ApplyUrlSource
{
    /// <summary>No direct link known. This is the board's own posting page.</summary>
    BoardPosting = 0,

    /// <summary>The posting itself published the employer's apply URL.</summary>
    Posting = 1,

    /// <summary>
    /// Taken from the same job on another board, matched on title, employer and city.
    /// </summary>
    /// <remarks>
    /// An inference. It recovers roughly 5% of the links LinkedIn stopped publishing, at no
    /// request and no risk, and the city is part of the match because without it better than a
    /// quarter of the candidates were one employer advertising one title in several cities.
    /// </remarks>
    MatchedOnAnotherBoard = 2,
}

/// <summary>
/// One posting worth applying to, as the agent surface sees it.
/// </summary>
/// <remarks>
/// Deliberately narrow. This is a work queue, not a browse response: enough to decide which
/// posting to act on and where to act on it, and nothing more. The advert body and the generated
/// documents are a second call, so a shortlist of fifty is a page rather than megabytes.
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
public sealed record ApplyableRow(
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
    double RankScore);

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
    /// What this candidate should apply to next: judged worth it, and not yet sent.
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
    /// Already-submitted postings are excluded by a subquery rather than by loading a set and
    /// filtering in memory - one round trip, and the same shape <see cref="GetDetailAsync"/>
    /// already uses to answer <c>HasApplication</c>. Filtering after <c>Take</c> would be the
    /// third instance in this codebase of a bound that a later filter quietly shrinks.
    /// </remarks>
    public async Task<IReadOnlyList<ApplyableRow>> ListApplyableAsync(
        long profileId,
        SubmissionChannel? channel,
        int limit,
        CancellationToken ct = default)
    {
        var query = db.JobMatches
            .AsNoTracking()
            .Where(m => m.ProfileId == profileId
                && m.Verdict != null
                && m.Verdict >= CandidacyVerdict.Possible
                && !db.Submissions.Any(s => s.ProfileId == profileId && s.PostingId == m.PostingId));

        // Filtered in the query, before the bound, so asking for ten board-hosted postings
        // returns ten rather than however many of the top ten happened to be board-hosted.
        // Filtered on the same expression the projection uses, so what comes back matches what
        // each row says its channel is. Written out twice rather than shared, because EF has to
        // translate it here and materialise it there, and a helper serving both would have to be
        // an expression tree nobody can read.
        // Mirrors the projection below exactly, in the same precedence. The two are written out
        // twice because EF translates one and materialises the other, and a helper serving both
        // would have to be an expression tree nobody can read - so
        // The_channel_is_projected_from_the_apply_link_and_filters_before_the_bound is what
        // holds them together. It has already caught them diverging once.
        query = channel switch
        {
            SubmissionChannel.Ats => query.Where(m =>
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
            SubmissionChannel.Board => query.Where(m =>
                m.Posting!.JobUrlDirect == null && m.Posting.OffsiteApply == false),
            SubmissionChannel.Unknown => query.Where(m =>
                m.Posting!.JobUrlDirect == null
                && m.Posting.OffsiteApply == null
                && !(m.Posting.LocationCity != null && db.JobPostings.Any(other =>
                        other.Title == m.Posting.Title
                        && other.Company == m.Posting.Company
                        && other.LocationCity == m.Posting.LocationCity
                        && other.Site != m.Posting.Site
                        && other.JobUrlDirect != null))),
            _ => query,
        };

        return await query
            .OrderByDescending(m => m.RankScore)
            .ThenBy(m => m.PostingId)
            .Take(limit)
            .Select(m => new ApplyableRow(
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
                m.RankScore))
            .ToListAsync(ct);
    }

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

        return (match, assessment, new PostingBrief(postingId, row.Title, row.Company, row.Description ?? string.Empty));
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

