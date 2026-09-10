namespace JobPlatform.Core.Submissions;

/// <summary>
/// Why an application was parked instead of made.
/// </summary>
/// <remarks>
/// <b>Parking is an attribute on the submission and never a member of
/// <see cref="SubmissionEventType"/>.</b> That is the decision in this file most likely to be
/// re-litigated, because adding a <c>Blocked</c> member looks like the smaller change: the event
/// log is already the record, and "we could not apply" is plainly a thing that happened. It does
/// not work, and the reason is mechanical rather than a matter of taste.
///
/// <see cref="SubmissionState.Fold"/> has exactly two rules and a new member has to survive
/// both: a terminal event wins outright, and otherwise the furthest-advanced phase wins, which
/// is <c>max(Type)</c> over the log. There is no numbering that lets <c>Blocked</c> through.
/// <b>Placed above <see cref="SubmissionEventType.OfferReceived"/></b> it is what <c>max</c>
/// picks, so: park on a captcha, the candidate finishes the application by hand, the offer
/// arrives - and the next run, which reads the parked columns rather than the events and so
/// still sees a parked row, appends a second <c>Blocked</c> and reports that offer as blocked.
/// That is the backwards walk
/// <c>The_furthest_advanced_phase_wins_rather_than_the_most_recent_event</c> exists to catch.
/// <b>Made terminal</b> it wins outright, so one <c>Blocked</c> closes an application that was
/// actually sent - the automated "thanks for applying" after a rejection, pointing the other way
/// - and <c>IsClosed</c> then makes it permanently un-stale, so the row that most needs chasing
/// is the one that stops being flagged. <b>Placed below
/// <see cref="SubmissionEventType.Submitted"/></b> there is nowhere to put it, zero being
/// unavailable, and the row would still carry a non-null <c>Phase</c>, which is what the
/// dashboard counts as sent.
///
/// None of those numberings work because the ladder is a total order over <i>how far the
/// application got</i>, and parking is not a point on it. It says no attempt was made at all,
/// which is a fact about this system's afternoon rather than about the employer's process.
///
/// <b>And parking is reversible, where an event is not.</b> <c>UnparkedAtUtc</c> lets a posting
/// back into the queue; the log has no eraser and the fold cannot un-see an event, so undoing a
/// <c>Blocked</c> would need "the most recent event wins" for that one member - precisely the
/// rule the fold was written to refuse.
///
/// So the fold is untouched, <see cref="SubmissionEventType"/> does not grow, and the fact lives
/// on <c>Submissions</c> as <c>ParkedReason</c> / <c>ParkedAtUtc</c> / <c>UnparkedAtUtc</c>,
/// where the queue predicate reads it through <see cref="ParkReasonPolicy"/> and
/// <c>list_submissions</c> projects it. <b>A parked row is not a sent one</b>, and every reader
/// that counts submissions has to be taught that: the dashboard counts any row with a non-null
/// phase, and a parked row must not land in that total.
///
/// <b>No member is zero.</b> <c>ParkedReason</c> is nullable and null already means "not
/// parked", so a zero member would be a second spelling of that absence and would let
/// <c>default(ParkReason)</c> reach the column reading as a real reason. Where zero <i>is</i> a
/// member here - <see cref="SubmissionChannel.Unknown"/> - it is because nothing had established
/// the fact and the default is the honest answer. This is the opposite case: a row is parked
/// because something decided to park it, and there is no such thing as parked for no reason.
/// </remarks>
public enum ParkReason
{
    /// <summary>The vacancy is gone - a 404, or a page saying it is no longer accepting applications.</summary>
    /// <remarks>
    /// Permanent, and it costs nothing to be: a closed vacancy does not reopen at the same URL,
    /// and an employer who re-advertises arrives as a new posting with an id the queue has never
    /// seen. "This one never returns" is therefore not "this job is never offered again".
    /// </remarks>
    Expired = 1,

    /// <summary>The same job has already been applied to, usually through another board.</summary>
    /// <remarks>
    /// Permanent for a stronger reason than <see cref="Expired"/>: applying twice to one vacancy
    /// is worse than not applying at all, and the recruiter sees both. What makes "the same job"
    /// answerable is the cross-board cluster - <c>JobFingerprint.CrossBoardKey</c>, title,
    /// employer and city folded together - rather than the posting id, which is per board by
    /// construction and so can never say that two rows are one vacancy.
    /// </remarks>
    Duplicate = 2,

    /// <summary>The employer's system wants a signed-in session and there is none.</summary>
    /// <remarks>
    /// A fact about the attempt rather than about the vacancy, so it returns next run. Kept
    /// apart from <see cref="AccountRequired"/> even though the retry answer is identical,
    /// because the policy is not the only reader: the two ask a person for different things - a
    /// sign-in to an account that exists, or the creation of one - and merging them would tidy
    /// this enum at the cost of the message somebody has to act on.
    /// </remarks>
    LoginRequired = 3,

    /// <summary>A human challenge stands in the way.</summary>
    /// <remarks>
    /// Session-shaped like <see cref="LoginRequired"/>, and returns for the same reason: a
    /// challenge served once is not a property of the posting, and the same URL is frequently
    /// clean on a later attempt. Nothing here tries to defeat one - the park <i>is</i> the
    /// handling.
    /// </remarks>
    Captcha = 4,

    /// <summary>The employer's system will not take an application until an account is created.</summary>
    /// <remarks>
    /// Retryable because it stops being true the moment somebody creates the account, and
    /// nothing in this system is told when that happened - so asking again next run is the only
    /// way to find out, and it costs a page load. The account is the candidate's to create, for
    /// the reason <c>FormFieldCatalog</c> gives about fields an agent cannot fill.
    /// </remarks>
    AccountRequired = 5,

    /// <summary>The form asked something this system cannot answer.</summary>
    /// <remarks>
    /// <b>The park that exists so abstention has somewhere to go.</b> Resolution refuses by
    /// default - below the confidence floor, on a sensitive field with no stored answer, or
    /// where an option set will not map cleanly - because a confident near-miss on somebody's
    /// application is worse than an interruption. An interruption still has to be recoverable,
    /// so the question is raised as an <c>OpenQuestion</c> and the posting comes back when it is
    /// answered rather than on the next run: offering it again unanswered produces the same park
    /// every run, forever. Hence <see cref="ParkRequeue.WhenAnswered"/>, which is the only
    /// reason this classification is three-valued rather than a bool.
    /// </remarks>
    MissingAnswer = 6,

    /// <summary>The form itself refused - a validation failure, a broken step, a submit that did not take.</summary>
    /// <remarks>
    /// The catch-all, and deliberately retryable rather than permanent. Most of what lands here
    /// is transient; what is not parks again next run and costs a page load, where reading it as
    /// permanent would drop a live vacancy on the strength of a rendering bug. When this becomes
    /// the most common reason in the table it is a signal to read the notes on those rows, not
    /// to change this classification.
    /// </remarks>
    FormError = 7,

    /// <summary>The candidate's daily send cap is spent for the day.</summary>
    /// <remarks>
    /// The one reason that is about this system rather than about the posting, and therefore the
    /// one that must never be permanent: the cap resets at midnight UTC and the vacancy was
    /// never the problem. Parking on it is also how the cap becomes <i>visible</i> - a loop that
    /// simply stops at its last allowed application leaves nothing behind saying why, and a cap
    /// nobody can see is one somebody eventually removes as a mystery.
    ///
    /// <b>The cap is <c>PipelineSettings.DailySendCap</c>;
    /// <see cref="SubmissionLimits.MaxSubmittedPerDay"/> is what that setting defaults to and is
    /// no longer the whole answer.</b> It is nought to a hundred and per candidate, so twenty-five
    /// is the number an unconfigured candidate is refused at rather than the number this reason
    /// means - and a run reading "the twenty-fifth application" would be reading one deployment's
    /// default as the rule. <b>Where it is enforced did not move with it</b>:
    /// <c>SubmissionRepository</c> counts <c>Submitted</c> events by their own <c>AtUtc</c> and
    /// refuses there, and this park is what a run leaves behind when it is refused.
    /// </remarks>
    OutOfQuota = 8,

    /// <summary>No CV variant the candidate has written covers what this posting asks for.</summary>
    /// <remarks>
    /// <b>The second park written so that an abstention has somewhere to go</b>, and the first
    /// about a document rather than about an attempt. Selection scores every variant against the
    /// posting and takes a winner only where one clears a floor and beats the runner-up; where
    /// none does, sending the nearest CV is precisely the failure a curated library replaces, and
    /// it is invisible - a badly aimed application does not come back to say so. So the pass
    /// declines, the run continues, and what the posting asked for that nothing covers is
    /// reported instead.
    ///
    /// <b>Not <see cref="ParkRequeue.NextRun"/>, and that is the classification to get wrong here,
    /// because "write another CV" sounds like the sort of thing that happens between runs.</b> The
    /// gap is a pure function of the posting's requirements and the library, and a run changes
    /// neither: the next pass meets the same posting, scores the same variants, computes the same
    /// gap and parks it again, having spent a page load to learn what the last run already knew.
    /// That is a loop and not a retry - the same one <see cref="MissingAnswer"/> had, ended by
    /// giving it a conditional class of its own and a clause of its own in the queue predicate -
    /// and it is worse here, because one library serves the whole queue: a single missing CV parks
    /// every posting that wanted it, on every run, for ever.
    ///
    /// <b>Not <see cref="ParkRequeue.Never"/> either, and permanence would be the more expensive
    /// mistake.</b> The two permanent reasons are statements about the vacancy - it is gone, or it
    /// has already been applied to. This is a statement about the candidate's library, which is
    /// the one thing named in this enum that they can change on a Saturday afternoon. Made
    /// permanent, the report of what is missing becomes a list of postings that cannot come back,
    /// so acting on it would buy nothing - and that report is the entire reason for preferring an
    /// abstention to a near miss.
    ///
    /// <b>The condition is coverage and not authorship.</b>
    /// <see cref="ParkRequeue.WhenCovered"/> waits on a variant that covers <i>this</i> posting's
    /// gap, never on the library having grown: releasing a Kubernetes-shaped posting because a
    /// second backend CV was written brings the loop back at a longer period - once per authoring
    /// event rather than once per run - and hands the bill to the person who has just done the
    /// work.
    ///
    /// <b>It raises no question, deliberately, which is also why it cannot borrow
    /// <see cref="ParkRequeue.WhenAnswered"/>.</b> Fifty individual "could not apply" notices is a
    /// queue nobody reads; the answer is one standing view ranked by how many postings each
    /// missing CV blocks, which is a set difference over the shared vocabulary rather than a
    /// question put to anybody. The mechanical consequence is what settles the classification: a
    /// park that waits on an answer names the question it waits on, and the queue's fallback for a
    /// park naming none holds the posting only while some <i>other</i> advert's question is
    /// outstanding. Filed under that class this park names nothing, so the posting would be
    /// released the moment the candidate's unrelated question queue drained - with no variant
    /// written, nothing about it changed, and the loop back in a form harder to see than the
    /// original, because it turns on an answer given to a different advert.
    ///
    /// <b>And this is the reason that most needs parking to be an attribute rather than an
    /// event</b>, in the terms this file opens with. The report is a query over the parked rows -
    /// which postings wait on which missing CV, and how many share a gap - which a <c>Blocked</c>
    /// event could answer only by folding every posting's log first. And the release is
    /// many-to-one: one variant lets a dozen postings back at once, which is an un-parking of
    /// twelve rows, and an append-only log with no eraser cannot express it at all.
    /// </remarks>
    NoCvVariant = 9,
}

/// <summary>When, if ever, a parked posting comes back to the queue.</summary>
/// <remarks>
/// Four answers rather than the bool <see cref="ParkReasonPolicy.Retryable"/> gives, because
/// the queue predicate does something different for each and "retryable" alone cannot say what.
/// <see cref="WhenAnswered"/> and <see cref="WhenCovered"/> are retryable and each still needs a
/// second fact before the posting returns; folding either into "yes" puts a posting in front of
/// an agent that will park it again for the same reason on every run, which is a loop rather than
/// a retry.
///
/// <b>Two conditional members rather than one carrying a payload</b>, for the reason
/// <see cref="ParkReason.LoginRequired"/> and <see cref="ParkReason.AccountRequired"/> stay apart
/// one enum above: the policy is not the only reader. The two are held back by different clauses,
/// they wait on facts kept in different places, and they ask a person for different things - to
/// answer a question, or to write a CV. A single <c>Conditional</c> member would tidy this enum
/// at the cost of both of those, and would let a reason join the class it happens to resemble
/// instead of the one whose condition it actually waits on.
///
/// Numbered from one like everything else here, though nothing persists it: a classification
/// with a zero member acquires a default, and a default here would be a decision about somebody's
/// application that nobody made.
/// </remarks>
public enum ParkRequeue
{
    /// <summary>Never. The posting is gone for good.</summary>
    Never = 1,

    /// <summary>On the next run. Whatever blocked it was about the attempt, not about the vacancy.</summary>
    NextRun = 2,

    /// <summary>Once the open question raised for it has an answer.</summary>
    WhenAnswered = 3,

    /// <summary>Once a CV variant covering what this posting asks for exists.</summary>
    /// <remarks>
    /// <b>Covering, and not merely written.</b> The fact the queue has to check is a set
    /// difference between what the posting requires and what the library holds, so a variant
    /// authored about something else leaves this posting exactly where it was. Reading this as
    /// "the library changed" is the loop again with a longer period, and one paid for by the
    /// person who has just written a CV.
    /// </remarks>
    WhenCovered = 4,
}

/// <summary>Whether, and when, a parked posting returns to the queue.</summary>
/// <remarks>
/// <b>The whole of the retry policy, as a pure function, so its readers cannot disagree.</b> The
/// queue predicate in <c>JobMatchRepository.ListApplyableAsync</c> decides what an agent is
/// offered and the dashboard decides what a person is shown as waiting on them; written out
/// twice they would drift, and that drift is invisible - a posting missing from a list is not
/// something anybody notices. Pure and free of every Azure type for the reason
/// <see cref="SubmissionState"/> and <c>MatchScorer</c> are: it makes the answers assertable
/// exactly rather than approximately.
///
/// <b>Asked as a question, never read off the numbering.</b> The two permanent reasons happen to
/// be the first two members today. Writing the rule as a comparison would tie it to that
/// accident and break silently the first time a permanent reason is added at the end - the trap
/// <see cref="SubmissionEventTypes.IsTerminal"/> is written the way it is to avoid.
/// </remarks>
public static class ParkReasonPolicy
{
    /// <summary>The reasons that never bring a posting back, as a list a query can be written against.</summary>
    /// <remarks>
    /// <b>Derived from <see cref="Requeue"/> rather than written out a second time</b>, because
    /// it exists only so EF has something it can translate: a static call on a column does not
    /// become SQL, so the queue predicate cannot ask <see cref="Retryable"/> per row. It asks
    /// <c>!Permanent.Contains(row.ParkedReason.Value)</c> instead, which becomes an <c>IN</c>.
    /// The shortlist's channel filter and its projection are written out twice and held together
    /// by a test because there was no way to avoid it; here there is, so the second spelling of
    /// the rule does not exist to go stale.
    /// </remarks>
    public static readonly IReadOnlyList<ParkReason> Permanent =
        [.. Enum.GetValues<ParkReason>().Where(reason => Requeue(reason) is ParkRequeue.Never)];

    /// <summary>The reasons that wait on an open question, for the same query-side purpose as <see cref="Permanent"/>.</summary>
    /// <remarks>
    /// A list of one still, and a list rather than a comparison against
    /// <see cref="ParkReason.MissingAnswer"/> so that a second such reason is a change to this
    /// file alone. The predicate needs it named separately because these rows are held back by a
    /// different clause - one that joins to the open questions - rather than by the reason on
    /// its own.
    ///
    /// <b>The second conditional reason has now arrived and did not join this list</b>, which is
    /// worth saying because joining it was the smaller-looking change:
    /// <see cref="ParkReason.NoCvVariant"/> also waits on a fact, a list called "the conditional
    /// ones" would have taken it, and every caller would have gone on compiling. It waits on a
    /// different fact, checked by a different clause against a different table, so it has
    /// <see cref="AwaitingCvVariant"/> instead. <b>"Waits on something" is not one condition</b>,
    /// and a park filed here that raised no question is let out by the queue's fallback the moment
    /// some unrelated question is answered.
    /// </remarks>
    public static readonly IReadOnlyList<ParkReason> AwaitingAnswer =
        [.. Enum.GetValues<ParkReason>().Where(reason => Requeue(reason) is ParkRequeue.WhenAnswered)];

    /// <summary>The reasons that wait on a covering CV variant, on the same query-side terms.</summary>
    /// <remarks>
    /// A third list rather than a third spelling of the rule, derived from <see cref="Requeue"/>
    /// like the other two and for the same mechanical reason: the queue reaches these rows with
    /// <c>Contains</c> over a column, which becomes an <c>IN</c>, where a call to
    /// <see cref="Requeue"/> per row becomes nothing at all.
    ///
    /// <b>The clause it feeds has to ask about coverage and never about existence.</b> "This
    /// candidate has variants" is true from the first one written and would hand the whole parked
    /// backlog back on the strength of it; what holds a posting is that nothing in the library
    /// covers what <i>it</i> asked for, which is the gap the park recorded. Where that join cannot
    /// be expressed the clause should hold the posting rather than guess, because the two mistakes
    /// are not the same size: holding it is a delay the standing report already names and somebody
    /// can act on, and releasing it is the loop this classification exists to prevent, which
    /// leaves nothing behind but runs that did nothing.
    /// </remarks>
    public static readonly IReadOnlyList<ParkReason> AwaitingCvVariant =
        [.. Enum.GetValues<ParkReason>().Where(reason => Requeue(reason) is ParkRequeue.WhenCovered)];

    /// <summary>When this reason lets the posting back into the queue.</summary>
    /// <remarks>
    /// <b>The discard arm is lenient deliberately</b>, and the two mistakes it chooses between
    /// are not the same size. A stored <c>int</c> outlives the member that wrote it - a reason
    /// withdrawn, or a row written by a newer build than the one reading it - so that arm is
    /// reached by data rather than by code. Reading such a value as permanent removes a live
    /// vacancy from the queue forever with nothing to notice; reading it as retryable offers a
    /// posting the agent parks again, at the cost of one page load. The omission a discard arm
    /// would otherwise hide is caught where it can be: <c>ParkReasonPolicyTests</c> pins every
    /// declared member against a group, so a reason added without a decision is a red build
    /// rather than a silent <see cref="ParkRequeue.NextRun"/>.
    /// </remarks>
    public static ParkRequeue Requeue(ParkReason reason)
        => reason switch
        {
            ParkReason.Expired or ParkReason.Duplicate => ParkRequeue.Never,
            ParkReason.MissingAnswer => ParkRequeue.WhenAnswered,
            ParkReason.NoCvVariant => ParkRequeue.WhenCovered,
            ParkReason.LoginRequired or ParkReason.Captcha or ParkReason.AccountRequired
                or ParkReason.FormError or ParkReason.OutOfQuota => ParkRequeue.NextRun,
            _ => ParkRequeue.NextRun,
        };

    /// <summary>Whether this reason can ever return the posting to the queue.</summary>
    /// <remarks>
    /// <b>True is not the same as "offer it now".</b> <see cref="ParkReason.MissingAnswer"/> is
    /// retryable and still waits on an answer; <see cref="ParkReason.NoCvVariant"/> is retryable
    /// and still waits on a CV somebody has to write. So this answers "is this posting gone for
    /// good" and <see cref="ReturnsToQueue"/> answers "does it come back this run". A caller that
    /// takes this one for the whole policy re-offers a posting it cannot yet apply to, and there
    /// are now two members it gets wrong rather than one.
    /// </remarks>
    public static bool Retryable(ParkReason reason)
        => Requeue(reason) is not ParkRequeue.Never;

    /// <summary>Whether a posting parked for this reason is offered again.</summary>
    /// <param name="reason">Why it was parked.</param>
    /// <param name="answerRecorded">
    /// Whether the open question raised for it has been answered. Read for
    /// <see cref="ParkReason.MissingAnswer"/> and ignored for every other reason.
    /// </param>
    /// <param name="coveringVariantExists">
    /// Whether a CV variant covering what this posting asks for now exists. Read for
    /// <see cref="ParkReason.NoCvVariant"/> and ignored for every other reason. It defaults to
    /// false because false is what the row itself already says - nothing covered this posting, or
    /// it would not have been put down - so a caller that has not looked at the library repeats
    /// the park rather than guessing in the direction of offering it again.
    /// </param>
    /// <remarks>
    /// The whole decision in one call, so no caller has to remember which reasons read the
    /// conditional arguments. The obvious shorthand - <see cref="Retryable"/> and the answer,
    /// together - is wrong in a way that is hard to see: it holds every session-shaped park back
    /// until an answer arrives to a question nobody asked, so a captcha would strand a posting for
    /// good. Permanence is not conditional in the other direction either - an answer recorded
    /// against a posting parked as <see cref="ParkReason.Duplicate"/> does not resurrect it.
    ///
    /// <b>The two conditions are read separately and are never folded into one another.</b> Each
    /// is the fact for exactly one reason: a posting parked for a missing CV is not released by an
    /// answered question, and a posting parked for a missing answer is not released by a CV
    /// written since. They are adjacent booleans and the compiler cannot tell them apart, so pass
    /// them by name; <c>ParkReasonPolicyTests</c> pins the crossed pair, which is the arrangement
    /// an <c>or</c> between them would quietly accept.
    ///
    /// <b>The queue does not call this, and that is what <see cref="Permanent"/>,
    /// <see cref="AwaitingAnswer"/> and <see cref="AwaitingCvVariant"/> are for.</b> This is how
    /// the dashboard and the tool surface answer "when does this come back", and it exists so that
    /// their answer cannot drift from the SQL, which reaches the same rows through those lists
    /// plus one clause per condition. Two readers, one rule, nothing spelled out twice.
    /// </remarks>
    public static bool ReturnsToQueue(
        ParkReason reason, bool answerRecorded, bool coveringVariantExists = false)
        => Requeue(reason) switch
        {
            ParkRequeue.Never => false,
            ParkRequeue.WhenAnswered => answerRecorded,
            ParkRequeue.WhenCovered => coveringVariantExists,
            _ => true,
        };
}
