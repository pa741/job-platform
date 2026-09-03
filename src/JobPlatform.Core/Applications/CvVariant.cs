namespace JobPlatform.Core.Applications;

/// <summary>
/// The bounds a CV library is kept inside: how many variants a profile holds, and how long each
/// part of one may be.
/// </summary>
/// <remarks>
/// <b>One place, so the column width and the validation cannot drift.</b> This follows
/// <c>FormAnswerLimits</c> and <c>SubmissionLimits</c> exactly, and for the reason those exist: a
/// bound written once in a migration and again in a request validator is two numbers that agree
/// until somebody widens one, and the failure afterwards is a write the database refuses rather
/// than the API - which is a 500 on somebody's save with their typing lost, on a page whose whole
/// purpose is that they typed something.
///
/// <b>The numbers here bound a document a person wrote, so none of them truncates anything.</b>
/// Every value on <see cref="CvVariant"/> is either refused or stored whole. A CV cut off at a
/// character count is a CV that ends mid-sentence in front of an employer, which is the same
/// argument <c>FreeTextPrompt.MaxWords</c> makes about a drafted answer and a stronger one here,
/// because nothing regenerates this document afterwards.
/// </remarks>
public static class CvVariantLimits
{
    /// <summary>
    /// How many variants one profile may keep in use at a time.
    /// </summary>
    /// <remarks>
    /// <b>Six, and the spec's guess of eight is the number of CVs a person will happily create
    /// rather than the number they will keep current.</b> Those are different quantities and only
    /// the second one matters, because a library nobody maintains is worse than a single CV: a
    /// stale variant looks exactly like a current one in the picker, and the system will send it.
    ///
    /// <b>The maintenance cost is linear in the library and it arrives all at once.</b> One
    /// profile edit - a job added, a title corrected - puts every variant behind the profile on the
    /// same afternoon (see <see cref="CvVariant.PredatesProfileUpdate"/>). At eight, that is eight
    /// documents to reread in one sitting, and what actually happens is that two get updated and
    /// six quietly rot. Six is chosen as the number a person will still work through, not as a
    /// round number.
    ///
    /// <b>The ceiling on what is even useful is lower than it looks.</b> Selection scores a variant
    /// against a posting over the shared concept vocabulary and takes a winner only if it clears a
    /// floor <i>and</i> beats the runner-up by a margin. Two variants covering nearly the same
    /// concepts can therefore never separate: they abstain, and the candidate is told they are
    /// missing a CV they have in fact written twice. So a variant earns its place only by being
    /// about a distinguishable kind of work - and <c>RoleFamily</c> names thirteen kinds across the
    /// whole corpus, of which one person credibly applies across three or four. Six leaves room for
    /// one axis the selector cannot see, a second language most obviously, without reaching the
    /// point where variants start competing with each other.
    ///
    /// <b>It counts what is in use, never what is archived.</b> Archiving is not deletion here - an
    /// application made last year has to stay explicable - so a cap counting archived rows would
    /// mean the seventh rewrite of a CV is impossible until somebody erases the file an employer
    /// was actually sent. That trades an auditable history for a row count, and the history is the
    /// more expensive of the two. <see cref="CvVariantLibrary.HasRoomForAnother"/> is where that
    /// reading lives, so no caller has to remember it.
    ///
    /// Raising it is a one-line change and the cost is not measured in rows: it is measured in how
    /// many documents go quietly out of date the next time somebody edits their profile.
    /// </remarks>
    public const int MaxPerProfile = 6;

    /// <summary>
    /// The label a person picks a CV by - "Backend .NET", "AI and data platforms".
    /// </summary>
    /// <remarks>
    /// <b>Short because it is read at a glance and never sent anywhere.</b> Every application
    /// uploads the same filename whatever variant was chosen, so the label is seen by the
    /// candidate, by the dashboard, and by the pack when it says which CV it picked and why. It is
    /// not a document title and it is not a description; sixty characters is a phrase that fits on
    /// one line of a picker, which is the only place it has to work.
    /// </remarks>
    public const int MaxLabelLength = 60;

    /// <summary>
    /// The candidate's own markdown. The source of truth for both rendered formats.
    /// </summary>
    /// <remarks>
    /// <b>A product bound rather than a storage one, which inverts the argument
    /// <c>FormAnswerLimits.MaxValueLength</c> makes next door.</b> That one stops at 4,000 because
    /// it is the widest <c>nvarchar</c> SQL Server stores in row, and it says outright that
    /// anything longer is a document and documents are the CV path's business. This is that path,
    /// the column is <c>nvarchar(max)</c>, and so the number has to be justified by what a CV is
    /// rather than by what the database will hold.
    ///
    /// A two-page CV runs four to six thousand characters of markdown. Twenty thousand is roughly
    /// ten pages, past which the document has stopped being a CV and the one controlled template
    /// stops producing something a recruiter reads. What the bound is really there to refuse is a
    /// paste of something else entirely - a portfolio site, the text dump of a PDF - which is
    /// exactly what somebody does when they misread the box.
    ///
    /// <b>There is no minimum, and the absence is a decision.</b> A three-line CV is a bad CV
    /// rather than an invalid one, and this system has no business telling somebody their own
    /// document is too short. What it does refuse is a blank one - see
    /// <see cref="CvVariant.Create"/> - because a blank variant renders to a blank PDF, and a blank
    /// PDF is a file that can be uploaded to an employer.
    /// </remarks>
    public const int MaxMarkdownLength = 20_000;

    /// <summary>
    /// Where a rendered file is kept, as a path inside its container.
    /// </summary>
    /// <remarks>
    /// <b>Set at the storage platform's own ceiling rather than at what a path is expected to
    /// be</b>, for the reason <c>SubmissionLimits.MaxScreenshotRefLength</c> gives: this bounds a
    /// pointer, and truncating a pointer costs the thing pointed at - a rendered document that
    /// exists, was uploaded to an employer, and can never be found again, with nothing in the row
    /// admitting it. An Azure blob name is at most 1,024 characters, so a path too long for this
    /// column is a path the store would have refused anyway.
    ///
    /// A separate constant from the one on the submissions table even though the number is
    /// identical, the same way <c>SubmissionLimits.MaxFinalUrlLength</c> is separate from
    /// <c>MaxApplyUrlLength</c>: two columns that happen to agree today are not one decision, and
    /// one constant behind both means widening either silently widens the other.
    /// </remarks>
    public const int MaxBlobPathLength = 1024;

    /// <summary>
    /// Exactly the width of a hex SHA-256, so <c>char(64)</c> and the validation are one decision.
    /// </summary>
    /// <remarks>
    /// The same construction as <c>FormAnswerLimits.QuestionHashLength</c>. The hash answers "what
    /// exactly did we send them" about a file already sitting in somebody else's system, so a
    /// column that could hold something other than a whole digest would answer that question wrongly
    /// rather than not at all.
    /// </remarks>
    public const int Sha256Length = 64;
}

/// <summary>
/// One CV the candidate wrote, kept as markdown and rendered to the two formats an ATS asks for.
/// </summary>
/// <remarks>
/// <b>This type exists because of a sentence.</b> On the first real generation run the writer was
/// asked what else an employer should know, answered with the candidate's citizenship - correctly,
/// out of their own summary - and then added "I am an AI and they should have seen this." It was
/// stored, served through the pack, and would have been typed into a real employer's form under a
/// person's name. <c>DraftedAnswerCatalog.IsCandidateVoice</c> now drops that class of sentence,
/// and a guard is a net under a trapeze. This is the other half: <b>a curated CV takes the model
/// out of the document that matters most</b>, because a model that is not writing the CV cannot
/// invent a claim in it. The prose in <see cref="Markdown"/> is the candidate's, always, and
/// nothing in this system rewrites it.
///
/// <b>There are no concepts on this record, and the absence is the guard rather than an
/// omission.</b> A variant's concepts are extracted - that is how selection works at all - and they
/// feed selection <i>only</i>. They must never reach <c>ProfileConcepts</c>, never move a match
/// score, and never widen what the candidate is judged to have. A CV is written <i>from</i> the
/// profile; letting it back in would let a document inflate the record it came from, and the loop
/// would then apply to jobs on the strength of its own prose. Keeping the list off the row makes
/// that a property of the type instead of a rule somebody has to remember: the Data layer joins
/// <c>CvVariantConcepts</c> when a selector asks for them, the selector takes them as an argument,
/// and there is no field here for a caller to hand to a profile writer by mistake.
///
/// <b>No profile id, though the table carries one.</b> Every one of these is materialised by a read
/// already scoped to a candidate - the rule <c>CandidateProfileRepository</c> states as a type by
/// taking a subject id and never a profile id, and <c>FormAnswer</c> follows for the same reason. A
/// field here would be a second copy of a fact the query has already established, and the failure
/// it invites is a caller reading the owner off the row it is deciding whether to disclose.
///
/// <b>The markdown is the record and the rendered files are stored, which is the opposite of what
/// <c>ApplicationDocuments</c> does, deliberately.</b> A per-posting document is rendered per
/// request so that a template fix reaches documents already generated. A library variant is
/// rendered once and its bytes hashed, because the file was uploaded into somebody else's system:
/// "what exactly did we send them" is a question about a file that has to still exist and still be
/// the same file. A template improvement reaches a variant by re-rendering it, which is free and
/// deterministic, rather than by silently changing what a stored hash describes.
///
/// <b>Archived, never deleted.</b> <see cref="IsArchived"/> removes a variant from selection and
/// from nothing else: the paths, the hash and the markdown stay exactly where they were, because an
/// application made last year has to stay explicable and the submission that records it names this
/// variant by id. Nothing in this file reads <see cref="IsArchived"/> to decide whether a variant
/// may be <i>read</i>.
/// </remarks>
public sealed record CvVariant
{
    /// <summary>The row. Zero until it is written, because identity is the database's to assign.</summary>
    /// <remarks>
    /// <b>This, and not the label, is what a submission records.</b> Labels are reused - a rewritten
    /// "Backend .NET" archives the old one and takes its name back - so a history keyed on the label
    /// would say two different documents were the same CV, which is precisely the question outcome
    /// feedback exists to answer.
    /// </remarks>
    public long Id { get; init; }

    /// <summary>What the person calls this CV. The handle they pick it by.</summary>
    /// <remarks>
    /// <b>It has to be distinguishable at a glance, which is a stronger requirement than being
    /// unique.</b> Two variants called "Backend" and "backend " are unique as strings and identical
    /// to a reader, so uniqueness is enforced over the folded form -
    /// <see cref="CvVariantLibrary.FoldLabel"/> - and only among the variants still in use. The
    /// argument for enforcing it at all is not tidiness: the pack reports which variant it chose and
    /// why, in the label's words, and an explanation naming something ambiguous cannot be checked by
    /// the person reading it. A duplicate label is also, nearly always, a second copy of a CV
    /// somebody meant to edit - refusing it surfaces that while it is still cheap to fix.
    /// </remarks>
    public required string Label { get; init; }

    /// <summary>The candidate's own words. The source both rendered formats are made from.</summary>
    public required string Markdown { get; init; }

    /// <summary>
    /// When <see cref="Markdown"/> last became what it is now.
    /// </summary>
    /// <remarks>
    /// <b>It moves on an edit to the words and on nothing else.</b> Renaming a variant or archiving
    /// it must leave this alone, or a rename would make a document that has not changed look freshly
    /// written - and this timestamp is the whole of the staleness signal, compared against the
    /// profile's own <c>UpdatedUtc</c>. <see cref="WithMarkdown"/> is the transition that moves the
    /// two together, so no writer has to remember the pairing.
    /// </remarks>
    public required DateTimeOffset AuthoredAtUtc { get; init; }

    /// <summary>When the stored files were produced, or null while the variant is still only text.</summary>
    public DateTimeOffset? RenderedAtUtc { get; init; }

    /// <summary>The PDF, as a path inside its container. Null until it is rendered.</summary>
    public string? PdfBlobPath { get; init; }

    /// <summary>The DOCX, which several large ATS vendors parse more reliably than a PDF.</summary>
    public string? DocxBlobPath { get; init; }

    /// <summary>Over the rendered bytes, so "what did we send them" is answerable exactly.</summary>
    public string? Sha256 { get; init; }

    /// <summary>Retired from selection, and from nothing else.</summary>
    public bool IsArchived { get; init; }

    /// <summary>The form the uniqueness rule is written over. Case and whitespace folded away.</summary>
    /// <remarks>
    /// Derived rather than stored, because nothing here is keyed on it: the row is found by
    /// <see cref="Id"/>. If the Data layer persists it to hang a unique index off, it must come from
    /// <see cref="CvVariantLibrary.FoldLabel"/> and from nowhere else - the single-writer rule
    /// <c>JobFingerprint.CrossBoardKeyHash</c> already lives under, because a second spelling of a
    /// fold splits one identity in two with nothing failing.
    /// </remarks>
    public string LabelKey => CvVariantLibrary.FoldLabel(Label);

    /// <summary>Whether a rendered PDF exists at all.</summary>
    /// <remarks>
    /// The DOCX is deliberately not part of this. A variant with only a PDF can still be uploaded to
    /// most forms, so treating it as unsendable would refuse an application over a format that
    /// particular employer never asked for; a variant with no PDF cannot be uploaded anywhere. A
    /// caller that specifically needs the DOCX - a Workday form - reads <see cref="DocxBlobPath"/>
    /// and decides for itself.
    /// </remarks>
    public bool IsRendered => RenderedAtUtc is not null && !string.IsNullOrWhiteSpace(PdfBlobPath);

    /// <summary>
    /// Whether the stored files are known to be the current text rendered.
    /// </summary>
    /// <remarks>
    /// <b>An edit invalidates a render by arithmetic rather than by a flag.</b>
    /// <see cref="WithMarkdown"/> moves <see cref="AuthoredAtUtc"/> past <see cref="RenderedAtUtc"/>,
    /// this goes false, and the variant leaves selection until the renderer stamps a new time. A
    /// boolean would have to be cleared by every writer that touches the markdown, and the one that
    /// forgets is the one that uploads last week's paragraph.
    ///
    /// <b>A file rendered <i>before</i> the text it came from is the clock problem, and this reads
    /// it as "not current" on purpose.</b> The two timestamps are written by different processes - a
    /// save in the API, a render in a container - so an inversion is usually two hosts disagreeing
    /// by seconds rather than a real reordering, and nothing here can tell which it is. What decides
    /// the reading is the cost of each mistake. Treating it as current sends an employer a PDF of a
    /// paragraph the candidate may have deleted, under their name, which is the exact class of
    /// failure this whole design exists to remove and is not recoverable once the form is submitted.
    /// Treating it as stale costs one re-render, which is deterministic, needs no model call, and
    /// leaves the previous file in place meanwhile.
    ///
    /// <b>No skew tolerance, deliberately.</b> A window wide enough to absorb an unknown clock
    /// difference is a window wide enough to admit the case it was added to exclude, and the honest
    /// fix for a persistent inversion is one clock rather than a wider window. It is visible rather
    /// than silent, too: the dashboard can say "this needs rendering again", where a tolerance would
    /// say nothing and occasionally be wrong.
    ///
    /// Compared as instants, so a render stamped in one offset and an edit in another still order
    /// correctly.
    /// </remarks>
    public bool IsRenderCurrent
        => RenderedAtUtc is { } rendered
            && !string.IsNullOrWhiteSpace(PdfBlobPath)
            && rendered >= AuthoredAtUtc;

    /// <summary>
    /// Whether this variant may be chosen for an application being made now.
    /// </summary>
    /// <remarks>
    /// <b>This governs selection and nothing else.</b> It is never a permission check on reading a
    /// variant, fetching its files or explaining an application that has already been made -
    /// archiving must not make a sent file unexplainable, and a system that answers "what did you
    /// send them" with "that CV is archived" has lost the record it exists to keep.
    ///
    /// <b>Being out of date is not part of it.</b> A variant that predates the last profile change
    /// is still sendable, and that is the whole difference between a nudge and a lockout: somebody
    /// who adds a job to their profile at lunchtime would otherwise find every CV excluded, every
    /// posting parked for want of one, and a dashboard telling them they have no CVs when they have
    /// six good ones and a line to add to them. Staleness is reported by
    /// <see cref="CvVariantLibrary.Staleness"/> and acted on by a person.
    ///
    /// <b>Unrendered is part of it, and that is where the failure would otherwise land.</b> A
    /// variant with no current PDF has no URL for the pack to hand over, so choosing it produces a
    /// pack whose file the browser loop discovers is missing at the upload box - after the tab is
    /// open, which is the same late-discovery failure <c>SubmissionQuota</c> exists to prevent on
    /// the other side of the same loop. Better that it never enters the choice, and that the
    /// candidate is told a CV needs rendering.
    /// </remarks>
    public bool IsSendable => !IsArchived && IsRenderCurrent;

    /// <summary>
    /// Whether this variant was written before a given profile change.
    /// </summary>
    /// <remarks>
    /// <b>The whole of the staleness signal, and it reports rather than acts.</b> Nothing in this
    /// system may respond to a true here by rewriting the document: regenerating the candidate's CV
    /// with a model is precisely the thing this change removes, and doing it on a timer would put
    /// the model back into the one document it was taken out of, unattended. The correct response is
    /// a sentence on the page the variants live on. It is the same class of problem as the apply
    /// loop's answer TTL - a notice period goes off, and so does a CV - and it has the same answer:
    /// tell the person, and let them decide whether the change is one their CV needed to mention.
    ///
    /// <b>A profile that has never recorded an update makes nothing stale.</b> A null
    /// <c>UpdatedUtc</c> is a profile nothing has written since the column existed, not a profile
    /// changed at the beginning of time, and reading absence as "changed" would open the dashboard
    /// with every CV flagged on the day the feature ships.
    ///
    /// <b>Equal instants are not stale.</b> A variant authored in the same save that stamped the
    /// profile is not behind it, and the strict comparison keeps a boundary case from producing a
    /// nudge nobody can act on - there is nothing to rewrite when the two describe one moment.
    /// </remarks>
    /// <param name="profileUpdatedUtc">The profile's <c>UpdatedUtc</c>, or null where it has none.</param>
    public bool PredatesProfileUpdate(DateTimeOffset? profileUpdatedUtc)
        => profileUpdatedUtc is { } updated && AuthoredAtUtc < updated;

    /// <summary>
    /// Replaces the words, and dates them.
    /// </summary>
    /// <remarks>
    /// <b>The pairing is the point.</b> Markdown and <see cref="AuthoredAtUtc"/> moving together is
    /// what makes <see cref="IsRenderCurrent"/> answer honestly, and a writer free to set one
    /// without the other is a writer that can leave last week's PDF looking current. Everything else
    /// about a variant - its label, its archived flag - is an ordinary <c>with</c>, precisely
    /// because those must <i>not</i> disturb the date.
    ///
    /// <b>The blob paths survive an edit rather than being cleared.</b> The stored file is still the
    /// file a previous application uploaded, and blanking the pointer would make that application
    /// unexplainable in order to keep a row tidy. The variant leaves selection through the
    /// timestamps instead, which is reversible by re-rendering and destroys nothing.
    ///
    /// Nothing here refuses a timestamp earlier than the one it replaces. Two hosts disagreeing by a
    /// second is not a reason to lose somebody's typing - the argument <c>ScraperConfigPublisher</c>
    /// makes about a failed publish - and the consequence of an out-of-order authoring time is a
    /// render that looks current one save early, which the next render corrects.
    /// </remarks>
    /// <param name="markdown">The new text. Trimmed at the ends, never in the middle, and never truncated.</param>
    /// <param name="authoredAtUtc">When it was written.</param>
    public CvVariant WithMarkdown(string markdown, DateTimeOffset authoredAtUtc)
        => this with { Markdown = ValidMarkdown(markdown), AuthoredAtUtc = authoredAtUtc };

    /// <summary>
    /// A new variant, from what a person typed.
    /// </summary>
    /// <remarks>
    /// <b>The only constructor that validates, for the reason <c>AiCallRecord.Create</c> is the only
    /// constructor there</b>: bounds enforced at the call sites survive exactly until somebody adds
    /// another call site. Rehydrating a stored row uses the object initialiser instead and keeps
    /// whatever is on disk, which is the split <c>FormAnswer</c> already draws - history that no
    /// longer satisfies a bound is still history, and a row written by an older build must not
    /// become unreadable because a number here moved.
    ///
    /// <b>It refuses rather than trimming, and refuses rather than truncating.</b> A blank CV renders
    /// to a blank PDF that can be uploaded to an employer; an over-long one is somebody having
    /// pasted the wrong thing entirely. Neither is improved by being quietly fixed up, and a CV cut
    /// at twenty thousand characters would end mid-sentence in front of a recruiter.
    ///
    /// <b>Uniqueness is not checked here, because a row does not know its library.</b>
    /// <see cref="CvVariantLibrary.IsLabelAvailable"/> is that rule and the Data layer's filtered
    /// unique index is its enforcement; this refuses only a label nobody could pick a CV by.
    /// </remarks>
    /// <param name="label">What the person calls it.</param>
    /// <param name="markdown">Their CV.</param>
    /// <param name="authoredAtUtc">When they wrote it.</param>
    public static CvVariant Create(string label, string markdown, DateTimeOffset authoredAtUtc)
        => new()
        {
            Label = ValidLabel(label),
            Markdown = ValidMarkdown(markdown),
            AuthoredAtUtc = authoredAtUtc,
        };

    private static string ValidLabel(string label)
    {
        ArgumentNullException.ThrowIfNull(label);

        var trimmed = label.Trim();

        if (trimmed.Length > CvVariantLimits.MaxLabelLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(label),
                $"A label is at most {CvVariantLimits.MaxLabelLength} characters.");
        }

        return CvVariantLibrary.IsUsableLabel(trimmed)
            ? trimmed
            : throw new ArgumentException(
                "A label has to be something a person can pick a CV by.", nameof(label));
    }

    private static string ValidMarkdown(string markdown)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markdown);

        var trimmed = markdown.Trim();

        return trimmed.Length <= CvVariantLimits.MaxMarkdownLength
            ? trimmed
            : throw new ArgumentOutOfRangeException(
                nameof(markdown),
                $"A CV is at most {CvVariantLimits.MaxMarkdownLength} characters of markdown.");
    }
}

/// <summary>
/// How much of a library has fallen behind the profile it was written from.
/// </summary>
/// <remarks>
/// <b>Counts, not a sentence and not an instruction.</b> The page says "three of your CVs predate
/// your last profile change", and the pluralisation belongs to whatever renders it; what belongs
/// here is the arithmetic, pure and assertable, for the reason <c>RunSummary</c> and
/// <c>SubmissionQuota</c> are pure - the numbers can then be checked exactly rather than through a
/// database.
///
/// <b>There is no field here for what to do about it, and that is deliberate.</b> No "regenerate",
/// no "suggested action", nothing a scheduled pass could pick up and act on. Rewriting the
/// candidate's document with a model is the thing this change removes, and a summary carrying a verb
/// is how that comes back six months later as a helpful automation.
///
/// <b>One nudge, aggregated, rather than one per variant.</b> The same argument the gap brief makes
/// about parked postings: fifty individual notices is a queue nobody reads, and one line naming a
/// number is something a person acts on in an afternoon.
/// </remarks>
/// <param name="Considered">
/// How many variants were counted. <b>Archived ones never reach it</b> - a retired CV falling behind
/// the profile is not a thing to nudge about, and counting them would let a library of two live CVs
/// and seven archived ones report nine.
/// </param>
/// <param name="Stale">How many of those were written before <paramref name="ProfileUpdatedUtc"/>.</param>
/// <param name="ProfileUpdatedUtc">
/// What they were compared against, carried so the answer can be read without knowing what was
/// passed in - and so that "nothing is out of date" can be told apart from "the profile has never
/// been updated", which are the same zero for opposite reasons.
/// </param>
public sealed record CvVariantStaleness(int Considered, int Stale, DateTimeOffset? ProfileUpdatedUtc)
{
    /// <summary>A library with nothing in it to be out of date.</summary>
    /// <remarks>
    /// <b>An empty library is not a problem this type reports.</b> Somebody with no CVs is told so
    /// by the gap brief, which knows how many postings are waiting on one; a staleness notice saying
    /// "none of your zero CVs are out of date" is noise on the page of somebody who has not started.
    /// </remarks>
    public static CvVariantStaleness None { get; } = new(0, 0, null);

    /// <summary>How many are still level with the profile.</summary>
    public int Current => Considered - Stale;

    /// <summary>Whether there is anything to say at all, asked rather than left to arithmetic.</summary>
    public bool AnyStale => Stale > 0;
}

/// <summary>
/// The rules about a library of variants, as pure functions, so its readers cannot disagree.
/// </summary>
/// <remarks>
/// <b>The same construction as <c>ParkReasonPolicy</c>, and for the same reason.</b> Three readers
/// ask these questions - the pack when it selects, the dashboard when it draws the page, and the
/// repository when it decides whether a save is allowed - and a rule spelled out three times drifts
/// invisibly, because a variant missing from a list is not something anybody notices.
///
/// <b>Pure, and free of every Azure type</b>, like <c>MatchScorer</c> and <c>SubmissionState</c>:
/// the counting a repository does is a query, and this is only what the answers mean.
/// </remarks>
public static class CvVariantLibrary
{
    /// <summary>
    /// The variants a selection pass may choose between.
    /// </summary>
    /// <remarks>
    /// <b>One definition, so the selector and the pack cannot disagree about what is on the
    /// table.</b> Anything excluded here is excluded from every path that leads to an employer, and
    /// the exclusions are exactly two: archived, and not currently rendered. Neither is about how
    /// good the CV is - that is the selector's arithmetic - and neither is staleness, which stays in
    /// the candidate's hands.
    ///
    /// <b>An empty answer is an ordinary result and not a failure.</b> It is what a library of
    /// archived variants gives, and what a library of unrendered ones gives, and the correct
    /// response to it is the abstention the design already specifies: park the posting, and tell the
    /// candidate what they are missing. Sending the nearest CV to a job it does not fit is the
    /// failure this whole feature replaces, and it is invisible - the application simply never comes
    /// back.
    ///
    /// The caller's order is preserved rather than sorted. A sort here would look like a ranking,
    /// and ranking is the selector's job.
    /// </remarks>
    public static IReadOnlyList<CvVariant> Selectable(IEnumerable<CvVariant> library)
    {
        ArgumentNullException.ThrowIfNull(library);

        return [.. library.Where(variant => variant is not null && variant.IsSendable)];
    }

    /// <summary>How many variants are in use, which is what the cap counts.</summary>
    public static int ActiveCount(IEnumerable<CvVariant> library)
    {
        ArgumentNullException.ThrowIfNull(library);

        return library.Count(variant => variant is not null && !variant.IsArchived);
    }

    /// <summary>
    /// Whether another variant may be written.
    /// </summary>
    /// <remarks>
    /// <b>Archived variants do not occupy the cap</b>, for the reason
    /// <see cref="CvVariantLimits.MaxPerProfile"/> gives: they are kept forever so that past
    /// applications stay explicable, and a cap counting them would make the seventh rewrite
    /// impossible until somebody erased a file an employer was sent.
    ///
    /// A library already over the cap answers no rather than throwing. That state is reachable
    /// without anybody doing anything wrong - lowering the constant leaves every library above it
    /// above it - and it is the same shape as <c>SubmissionQuota.Remaining</c> being floored at
    /// zero: the answer is "no room", never a negative number or an exception on a page that is only
    /// being drawn.
    /// </remarks>
    public static bool HasRoomForAnother(IEnumerable<CvVariant> library)
        => ActiveCount(library) < CvVariantLimits.MaxPerProfile;

    /// <summary>
    /// How many of a library predate a profile change.
    /// </summary>
    /// <remarks>
    /// <b>The counting lives here rather than at the call site</b>, the way <c>RunSummary.From</c>
    /// tallies its own parks: a caller that counts its own stale variants has written the rule twice,
    /// and the copy on the dashboard is the one that quietly stops agreeing with the copy that marks
    /// each row.
    ///
    /// Each row is marked by <see cref="CvVariant.PredatesProfileUpdate"/> and the summary counts
    /// with that same call, so the sentence at the top of the page and the badges beside the
    /// variants cannot say different things.
    /// </remarks>
    /// <param name="library">Every variant the profile holds, archived ones included.</param>
    /// <param name="profileUpdatedUtc">The profile's <c>UpdatedUtc</c>, or null where it has none.</param>
    public static CvVariantStaleness Staleness(
        IEnumerable<CvVariant> library,
        DateTimeOffset? profileUpdatedUtc)
    {
        ArgumentNullException.ThrowIfNull(library);

        var considered = 0;
        var stale = 0;

        foreach (var variant in library)
        {
            if (variant is null || variant.IsArchived)
            {
                continue;
            }

            considered++;

            if (variant.PredatesProfileUpdate(profileUpdatedUtc))
            {
                stale++;
            }
        }

        return new CvVariantStaleness(considered, stale, profileUpdatedUtc);
    }

    /// <summary>
    /// Whether a label is one somebody could pick a CV by.
    /// </summary>
    /// <remarks>
    /// Blank fails, over-long fails, and so does a label with no letter or digit anywhere in it.
    /// That last one is not pedantry: a variant called "-" or "..." is indistinguishable from its
    /// neighbours in a picker and unquotable in the pack's account of why it chose that CV, which is
    /// the one thing a label has to be able to do.
    /// </remarks>
    public static bool IsUsableLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return false;
        }

        var trimmed = label.Trim();

        return trimmed.Length <= CvVariantLimits.MaxLabelLength && trimmed.Any(char.IsLetterOrDigit);
    }

    /// <summary>
    /// The comparison form of a label: trimmed, inner whitespace collapsed, case folded.
    /// </summary>
    /// <remarks>
    /// <b>It folds what is invisible and nothing else.</b> "Backend .NET" and "backend  .net" are
    /// the same label to anybody looking at a list, so they must not both exist; "Backend .NET" and
    /// "Backend" are different labels somebody meant to keep apart, and a fold that stripped
    /// punctuation would refuse the second on account of the first. Case and whitespace are exactly
    /// the differences a reader cannot see.
    ///
    /// <b>The only place this fold is written</b>, for the reason
    /// <c>JobFingerprint.CrossBoardKeyHash</c> is the only place its hash is written: a second
    /// spelling - another normaliser, another notion of whitespace - splits one label into two with
    /// nothing failing and no count to compare against. If the Data layer stores a key column to
    /// index, it stores this.
    /// </remarks>
    public static string FoldLabel(string? label)
        => string.IsNullOrWhiteSpace(label)
            ? string.Empty
            : string.Join(' ', label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .ToLowerInvariant();

    /// <summary>
    /// Whether a label may be used, given what the profile already keeps.
    /// </summary>
    /// <remarks>
    /// <b>Unique among the variants in use, and deliberately not among the archived ones.</b>
    /// Rewriting a CV and giving the new one the old one's name is the ordinary case - "Backend
    /// .NET" superseded by a better "Backend .NET" - and a rule reserving a label forever would push
    /// people into naming their CVs "Backend .NET v3" to get around a constraint meant to help them.
    /// History does not suffer for it, because a submission records the variant's
    /// <see cref="CvVariant.Id"/>: the label is the live handle, the id is the record.
    ///
    /// <paramref name="excludingId"/> is what lets a variant keep its own name through a rename -
    /// without it, saving "Backend .NET" over "Backend .NET" collides with itself, which reads to
    /// the person as the system refusing a change they did not make.
    /// </remarks>
    /// <param name="library">Every variant the profile holds, archived ones included.</param>
    /// <param name="label">The label being asked about.</param>
    /// <param name="excludingId">The variant being renamed, where there is one.</param>
    public static bool IsLabelAvailable(
        IEnumerable<CvVariant> library,
        string? label,
        long? excludingId = null)
    {
        ArgumentNullException.ThrowIfNull(library);

        if (!IsUsableLabel(label))
        {
            return false;
        }

        var key = FoldLabel(label);

        return !library.Any(variant => variant is not null
            && !variant.IsArchived
            && variant.Id != excludingId
            && string.Equals(variant.LabelKey, key, StringComparison.Ordinal));
    }
}
