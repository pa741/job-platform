using System.Text;

namespace JobPlatform.Core.Applications;

/// <summary>
/// One vacancy as an employer's own applicant tracking system publishes it.
/// </summary>
/// <remarks>
/// <b>A plain record, because the fetch must not reach this far.</b> Greenhouse, Ashby, Lever,
/// Workable and SmartRecruiters each answer a differently shaped JSON document, and whoever reads
/// one maps it into these three fields; nothing here knows an HTTP type, a board token, a vendor
/// or a response. That is the argument <c>MatchScorer</c> and <c>SubmissionState</c> rest on, and
/// it is worth more here than in either: this is the step that decides <i>which vacancy a person
/// is sent to</i>, so it has to be assertable exactly, against a fixture, with no network in the
/// room and no board that has to still be up.
///
/// <b>What it deliberately does not carry.</b> No vendor - <see cref="AtsVendorDetector"/> reads
/// that off <see cref="ApplyUrl"/> and a second copy would be free to disagree with it. No
/// department, no requisition id, no posted date: every field a matcher can see is a field
/// somebody eventually matches on, and each of those is a fact about how the employer files its
/// vacancies rather than about the job the candidate actually found.
///
/// <see cref="Location"/> is nullable because boards genuinely publish listings without one, and
/// that is silence rather than an error - see <see cref="AtsListingMatcher"/> on why silence must
/// not be read as disagreement. <see cref="Title"/> and <see cref="ApplyUrl"/> are not nullable
/// and are still checked before use: this record is filled from somebody else's JSON, where a
/// null, an empty string and a missing property all arrive by the same route.
/// </remarks>
/// <param name="Title">The vacancy's title, as the board publishes it.</param>
/// <param name="Location">
/// The board's free-text place, exactly as published and not pre-parsed by the caller. "Hybrid",
/// "London, UK", "Remote (UK)" and "New York, NY (HQ)" are all real shapes; parsing it upstream
/// would settle the hardest question in this file somewhere nothing tests it.
/// </param>
/// <param name="ApplyUrl">Where the application is made. The whole point of the exercise.</param>
public sealed record AtsListing(string Title, string? Location, string ApplyUrl);

/// <summary>
/// How much of the posting the matched listing actually agreed with.
/// </summary>
/// <remarks>
/// <b>The members name the evidence rather than a feeling.</b> A caller can act on "the place
/// agreed" - store it, show it, keep it out of an unattended run - and can do nothing sensible
/// with "0.8" except compare it against a number somebody guessed. The same reasoning already
/// stands one layer up: <c>ApplyUrlSource</c> separates a published link from an inference by
/// naming where it came from, not by scoring it.
///
/// <b>The numbering ascends with strength, unlike <c>ApplyUrlSource</c>'s.</b> That enum's own
/// remarks record what the alternative costs - there the weakest value carries the highest number,
/// so anything sorting on it inverts exactly at the top. There is no zero member here: a
/// <c>default</c> must never be able to read as a match that was made.
/// </remarks>
public enum AtsMatchConfidence
{
    /// <summary>
    /// The titles agreed and nothing could be checked about the place.
    /// </summary>
    /// <remarks>
    /// Either side may be the silent one: the posting carries no city, or the board answered the
    /// question with an arrangement - "Remote", "Hybrid", "Remote (UK)" - which is an answer to a
    /// different question. <b>Weaker, and still worth returning.</b> An employer with one vacancy
    /// under this title has told you which one it is; an employer with three of them in three
    /// cities never reaches here at all, because more than one contender is an abstention rather
    /// than a choice.
    /// </remarks>
    TitleOnly = 1,

    /// <summary>
    /// The titles agreed, both sides named a place, and it was the same place.
    /// </summary>
    /// <remarks>
    /// The city is what took 285 cross-board title-and-employer matches down to 211 - better than
    /// a quarter of them were one employer advertising one title in several cities - so this value
    /// says that the failure the whole rule is built around was actually excluded for this
    /// posting, rather than merely not detectable.
    /// </remarks>
    TitleAndPlace = 2,
}

/// <summary>
/// What the match concluded. Two of the three answers hand back no link.
/// </summary>
/// <remarks>
/// <b>"Nothing found" and "refused to choose" are the same action and different facts</b>, and
/// this repository has already paid once for letting two of those share one representation: "no
/// apply URL" meant either "the board hosts the application" or "nobody opened the detail page",
/// and that was written down as indistinguishable until a second column showed it was not.
///
/// They want opposite work. <see cref="NoMatch"/> is evidence about the <i>board</i>: the token
/// may belong to another company entirely - "Dex", "Kernel", "Fin" and "Orbital" are all real
/// boards owned by somebody - or the vacancy is filled and gone, and either way the thing to
/// re-check is the token. <see cref="Ambiguous"/> is evidence about the <i>posting</i>: the board
/// is plainly the right employer, it advertises this exact title more than once, and what is
/// missing is the city that would separate them. A single null answering both reports neither, and
/// an operator watching recoveries would see one number fall with no way to ask why.
///
/// Numbered from one, like every other classification here: a stored zero would read as a real
/// decision rather than as "nothing was recorded".
/// </remarks>
public enum AtsListingMatchOutcome
{
    /// <summary>Exactly one listing survived, and it is the one to apply through.</summary>
    Matched = 1,

    /// <summary>
    /// Several listings matched and the rule declined to pick between them.
    /// </summary>
    /// <remarks>
    /// <b>An abstention, not a coin toss, and the case is measured rather than hypothetical.</b>
    /// One employer advertising one title in several cities is 74 of 285 cross-board candidates -
    /// better than a quarter - and settling it on board order, on the first match, or on the
    /// shortest location string would dress that coin toss as arithmetic. The contenders come back
    /// so a caller can say how many there were; nothing here ranks them.
    /// </remarks>
    Ambiguous = 2,

    /// <summary>Nothing on the board matched. The posting is exactly where it was.</summary>
    NoMatch = 3,
}

/// <summary>
/// The answer: a listing to apply through and how much agreed, or neither.
/// </summary>
/// <remarks>
/// <b>Built through the three factories rather than by property initialisers</b>, so that a
/// <see cref="AtsListingMatchOutcome.Matched"/> carrying no listing, or a listing carrying no
/// confidence, cannot be expressed at all. That is the same instinct as the application pack
/// having no parameter a CV label could be passed in: a rule enforced by there being no way to
/// write the alternative outlives one enforced by everybody remembering it.
/// </remarks>
public sealed record AtsListingMatch
{
    private AtsListingMatch(
        AtsListingMatchOutcome outcome,
        AtsListing? listing,
        AtsMatchConfidence? confidence,
        IReadOnlyList<AtsListing> contenders)
    {
        Outcome = outcome;
        Listing = listing;
        Confidence = confidence;
        Contenders = contenders;
    }

    /// <summary>Nothing on the board matched this posting.</summary>
    public static AtsListingMatch None { get; } = new(AtsListingMatchOutcome.NoMatch, null, null, []);

    /// <summary>What was concluded. Read this before reading anything else.</summary>
    public AtsListingMatchOutcome Outcome { get; }

    /// <summary>
    /// The listing to apply through, and null on either of the other two outcomes.
    /// </summary>
    /// <remarks>
    /// Null on <see cref="AtsListingMatchOutcome.Ambiguous"/> deliberately, rather than holding
    /// the first or the nearest of the contenders. A field holding the pick of a set the rule has
    /// just refused to choose from is a field somebody eventually applies through, and the refusal
    /// would then have cost a request and bought nothing.
    /// </remarks>
    public AtsListing? Listing { get; }

    /// <summary>How much agreed, where something was matched. Null otherwise.</summary>
    public AtsMatchConfidence? Confidence { get; }

    /// <summary>
    /// The listings that tied, on <see cref="AtsListingMatchOutcome.Ambiguous"/> only. Empty
    /// otherwise.
    /// </summary>
    /// <remarks>
    /// Reported so an abstention can be counted and read - three listings under one title is a
    /// fact about the employer worth seeing - and in the order the board returned them, because
    /// any ordering imposed here would be a ranking of the set this file has just said it will not
    /// rank.
    /// </remarks>
    public IReadOnlyList<AtsListing> Contenders { get; }

    /// <summary>One listing, and how much of it agreed.</summary>
    public static AtsListingMatch For(AtsListing listing, AtsMatchConfidence confidence)
    {
        ArgumentNullException.ThrowIfNull(listing);

        return new AtsListingMatch(AtsListingMatchOutcome.Matched, listing, confidence, []);
    }

    /// <summary>Several listings matched and none of them is returned.</summary>
    public static AtsListingMatch Abstained(IReadOnlyList<AtsListing> contenders)
    {
        ArgumentNullException.ThrowIfNull(contenders);

        if (contenders.Count < 2)
        {
            throw new ArgumentException(
                "An abstention needs at least two listings to have abstained between.",
                nameof(contenders));
        }

        return new AtsListingMatch(AtsListingMatchOutcome.Ambiguous, null, null, contenders);
    }
}

/// <summary>
/// Matches one posting to one entry on an employer's own board, or to nothing.
/// </summary>
/// <remarks>
/// <b>This is where a wrong answer sends somebody to the wrong vacancy</b>, which makes it the
/// file in this feature worth being timid in. The link it returns is not read, ranked or
/// displayed - it is opened, and a form is filled in under the candidate's name at the far end of
/// it. A posting this rule declines is exactly where it was before, carrying no employer link and
/// costing nothing; a posting this rule gets wrong is an application sent to a job nobody chose,
/// and nothing downstream can tell the two apart afterwards, because the form opens either way.
/// <b>So every rule below is written to fail towards silence.</b>
///
/// <b>The discipline is borrowed rather than invented, and the measurement behind it is this
/// codebase's own.</b> <c>JobFingerprint.CrossBoardKey</c> folds case, punctuation and whitespace
/// and then <i>requires</i> the city, because title and employer alone matched 285 postings across
/// boards and adding the city left 211: 74 of them - better than a quarter - were one employer
/// advertising one title in several cities. The identical failure is available here and it is
/// worse, because there the consequence was a duplicate row in a list and here it is the link an
/// agent actually opens. So: fold, compare, and let the place separate what the title cannot.
///
/// <b>The place is where the two sides stop looking alike, and that is the interesting half.</b>
/// A board posting has a city parsed out of "City, REGION, CC"; an employer's ATS has a free-text
/// field a recruiter typed, and the live shapes include "Hybrid", "London, UK", "Remote (UK)",
/// "New York, NY (HQ)", "Greater London, United Kingdom" and "London / Remote". The rule is in
/// three parts and each prevents a different mistake:
/// <list type="number">
/// <item>
/// <b>A place is compared segment by segment, for equality, never by containment.</b> The location
/// is cut on commas, semicolons, slashes and pipes, and each piece is folded and compared whole
/// against the posting's city. Containment is the generous version, and it is what makes "York"
/// match "New York, NY" - a real city inside a real city, at the right employer, with the
/// candidate sent 200 miles.
/// </item>
/// <item>
/// <b>An arrangement is not a place, and it must not read as a contradiction.</b> "Remote",
/// "Hybrid" and "Remote (UK)" answer how the job is done rather than where it is, and boards file
/// remote vacancies under a city constantly - so an arrangement leaves the place unstated, which
/// costs the confirmation and never the match. Where an arrangement sits <i>beside</i> a place -
/// "Hybrid - London", "London / Remote" - the arrangement words are removed and what remains is
/// compared, so the city still confirms.
/// </item>
/// <item>
/// <b>A place stated on both sides that disagrees drops the listing outright.</b> Not demoted to a
/// weaker confidence: dropped. "Berlin, Germany" against a London posting is the measured failure
/// this whole rule exists to refuse, and a confidence value is no defence against a caller that
/// stores it and applies anyway.
/// </item>
/// </list>
///
/// <b>Titles are compared exactly after folding, with one widening.</b> Case, punctuation and
/// whitespace go, so "Senior Software Engineer (Platform)" and "Senior Software Engineer,
/// Platform" agree; then a trailing run of words saying <i>where</i> or <i>how</i> is removed from
/// both sides, so an ATS's "Software Engineer (Remote)" and "Data Engineer - London" reach the
/// posting's bare title. Only a <i>trailing</i> run, which is what makes it safe: "Engineer,
/// Remote Sensing" keeps every word, because "sensing" stops the walk before "remote" is reached.
/// <b>And every widening of the title rule is caught by the abstention</b> - if it wrongly folds
/// two of the employer's own entries together, both become contenders and nothing is returned.
/// That is the property which makes widening affordable at all.
///
/// <b>Seniority is not folded, exactly as it is not folded in the cross-board key.</b> "Senior
/// Data Engineer" and "Data Engineer" are a ladder and stay two jobs; Harnham advertised one
/// requisition four times - Junior, plain, Senior and Lead - and merging a rung there costs a
/// duplicate row, where merging one here sends an application to the wrong grade.
///
/// <b>What this rule cannot do, stated rather than discovered later.</b> It has no gazetteer and
/// will not acquire one - the refusal <c>JobFingerprint.CanonicalCity</c> already makes, where
/// three general rules about how boards write a metropolitan area replaced a list of cities. So:
/// <list type="bullet">
/// <item>
/// A listing naming only a country - "United Kingdom" against a posting in London - reads as a
/// place that disagrees, and the listing is dropped. That is a lost recovery on a shape that
/// occurs, and it is the safe direction.
/// </item>
/// <item>
/// "NYC" is not "New York", "Bengaluru" is not "Bangalore", and a translated city name is not the
/// English one. Each costs a recovery; none produces a wrong one.
/// </item>
/// <item>
/// <b>The softest edge is an arrangement paired with a place we do not share</b> - "Remote -
/// Berlin" against a London posting - which reads as unpinned rather than as a contradiction, and
/// can therefore be matched on the title alone when it is the only entry under that title. The
/// alternative makes "Remote (UK)" a contradiction too, and that shape is far commoner than this
/// one; separating a country from a city is the gazetteer again.
/// </item>
/// <item>
/// A department suffix is not stripped: "Software Engineer, Payments" will not match a posting
/// titled "Software Engineer". A trailing word saying what the job is, is not noise.
/// </item>
/// <item>
/// <b>A confirmed match is still not proof.</b> An employer may run two genuinely different
/// vacancies under one title in one city; where both are on the board this abstains, and where
/// only one is published it will be returned. That residue is why a link recovered here is
/// recorded as <c>ApplyUrlSource.MatchedOnEmployerAts</c> - an inference with a name - rather than
/// folded in beside a link the board itself published.
/// </item>
/// </list>
/// </remarks>
public static class AtsListingMatcher
{
    /// <summary>
    /// Words describing how a job is done rather than where it is.
    /// </summary>
    /// <remarks>
    /// Removed from a location before it is compared, and taken as a sign that the field was
    /// answering a different question when nothing in it matched. <b>Deliberately short and
    /// deliberately unambiguous</b>: every entry is a word that cannot plausibly be part of a place
    /// name, because a word removed in error turns one place into another. "On" is the entry
    /// missing for exactly that reason - "Stratford-upon-Avon" and "Newcastle-under-Lyme" are how
    /// that goes wrong - so "on site" is caught by "onsite" alone, and the two-word spelling costs
    /// a confirmation rather than a match.
    /// </remarks>
    private static readonly HashSet<string> Arrangements = new(StringComparer.Ordinal)
    {
        "remote",
        "hybrid",
        "onsite",
        "anywhere",
        "worldwide",
        "distributed",
        "virtual",
        "wfh",
    };

    /// <summary>
    /// How an employer's board writes several places into one field.
    /// </summary>
    /// <remarks>
    /// The comma is JobSpy's own convention and the rest are what recruiters type. The slash
    /// matters most: "London / Remote" and "Remote - US/Canada" are one field listing
    /// alternatives, and read as a single string neither can ever equal a city. Nothing splits on
    /// a hyphen - folding already turns it into a space, and splitting on it would cut a hyphenated
    /// city name in half before the fold could keep it whole.
    /// </remarks>
    private static readonly char[] SegmentSeparators = [',', ';', '/', '|', '\n'];

    /// <summary>
    /// Finds the one listing on an employer's board that is this posting, or declines to.
    /// </summary>
    /// <param name="postingTitle">
    /// The posting's title as the board carrying it published it. Blank answers
    /// <see cref="AtsListingMatch.None"/> rather than matching everything, which is the shape this
    /// mistake takes when a caller passes a column that was never populated.
    /// </param>
    /// <param name="postingCity">
    /// The city parsed out of the posting's location - <c>JobPostings.LocationCity</c>, the same
    /// value the cross-board key is built from - or null where the posting states none. Null is a
    /// fact about the posting and never a wildcard: it cannot make a place agree, and it cannot
    /// make one disagree either.
    /// </param>
    /// <param name="listings">
    /// Everything the employer's board publishes, already fetched. The whole board rather than a
    /// pre-filtered subset, because filtering upstream settles the ambiguity question somewhere
    /// that cannot see it: two listings excluded before they arrive are two listings this cannot
    /// abstain between.
    /// </param>
    public static AtsListingMatch Match(
        string? postingTitle,
        string? postingCity,
        IReadOnlyList<AtsListing> listings)
    {
        ArgumentNullException.ThrowIfNull(listings);

        var city = CanonicalCity(Fold(postingCity));
        var title = TrimTrailingPlace(Fold(postingTitle), city);

        if (title.Length == 0)
        {
            return AtsListingMatch.None;
        }

        // Kept in two lists rather than scored into one, because these are not two grades of the
        // same thing: one set has had the measured failure excluded and the other has not.
        List<AtsListing>? confirmed = null;
        List<AtsListing>? unplaced = null;

        foreach (var listing in listings)
        {
            if (string.IsNullOrWhiteSpace(listing.ApplyUrl))
            {
                continue;
            }

            var listingTitle = TrimTrailingPlace(Fold(listing.Title), city);

            // Emptiness is checked on the folded form rather than the raw one: a title of "--"
            // folds to nothing, and without this two pieces of punctuation would agree with each
            // other. The posting's own blank title was refused above for the same reason.
            if (listingTitle.Length == 0
                || !string.Equals(listingTitle, title, StringComparison.Ordinal))
            {
                continue;
            }

            switch (ComparePlace(city, listing.Location))
            {
                case PlaceAgreement.Agrees:
                    (confirmed ??= []).Add(listing);
                    break;

                case PlaceAgreement.Unstated:
                    (unplaced ??= []).Add(listing);
                    break;

                // Contradicts falls through to nothing: dropped, never demoted to a confidence.
            }
        }

        // A listing the place confirms outranks one the place could not speak to, and this is the
        // rule most worth arguing with. A board carrying "Data Engineer, London" and "Data
        // Engineer, Remote" against a London posting has two contenders, and abstaining there
        // forfeits the recovery on precisely the evidence the cross-board measurement says to
        // trust. What it can cost is picking the located requisition when the candidate found the
        // remote one - same employer, same title, a real vacancy - which is a smaller mistake than
        // the no-link outcome it replaces. Where two listings AGREE on the place, nothing
        // separates them and the abstention stands.
        if (confirmed is { Count: > 0 })
        {
            return Decide(confirmed, AtsMatchConfidence.TitleAndPlace);
        }

        return unplaced is { Count: > 0 }
            ? Decide(unplaced, AtsMatchConfidence.TitleOnly)
            : AtsListingMatch.None;
    }

    /// <summary>One contender is an answer; several are an abstention, with one exception.</summary>
    /// <remarks>
    /// <b>Contenders sharing a destination are one vacancy, not a tie.</b> Boards list a
    /// requisition under two departments and answer both with the same apply URL, and abstaining
    /// there would be refusing to choose between one link and itself. The comparison is ordinal
    /// over the whole URL - nothing trimmed, no case folded, no query parameter ignored - because
    /// two URLs differing anywhere may be two requisitions, and this exception is only safe while
    /// it cannot be wrong.
    /// </remarks>
    private static AtsListingMatch Decide(List<AtsListing> contenders, AtsMatchConfidence confidence)
        => contenders.Count == 1 || SharesOneDestination(contenders)
            ? AtsListingMatch.For(contenders[0], confidence)
            : AtsListingMatch.Abstained(contenders);

    private static bool SharesOneDestination(List<AtsListing> contenders)
    {
        for (var index = 1; index < contenders.Count; index++)
        {
            if (!string.Equals(contenders[index].ApplyUrl, contenders[0].ApplyUrl, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>What the two places say about each other.</summary>
    private enum PlaceAgreement
    {
        /// <summary>One side said nothing, or said how rather than where.</summary>
        Unstated,

        /// <summary>Both named a place and it is the same place.</summary>
        Agrees,

        /// <summary>Both named a place and they are different places.</summary>
        Contradicts,
    }

    /// <summary>
    /// Compares the posting's city against an ATS's free text.
    /// </summary>
    /// <remarks>
    /// <b>The arrangement test reads the whole field and the equality test reads one segment</b>,
    /// which is what lets "London / Remote" agree with London while "Remote (UK)" stays silent
    /// about it. A field mentioning an arrangement anywhere has said something about how the job
    /// is done, so when no segment names our city the honest answer is that the question was not
    /// answered - not that it was answered differently.
    /// </remarks>
    private static PlaceAgreement ComparePlace(string city, string? location)
    {
        if (city.Length == 0 || string.IsNullOrWhiteSpace(location))
        {
            return PlaceAgreement.Unstated;
        }

        foreach (var segment in location.Split(SegmentSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var place = CanonicalCity(WithoutArrangements(Fold(segment)));

            if (place.Length > 0 && string.Equals(place, city, StringComparison.Ordinal))
            {
                return PlaceAgreement.Agrees;
            }
        }

        return MentionsArrangement(Fold(location))
            ? PlaceAgreement.Unstated
            : PlaceAgreement.Contradicts;
    }

    /// <summary>Whether any whole word of a folded string describes an arrangement.</summary>
    private static bool MentionsArrangement(string folded)
    {
        foreach (var word in folded.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Arrangements.Contains(word))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The folded string with any arrangement words taken out.</summary>
    /// <remarks>
    /// Whole words only, which is the whole safety of it: a substring rule finds "remote" inside a
    /// place name that merely contains those letters and quietly renames a city.
    /// </remarks>
    private static string WithoutArrangements(string folded)
    {
        var kept = folded
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => !Arrangements.Contains(word));

        return string.Join(' ', kept);
    }

    /// <summary>
    /// A folded title with a trailing run of place and arrangement words removed.
    /// </summary>
    /// <remarks>
    /// <b>Trailing only, and one word at a time from the end.</b> "Data Engineer - London
    /// (Remote)" folds to "data engineer london remote" and walks back to "data engineer";
    /// "Engineer, Remote Sensing" folds to "engineer remote sensing" and stops at once, because
    /// "sensing" is not one of these words and the walk never reaches "remote". A rule removing
    /// these words wherever they appeared would turn "Remote Sensing Engineer" into "sensing
    /// engineer" and match it against a job that is not it.
    ///
    /// <b>The city comes off as a phrase and only from the end</b>, so a multi-word city is
    /// removed whole - "engineer new york" against a New York posting - and a title that <i>is</i>
    /// a place name keeps it, because at least one word always survives.
    /// </remarks>
    private static string TrimTrailingPlace(string folded, string city)
    {
        var title = folded;
        var trimming = true;

        while (trimming)
        {
            trimming = false;

            if (city.Length > 0
                && title.Length > city.Length + 1
                && title[title.Length - city.Length - 1] == ' '
                && title.EndsWith(city, StringComparison.Ordinal))
            {
                title = title[..^(city.Length + 1)];
                trimming = true;
            }

            var lastSpace = title.LastIndexOf(' ');

            if (lastSpace > 0 && Arrangements.Contains(title[(lastSpace + 1)..]))
            {
                title = title[..lastSpace];
                trimming = true;
            }
        }

        return title;
    }

    /// <summary>
    /// One spelling for a city that boards write several ways.
    /// </summary>
    /// <remarks>
    /// The three rules from <c>JobFingerprint.CanonicalCity</c>, applied to both sides here for
    /// the reason they were written there: "Greater X", "X Area" and "City of X" are how boards
    /// write a metropolitan area rather than names anybody uses, and one place arrives as London,
    /// London Area, Greater London and City Of London across 4,323, 1,542, 322 and 66 postings.
    /// The posting side needs this as much as the board side does - <c>LocationCity</c> holds
    /// whichever spelling the board carrying the advert used.
    ///
    /// <b>Restated rather than shared, and the cost of that is worth naming.</b> The rule the
    /// hashed key enforces - one writer, or a cluster silently splits in two - does not transfer,
    /// because nothing here is stored: a divergence between the two spellings of this costs a
    /// recovery that does not happen, which is a posting left exactly where it was. Making the
    /// fingerprint's private helper public to share it would widen the surface of the one function
    /// this database's deduplication depends on, to save nine lines.
    /// </remarks>
    private static string CanonicalCity(string city)
    {
        if (city.StartsWith("greater ", StringComparison.Ordinal))
        {
            return city["greater ".Length..];
        }

        if (city.StartsWith("city of ", StringComparison.Ordinal))
        {
            return city["city of ".Length..];
        }

        return city.EndsWith(" area", StringComparison.Ordinal)
            ? city[..^" area".Length]
            : city;
    }

    /// <summary>Case-, punctuation- and whitespace-insensitive form.</summary>
    /// <remarks>
    /// Character for character what <c>JobFingerprint</c> folds a title and a company with, so
    /// that "Senior Software Engineer (Platform)" and "senior software engineer - platform" are
    /// one string in both files. Punctuation becomes a separator rather than vanishing: removing
    /// it outright would make "AI/ML" one word and "co-op" another, and the tokens either side of
    /// it are what the trailing-word walk counts.
    /// </remarks>
    private static string Fold(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var lastWasSpace = false;

        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }
}
