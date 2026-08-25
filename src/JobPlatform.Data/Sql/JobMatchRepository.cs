using System.Text.Json;
using JobPlatform.Core.Applications;
using JobPlatform.Core.Enrichment;
using JobPlatform.Core.Matching;
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
    string? ComponentsJson,
    string? MatchedJson,
    string? GapsJson,
    string? StrengthsJson,
    string? AssessmentGapsJson,
    string? EmphasiseJson,
    int ScorerVersion,
    bool HasApplication)
{
    /// <summary>Reads one of the JSON columns back. Never throws - see the repository.</summary>
    public IReadOnlyList<T> Read<T>(string? json) => JobMatchRepository.Read<T>(json);
}

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
    /// </remarks>
    public async Task<int> UpsertScoresAsync(
        long profileId,
        IReadOnlyList<(PostingFacts Posting, MatchResult Result)> scores,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scores);

        if (scores.Count == 0)
        {
            return 0;
        }

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
            else if (entity.Score == result.Score && entity.ScorerVersion == result.Version)
            {
                // Nothing moved. Skipping the write keeps the sweep from touching every row it
                // looked at, which on a database billed by wall-clock time is the difference
                // between a nightly job and a nightly bill.
                continue;
            }
            else if (entity.Score != result.Score)
            {
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

            written++;
        }

        if (written > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return written;
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
    public async Task<IReadOnlyList<CandidacyRequest>> GetUnassessedAsync(
        long profileId, int minimumScore, int limit, CancellationToken ct = default)
    {
        var rows = await db.JobMatches
            .AsNoTracking()
            .Where(m => m.ProfileId == profileId
                && m.Score >= minimumScore
                && (m.AssessedAtUtc == null || m.AssessmentVersion != CandidacyAssessment.CurrentVersion))
            .OrderByDescending(m => m.Score)
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
            .Where(r => !string.IsNullOrWhiteSpace(r.Description))
            .Select(r => new CandidacyRequest(
                r.PostingId,
                r.Title,
                r.Company,
                r.Description!,
                new MatchResult
                {
                    Score = r.Score,
                    Components = Read<MatchComponent>(r.ComponentsJson),
                    Matched = Read<ConceptMatch>(r.MatchedJson),
                    Gaps = Read<ConceptGap>(r.GapsJson),
                    Version = r.ScorerVersion,
                }))
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
    /// Ordered by the deterministic score rather than by the model's, because the model's is
    /// null for everything the nightly sweep has not reached and a null-last ordering would put
    /// this morning's best new posting at the bottom of the page. The client has both numbers
    /// and can re-sort once it knows which rows carry a verdict.
    ///
    /// The description is excluded. This is a list, and it lands on the
    /// <c>(ProfileId, Score)</c> index precisely so that showing fifty matches does not read
    /// fifty unbounded text columns.
    /// </remarks>
    public async Task<IReadOnlyList<MatchRow>> ListAsync(
        long profileId,
        int minimumScore,
        bool assessedOnly,
        int limit,
        int offset,
        CancellationToken ct = default)
    {
        var query = db.JobMatches
            .AsNoTracking()
            .Where(m => m.ProfileId == profileId && m.Score >= minimumScore);

        if (assessedOnly)
        {
            query = query.Where(m => m.AssessedAtUtc != null);
        }

        return await query
            .OrderByDescending(m => m.Score)
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
                m.ComponentsJson,
                m.MatchedJson,
                m.GapsJson,
                m.StrengthsJson,
                m.AssessmentGapsJson,
                m.EmphasiseJson,
                m.ScorerVersion,
                false))
            .ToListAsync(ct);
    }

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
                m.ComponentsJson,
                m.MatchedJson,
                m.GapsJson,
                m.StrengthsJson,
                m.AssessmentGapsJson,
                m.EmphasiseJson,
                m.ScorerVersion,
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

        var match = new MatchResult
        {
            Score = row.Score,
            Components = Read<MatchComponent>(row.ComponentsJson),
            Matched = Read<ConceptMatch>(row.MatchedJson),
            Gaps = Read<ConceptGap>(row.GapsJson),
            Version = row.ScorerVersion,
        };

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

