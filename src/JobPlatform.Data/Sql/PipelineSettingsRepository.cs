using JobPlatform.Core.Settings;
using JobPlatform.Data.Sql.Entities;
using Microsoft.EntityFrameworkCore;

namespace JobPlatform.Data.Sql;

/// <summary>
/// Reads and writes one person's pipeline settings, and only ever their own.
/// </summary>
/// <remarks>
/// <b>Every method a person can reach takes a subject id, and none of them takes a profile id.</b>
/// That is the authorisation boundary expressed as a type rather than as a rule somebody has to
/// remember, exactly as <see cref="CandidateProfileRepository"/> and
/// <see cref="ScraperSearchRepository"/> express it: there is no overload an endpoint could hand a
/// route parameter to, so there is no way to write a route that reconfigures a stranger's nights
/// by mistake. The internal profile id is resolved here and never accepted from outside.
///
/// <b><see cref="GetForProfileAsync"/> and <see cref="GetForProfilesAsync"/> are the deliberate
/// exceptions</b>, and they exist for the unattended nightly passes, which iterate profile ids and
/// hold no subject at all. <see cref="ScraperSearchRepository.ListForPublishAsync"/> is the same
/// shape of named exception for the same kind of caller. Read the remarks on both before calling
/// either.
///
/// <b>Nothing here validates and nothing here clamps.</b> The bounds live in
/// <see cref="PipelineSettingsValidation"/> and are applied where a message can reach the person
/// who typed the number, which is the endpoint - the same division
/// <c>ScraperSearchValidation</c> already runs under. Clamping instead would be worse than either:
/// a setting that is silently corrected is a setting that lies to the person who typed it, which
/// is the whole argument for <c>MaxDraftsPerNight</c> being a refusal rather than the
/// <c>Math.Clamp</c> it replaced. So a value that reached this class without being validated is
/// stored as given, and behaves as the number it literally is.
///
/// <b>There is no delete, and none is needed.</b> Resetting to the shipped behaviour is
/// <c>SaveAsync(subjectId, PipelineSettings.Default, ...)</c>: a row of default values and no row
/// at all are read back identically, because an absent row <i>is</i> the defaults. A delete would
/// be a second spelling of one operation and the only difference between them would be a
/// timestamp.
/// </remarks>
public sealed class PipelineSettingsRepository(JobsDbContext db)
{
    /// <summary>
    /// What this candidate's pipeline is configured to do. Never null.
    /// </summary>
    /// <remarks>
    /// <b>A candidate who has never saved gets <see cref="PipelineSettings.Default"/>, and that is
    /// the property the whole feature is judged on.</b> An unconfigured deployment must not be
    /// able to tell the feature arrived, so the answer for "no row" has to be a complete
    /// configuration equal to the constants the shipped code already ran - never null, never a
    /// half-filled record, and never a signal for the caller to go and find the defaults itself.
    /// Handing back null would push that decision to every call site, and the call sites are the
    /// nightly passes and the submission write path: the first one to read a null as "zero" would
    /// switch somebody's pipeline off silently.
    ///
    /// <b>An absent row is one meaning and not two, which is why this is not the mistake
    /// <c>JobPostings.OffsiteApply</c> was added to undo.</b> That column is three-state because
    /// its two nulls really were different facts - "the board says it hosts the application" and
    /// "nobody ever read the detail page" - collapsed into one representation, and telling them
    /// apart needed a second column to do it. Here the absence has exactly one cause and exactly
    /// one meaning: nobody has chosen. There is no second reason a row could be missing, no
    /// question a reader could ask that the two would answer differently, and no work that would
    /// be done differently if we could tell them apart - "has not chosen" and "wants what the
    /// shipped code does" are the same instruction to every consumer. The test that column
    /// established is whether a second column could separate the two nulls; here there is only one
    /// null to separate.
    ///
    /// <b>It also answers the defaults for a subject with no profile at all</b>, which costs
    /// nothing and is honest: somebody with no profile has no pipeline running, so the settings
    /// their pipeline would run under are the ones nobody configured. <see cref="SaveAsync"/> is
    /// the method that has to care about a missing profile, because a row needs a key.
    /// </remarks>
    public async Task<PipelineSettings> GetAsync(string subjectId, CancellationToken ct = default)
    {
        // One query with a join rather than a profile lookup followed by a settings read. The
        // database is billed by wall-clock time online, so a second wakeup for a page that is
        // already reading the profile is a cost rather than a tidiness question.
        var stored = await db.PipelineSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Profile!.SubjectId == subjectId, ct);

        return stored is null ? PipelineSettings.Default : ToDomain(stored);
    }

    /// <summary>
    /// Stores this candidate's settings, replacing what was there. Null where they have no
    /// profile.
    /// </summary>
    /// <remarks>
    /// <b>A replace and not a merge, like a profile save and a search save, and for a stronger
    /// reason than either.</b> A partial override is a legitimate thing for a form to send and it
    /// is resolved before it reaches here, by <c>PipelineSettings.Default with { ... }</c> over
    /// whatever the request omitted - so what arrives at this method is always a complete
    /// configuration, and storing it whole is the only thing that keeps the row and the record
    /// meaning the same thing. A merge at this layer would give two places a partial document is
    /// resolved, which is two answers to "what does an omitted field mean" and one of them will be
    /// wrong.
    ///
    /// <b>Null rather than an exception for "no profile yet".</b> The endpoint answers 404, and
    /// the honest reading is that settings are an attribute of a pipeline that does not exist yet
    /// rather than an error the caller made. There is nothing to key the row on until the profile
    /// is saved, and inventing a profile here would create an empty one as a side effect of
    /// visiting a settings page - which is a row the extraction sweep would then pick up.
    ///
    /// <b>The read is explicitly <c>AsTracking()</c> because it is mutated.</b> That reads as a
    /// restatement of EF's own default and it is not redundant: the API host once set
    /// <c>NoTracking</c> globally on the argument that it only ever read from SQL, and under that
    /// a read-then-mutate saves nothing and throws nothing - four write paths were doing exactly
    /// that before anybody noticed. Here it would mean a settings page that reports success and
    /// changes nothing, and the candidate would find out from a bill.
    ///
    /// <b>Two round trips on the first save and one on every save after it.</b> The tracked read
    /// is by subject id, so a candidate who already has a row is one query; only the insert path
    /// needs the profile id, and that is also the only path that can distinguish "no settings yet"
    /// from "no profile at all". The per-row round trip warning is about loops against the free
    /// SQL grant - this is one person pressing save, and the nightly passes use
    /// <see cref="GetForProfilesAsync"/> precisely so they are not this shape.
    /// </remarks>
    /// <returns>The settings as stored, read back through the same mapping <see cref="GetAsync"/>
    /// uses - so a mapping that has lost a field shows up on the save rather than on the next page
    /// load.</returns>
    public async Task<PipelineSettings?> SaveAsync(
        string subjectId,
        PipelineSettings settings,
        TimeProvider time,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(time);

        var now = time.GetUtcNow();

        // AsTracking() said out loud: this row is read in order to be changed.
        var entity = await db.PipelineSettings
            .AsTracking()
            .FirstOrDefaultAsync(s => s.Profile!.SubjectId == subjectId, ct);

        if (entity is null)
        {
            var profileId = await db.CandidateProfiles
                .AsNoTracking()
                .Where(p => p.SubjectId == subjectId)
                .Select(p => (long?)p.Id)
                .FirstOrDefaultAsync(ct);

            if (profileId is not { } id)
            {
                return null;
            }

            entity = new PipelineSettingsEntity
            {
                ProfileId = id,
                CreatedUtc = now,
            };

            db.PipelineSettings.Add(entity);
        }

        Apply(entity, settings, now);

        await db.SaveChangesAsync(ct);

        return ToDomain(entity);
    }

    /// <summary>
    /// One profile's settings, for a caller that already holds a profile id. Never null.
    /// </summary>
    /// <remarks>
    /// <b>This is the deliberate exception to the subject-id rule, and it exists for the
    /// unattended passes.</b> The match sweep and the application generation pass both iterate
    /// every profile the platform holds - the sweep takes its list from
    /// <c>JobMatchRepository.GetProfileIdsAsync</c> - and neither of them has a subject id to be
    /// scoped by, because neither of them was started by a person. There is no token, no caller
    /// and nothing to authorise; the pass is the platform acting on its own schedule.
    /// <see cref="ScraperSearchRepository.ListForPublishAsync"/> is the same shape of exception
    /// for the same kind of caller, and it is named rather than general for the same reason.
    ///
    /// <b>What the exception permits and what it does not.</b> A profile id that reaches this
    /// method must have been iterated by an unattended pass or resolved from the caller's own
    /// subject id one layer up - the way the submission store, the answer store and the CV library
    /// all take a profile id the caller already proved is theirs. <b>What must never happen is a
    /// route or an MCP tool taking a profile id out of a request and handing it here.</b> A tool
    /// signature is the easier place to get that wrong than a route, because the argument is named
    /// by a model rather than by a router, and an unused <c>profileId</c> parameter is exactly
    /// what a model would helpfully fill in - which is why no tool takes one at all.
    ///
    /// <b>Absent row means the defaults here too</b>, and it has to be the same answer
    /// <see cref="GetAsync"/> gives: if the nightly pass and the settings page disagreed about
    /// what an unconfigured candidate runs, the page would be describing a pipeline that does not
    /// exist.
    ///
    /// <b>Prefer <see cref="GetForProfilesAsync"/> in a loop.</b> This one is a round trip per
    /// call, and a pass that calls it once per profile is the per-row pattern this database is
    /// least able to afford.
    /// </remarks>
    public async Task<PipelineSettings> GetForProfileAsync(
        long profileId, CancellationToken ct = default)
    {
        var stored = await db.PipelineSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.ProfileId == profileId, ct);

        return stored is null ? PipelineSettings.Default : ToDomain(stored);
    }

    /// <summary>
    /// Settings for many profiles in one query, keyed by profile id. Every id asked for is
    /// present in the answer.
    /// </summary>
    /// <remarks>
    /// <b>The nightly form of <see cref="GetForProfileAsync"/>, and the reason it exists is the
    /// bill.</b> Both passes loop over every profile the platform holds and each of them needs
    /// this row before it can decide what a night costs. Fetching it inside the loop is a wakeup
    /// per candidate against a database billed by wall-clock time, which is the per-row round trip
    /// this codebase warns about repeatedly - and it is worst in exactly this position, at the top
    /// of a pass, before any of the work that would have justified being awake. One query for the
    /// whole sweep costs the same as one query for the first candidate.
    ///
    /// <b>Every requested id is present in the returned dictionary, mapped to
    /// <see cref="PipelineSettings.Default"/> where no row exists.</b> Returning only the stored
    /// rows would put "absent means the defaults" back at every call site, which is the decision
    /// this repository exists to make once - and the call site is a loop, where the natural
    /// spelling of a miss is to skip the candidate rather than to run them on the defaults. An id
    /// that was never asked for is a <c>KeyNotFoundException</c> rather than a quiet default, which
    /// is the right way round: a pass reading settings for a profile it did not fetch is a bug,
    /// and a loud one is cheaper than a candidate silently run on somebody else's assumptions.
    ///
    /// <b>Bounded by the number of candidates, which is what makes one query safe here.</b> The
    /// input is the platform's profile list rather than anything a caller composes, so this is not
    /// an unbounded <c>IN</c> waiting to meet a parameter limit. A caller with a genuinely large
    /// list should page it, and should not reach for <see cref="GetForProfileAsync"/> in a loop
    /// instead.
    ///
    /// <b>It carries the same exception to the subject-id rule</b> that
    /// <see cref="GetForProfileAsync"/> does, on the same terms; read the remarks there. The two
    /// cannot be confused at a call site because their parameters are different types, so a
    /// transposition does not compile.
    /// </remarks>
    public async Task<IReadOnlyDictionary<long, PipelineSettings>> GetForProfilesAsync(
        IReadOnlyCollection<long> profileIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profileIds);

        var wanted = profileIds.Distinct().ToList();

        if (wanted.Count == 0)
        {
            // No query at all rather than one whose IN clause can only answer nothing. A sweep
            // with no profiles to run still wakes the database if this is written as a query.
            return new Dictionary<long, PipelineSettings>();
        }

        var stored = await db.PipelineSettings
            .AsNoTracking()
            .Where(s => wanted.Contains(s.ProfileId))
            .ToListAsync(ct);

        var byProfile = stored.ToDictionary(s => s.ProfileId, ToDomain);

        var answer = new Dictionary<long, PipelineSettings>(wanted.Count);

        foreach (var id in wanted)
        {
            answer[id] = byProfile.TryGetValue(id, out var settings) ? settings : PipelineSettings.Default;
        }

        return answer;
    }

    /// <summary>
    /// The stored row as the pipeline sees it.
    /// </summary>
    /// <remarks>
    /// <b>The nine fields are written out by hand here and in <see cref="Apply"/>, and those are
    /// the only two places in the system where that happens.</b> Everywhere else a variant of
    /// these settings is built with <c>PipelineSettings.Default with { ... }</c> precisely so that
    /// nine values are never copied across by hand - but a record and a row are two types, and
    /// there is no <c>with</c> that spans them. So the copying is confined to one file, in one
    /// direction each, rather than repeated at every consumer.
    ///
    /// <b>Nothing in the type system catches a forgotten line in either direction.</b> A field
    /// missed here reads back as its default, which is indistinguishable from a candidate who
    /// asked for the default; a field missed in <see cref="Apply"/> is a save that silently drops
    /// one lever. Both spellings of the object initialiser behave the same way, so this is a test
    /// obligation rather than a compiler one: a round trip through this pair, over a record whose
    /// nine values are all distinct and none of them the default, is what holds the two halves
    /// together.
    /// </remarks>
    private static PipelineSettings ToDomain(PipelineSettingsEntity entity)
        => new()
        {
            AssessmentsPerNight = entity.AssessmentsPerNight,
            AssessmentThreshold = entity.AssessmentThreshold,
            RecentSharePercent = entity.RecentSharePercent,
            RecentWindowDays = entity.RecentWindowDays,
            DraftsPerNight = entity.DraftsPerNight,
            DraftMinAssessmentScore = entity.DraftMinAssessmentScore,
            DraftPostedWithinDays = entity.DraftPostedWithinDays,
            DailySendCap = entity.DailySendCap,
            ChaseAfterDays = entity.ChaseAfterDays,
        };

    /// <summary>The submitted settings onto the row, whole.</summary>
    /// <remarks>
    /// <see cref="PipelineSettingsEntity.DraftPostedWithinDays"/> is assigned rather than
    /// conditionally assigned, so clearing the age bound writes NULL rather than leaving
    /// yesterday's number standing. That is the one field where a merge and a replace differ
    /// visibly, and it is the field where "did not choose" has to survive the write.
    /// </remarks>
    private static void Apply(
        PipelineSettingsEntity entity, PipelineSettings settings, DateTimeOffset now)
    {
        entity.AssessmentsPerNight = settings.AssessmentsPerNight;
        entity.AssessmentThreshold = settings.AssessmentThreshold;
        entity.RecentSharePercent = settings.RecentSharePercent;
        entity.RecentWindowDays = settings.RecentWindowDays;
        entity.DraftsPerNight = settings.DraftsPerNight;
        entity.DraftMinAssessmentScore = settings.DraftMinAssessmentScore;
        entity.DraftPostedWithinDays = settings.DraftPostedWithinDays;
        entity.DailySendCap = settings.DailySendCap;
        entity.ChaseAfterDays = settings.ChaseAfterDays;
        entity.UpdatedUtc = now;
    }
}
