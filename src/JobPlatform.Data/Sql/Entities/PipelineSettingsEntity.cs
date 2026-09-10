namespace JobPlatform.Data.Sql.Entities;

/// <summary>
/// One candidate's stored answers to the nine levers the nightly passes read, as a row of its
/// own keyed on their profile.
/// </summary>
/// <remarks>
/// <b>A separate table rather than nine columns on <see cref="CandidateProfileEntity"/>, and
/// there are two independent reasons for that. Either one would be enough on its own.</b>
///
/// <b>First: a profile save is a replace, not a merge.</b> The profile is a form - the client
/// sends the whole record and the whole record is what is stored, because a partial update has
/// no way to express "delete the third job", which is a thing people do. A preference living on
/// that record is therefore a preference an ordinary save can blank, and the client that does it
/// is the one least likely to be noticed doing it: a page that predates the field, a client a
/// version behind, or the profile form itself submitted from a tab opened before the settings
/// were changed in another. The failure is silent and it is expensive - the candidate's night
/// quietly returns to the shipped defaults, and the only evidence is a bill that changed shape.
/// A separate row survives a profile save because a profile save does not name it.
///
/// <b>Second: <c>CandidateProfiles.ExtractionInputHash</c> is computed over
/// <c>CandidateProfile.ToDocument()</c>, and that hash is what decides whether a model call is
/// bought.</b> It exists so that correcting a phone number does not cost an extraction and does
/// not invalidate every match already scored. A settings column sitting on that entity is one
/// edit away from being folded into the document, and whoever folds it in will be doing something
/// reasonable - widening the hash after a real omission - with nothing on the record marking
/// which of its fields are prose the extractor reads and which are preferences about spending.
/// The consequence would be that raising drafts per night marks the profile stale, buys an
/// extraction and re-scores the corpus. There is no path from this table into
/// <c>ToDocument()</c>, so the mistake has nowhere to happen rather than being a rule somebody
/// has to remember. <c>PostingEmbeddingEntity</c> is a side table on the same kind of argument,
/// and <c>CvVariantEntity</c> deliberately has no navigation to the profile side for a stricter
/// version of it.
///
/// <b>The row is written whole, and no column is nullable to mean "use the default".</b> Nine
/// nullable columns would be nine three-state columns, and this codebase already knows what one
/// of those costs: <c>JobPostings.OffsiteApply</c> is three-state on purpose, and its third state
/// had to be argued for at length because two different nulls had been sharing one
/// representation. Here there is nothing to tell apart - "I want forty" and "whatever you think"
/// produce identical behaviour from every reader - so a representation able to distinguish them
/// would be carrying a difference nothing could act on. A partial override is resolved before the
/// data reaches this table, by <c>PipelineSettings.Default with { ... }</c>, which is what makes
/// a partial document a partial override by construction.
///
/// <b>The consequence, stated rather than left to be discovered: a stored row is a set of answers
/// and not a snapshot of the defaults.</b> If a shipped default ever moves, it moves for the
/// candidates who never saved and not for the ones who saved the old value - because they did
/// save it. That is the right way round, and it is the price of row-level rather than
/// column-level granularity.
///
/// <b>An absent row is the defaults, and that is not a third state.</b> See
/// <c>PipelineSettingsRepository.GetAsync</c>, which is where that decision is executed and where
/// the argument for it is written down.
///
/// <b>The nine value columns carry no reasoning of their own, deliberately.</b> Every one of them
/// is argued in full on the member of <c>PipelineSettings</c> it stores - what it replaces, why
/// the default is that number, and what zero means on it - and a second copy here would be a
/// second copy of decisions that were made once, free to drift from the first. The only
/// annotations below are storage facts the pure record cannot state.
/// </remarks>
public sealed class PipelineSettingsEntity
{
    /// <summary>
    /// Whose settings these are, and the primary key.
    /// </summary>
    /// <remarks>
    /// <b>The key and the foreign key are one column, which is what makes "one row per candidate"
    /// a constraint rather than an intention.</b> A second row could only ever be a stale first,
    /// and the primary key refuses it at the database rather than leaving the upsert as the only
    /// thing standing between a candidate and two configurations. <c>ProfileEmbeddings</c> is
    /// keyed the same way for the same reason: one vector per profile, a second would only be a
    /// stale first.
    ///
    /// <b>A profile id and not a subject id, even though every route-facing method above it takes
    /// only subject ids.</b> The subject id lives on <see cref="CandidateProfileEntity"/> once; a
    /// second copy here would be a second thing to keep in step, and the unattended nightly passes
    /// iterate profile ids and hold no subject at all. The authorisation boundary is expressed in
    /// the repository's signatures rather than in this column - see
    /// <c>PipelineSettingsRepository</c>, and the named exception it makes for the passes.
    /// </remarks>
    public long ProfileId { get; set; }

    /// <summary>
    /// The profile this belongs to.
    /// </summary>
    /// <remarks>
    /// Present so the repository can express "the settings of the candidate holding this subject
    /// id" as one query with a join, rather than as a lookup followed by a read. That is its only
    /// purpose: this database is billed by wall-clock time online, so two wakeups where one would
    /// do is a real cost rather than a tidiness question. Nothing maps a settings row back to a
    /// person through it.
    /// </remarks>
    public CandidateProfileEntity? Profile { get; set; }

    // ---- Judgement. Read by the nightly match sweep. ----

    public int AssessmentsPerNight { get; set; }

    public int AssessmentThreshold { get; set; }

    public int RecentSharePercent { get; set; }

    public int RecentWindowDays { get; set; }

    // ---- Drafting. Read by the nightly application generation pass. ----

    public int DraftsPerNight { get; set; }

    public int DraftMinAssessmentScore { get; set; }

    /// <summary>
    /// Only draft for postings this recent, or null for every age.
    /// </summary>
    /// <remarks>
    /// <b>The one nullable value column, and null is not zero.</b> Null means there is no age
    /// bound at all, which is the behaviour the pass has always had; zero through
    /// <c>PostingAge.Cutoff</c> would mean "posted since this instant", which selects almost
    /// nothing and reads as the pass being broken. <c>PipelineSettingsValidation</c> refuses zero
    /// for exactly that reason, so a zero in this column could only have arrived from a writer
    /// that skipped validation - and it would then behave as the bound it literally is rather than
    /// as the absence somebody meant. A form's empty box has to reach this column as NULL.
    ///
    /// This is the rule the scraper config states as "an option nobody chose is omitted, never
    /// sent as null": did-not-choose and chose-nothing have to be different bytes. The spelling is
    /// inverted here because the destinations are - a merged parameter map there, where null would
    /// blank one of the scraper's own defaults, and a column here, where NULL is the only thing
    /// that can mean absence - but the distinction being preserved is the same one.
    /// </remarks>
    public int? DraftPostedWithinDays { get; set; }

    // ---- Sending. Read by the submission write path and by the fold over the event log. ----

    public int DailySendCap { get; set; }

    /// <summary>
    /// Silence for this many days makes an application stale.
    /// </summary>
    /// <remarks>
    /// <b>A day count and not a duration, and a column is where that has to hold.</b> The fold
    /// keeps its <c>TimeSpan</c> and the consumer builds one with <c>TimeSpan.FromDays</c>;
    /// storing a duration instead would put "14.00:00:00", "P14D" and 1209600000 within reach of
    /// one another, which is three contracts wearing one type. It is an <c>int</c> here for the
    /// same reason it is an <c>int</c> on the wire.
    ///
    /// <b>Storing the threshold does not store the answer.</b> Staleness stays derived - there is
    /// no stale column anywhere and this is not one - because storing the answer would mean a
    /// timer to keep it current, a race between that timer and a real event, and a row that is
    /// wrong in between. Changing this number re-reads the history rather than rewriting it, with
    /// nothing migrated and nothing to migrate, which is the property that made it safe to expose
    /// at all.
    /// </remarks>
    public int ChaseAfterDays { get; set; }

    /// <summary>When this candidate first saved settings of their own.</summary>
    /// <remarks>
    /// Kept because "the pipeline started behaving differently on the fourteenth" is a question
    /// somebody asks about a night that cost more or judged less, and these nine numbers are the
    /// likeliest answer to it. Two timestamps on a row this small cost nothing, and the
    /// alternative is inferring a settings change from its effects.
    /// </remarks>
    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>When they last saved them.</summary>
    public DateTimeOffset UpdatedUtc { get; set; }
}
