namespace JobPlatform.Core.Applications;

/// <summary>
/// Whose software takes the application at the other end of an apply link.
/// </summary>
/// <remarks>
/// <b>This is not <c>SubmissionChannel</c> and merging the two would be a regression.</b> That
/// enum answers <i>where</i> an application is made - the board hosts it, or the employer's own
/// system does - and it is read from the posting's <c>OffsiteApply</c> flag. This one answers
/// <i>whose system</i>, and it is read from the URL. A posting can be <c>Ats</c> with no link at
/// all, in which case the vendor is <see cref="Unknown"/> and both answers are correct.
///
/// <b>The value the loop acts on is as often <see cref="Aggregator"/> as it is a vendor name.</b>
/// A link that leaves the board for another job board has not reached an employer, and following
/// it costs a slot from the daily cap to arrive at a second search results page. Knowing the
/// vendor before a tab opens is also what makes <see cref="AtsVendors.RequiresAccount"/> worth
/// having: finding out that Workday wants a username is a fact worth ten minutes.
///
/// Ordering carries no meaning here - unlike <c>SubmissionEventType</c>, nothing compares two of
/// these. The numbering is fixed only so a stored value survives a member being inserted.
/// </remarks>
public enum AtsVendor
{
    /// <summary>
    /// There is no destination to reason about.
    /// </summary>
    /// <remarks>
    /// Zero, so an unset value reads as "not known" - the same default and the same argument as
    /// <c>SubmissionChannel.Unknown</c>. It covers an absent link, a blank one, a string that is
    /// not a URL, and a scheme nothing opens in a browser: in every case there is nothing to
    /// look at, which is one operational answer rather than four.
    ///
    /// <b>It is emphatically not <see cref="Other"/>.</b> The two want opposite work. Unknown is
    /// fixed by finding a link - the cross-board recovery in <c>ApplyUrlSource</c> exists for
    /// exactly this - while Other is fixed by opening the one already held and reading the form.
    /// A single "we don't know" value would hide which of those a posting needs.
    /// </remarks>
    Unknown = 0,

    /// <summary>Greenhouse. Frequently embedded in an employer's own careers page.</summary>
    /// <remarks>
    /// The vendor that most justifies reading query parameters: its embed leaves the host as the
    /// employer's and puts the job id in <c>gh_jid</c>. See <see cref="AtsVendorDetector"/>.
    /// </remarks>
    Greenhouse = 1,

    /// <summary>Lever.</summary>
    Lever = 2,

    /// <summary>Workable.</summary>
    Workable = 3,

    /// <summary>Ashby. Embeds under the employer's domain the way Greenhouse does.</summary>
    Ashby = 4,

    /// <summary>
    /// Workday.
    /// </summary>
    /// <remarks>
    /// <b>Workday almost always requires an account before the form can be seen</b>, and that is
    /// the single most useful thing this enum says. The application is a multi-page wizard behind
    /// a username, a password and an email confirmation, per employer tenant rather than once -
    /// so the cost of an apply is minutes and a mailbox round trip, not a form fill.
    ///
    /// Knowing it <i>before</i> the tab opens is the point. Discovering it at the account
    /// creation screen means the work already spent choosing this posting is spent, and a loop
    /// that meets it half way through has to abandon or park rather than decide. That is what
    /// <see cref="AtsVendors.RequiresAccount"/> is for.
    /// </remarks>
    Workday = 5,

    /// <summary>SAP SuccessFactors. Account-gated like Workday.</summary>
    SuccessFactors = 6,

    /// <summary>SmartRecruiters.</summary>
    SmartRecruiters = 7,

    /// <summary>Teamtailor.</summary>
    Teamtailor = 8,

    /// <summary>BambooHR.</summary>
    BambooHR = 9,

    /// <summary>iCIMS. Account-gated like Workday.</summary>
    Icims = 10,

    /// <summary>Oracle Taleo. Account-gated like Workday.</summary>
    Taleo = 11,

    /// <summary>Pinpoint.</summary>
    Pinpoint = 12,

    /// <summary>
    /// Another job board. Not an employer's system, whatever the link was labelled.
    /// </summary>
    /// <remarks>
    /// <b>A distinct value because the loop skips it, and it came out of the corpus rather than
    /// out of the specification.</b> <c>uk.whatjobs.com/pub_api__cpl__...</c> recurs constantly
    /// in the live data, arriving as a posting's "direct" apply URL - and it is a re-listing, so
    /// following it lands on a search page and, at best, another link to follow. Folding it into
    /// <see cref="Other"/> would put it in the same bucket as a real employer form and spend the
    /// daily cap discovering that by hand.
    ///
    /// It covers boards that originate listings as well as ones that only re-publish them:
    /// LinkedIn and Indeed are here for the same reason WhatJobs is, which is that neither is
    /// the employer. Where the board genuinely hosts the application, that fact is
    /// <c>SubmissionChannel.Board</c> - a different question, answered elsewhere, from a column
    /// the board itself populated.
    /// </remarks>
    Aggregator = 13,

    /// <summary>
    /// An employer's own application system that this list does not name.
    /// </summary>
    /// <remarks>
    /// A real destination with a real form, and the ordinary answer for a bespoke careers site or
    /// one of the long tail of vendors. <b>Nothing here refuses to proceed on it</b> - the vendor
    /// is a hint about what the page will look like, never permission to open it - so an
    /// unrecognised ATS costs nothing beyond the vendor-specific shortcuts it does not get.
    /// Contrast <see cref="Unknown"/>, where there is nothing to open at all.
    /// </remarks>
    Other = 14,
}

/// <summary>Questions worth asking about a vendor before a tab is opened.</summary>
/// <remarks>
/// Written as predicates rather than as comparisons or lists at the call sites, for the reason
/// <c>SubmissionEventTypes.IsTerminal</c> is: a rule spelled out where it is used survives
/// exactly until the second call site, and both of these already have two.
/// </remarks>
public static class AtsVendors
{
    /// <summary>Whether this is an employer's own system, and therefore worth applying through.</summary>
    /// <remarks>
    /// False for <see cref="AtsVendor.Aggregator"/> and <see cref="AtsVendor.Unknown"/> and true
    /// for everything else, <see cref="AtsVendor.Other"/> included - an ATS nobody here has
    /// named is still an ATS. This is the skip the queue owes the corpus: without it a run spends
    /// its cap opening WhatJobs.
    /// </remarks>
    public static bool IsEmployerAts(this AtsVendor vendor)
        => vendor is not (AtsVendor.Unknown or AtsVendor.Aggregator);

    /// <summary>
    /// Whether the form is expected to sit behind a newly created account.
    /// </summary>
    /// <remarks>
    /// The four enterprise suites gate the form on a per-tenant registration, so an apply here is
    /// a signup, an email confirmation and a wizard rather than a form fill. <b>Expected, not
    /// guaranteed</b> - some tenants allow a guest apply - which is why this reads as a warning
    /// to show a person up front and never as a reason to refuse the posting.
    /// </remarks>
    public static bool RequiresAccount(this AtsVendor vendor)
        => vendor is AtsVendor.Workday
            or AtsVendor.SuccessFactors
            or AtsVendor.Taleo
            or AtsVendor.Icims;
}

/// <summary>
/// Reads an apply URL and says whose application system is at the end of it.
/// </summary>
/// <remarks>
/// <b>Pure and Azure-free, like <c>MatchScorer</c> and <c>MetricsCalculator</c>, and for the same
/// reason.</b> It runs over every row of a queue projection, so it may not fetch the page, follow
/// a redirect or resolve a shortener - a detector that needed the network would turn one query
/// into two thousand requests and would answer differently depending on whether a site was up.
/// Everything it knows is in the string, which is also what makes its answers assertable exactly
/// against real corpus URLs.
///
/// <b>Query parameters are read, not only hosts, and that is the whole of the measurement.</b>
/// Greenhouse and Ashby embed their form under the employer's own domain - the live corpus has
/// <c>https://careers.withwaymo.com/jobs?gh_jid=7852098</c>, which a host list answers "Other" -
/// and 1,259 of 2,006 direct apply URLs match no bare host at all. A host-only detector is not a
/// slightly worse detector here; it misses most of what there is to find.
///
/// <b>Host matching is on a label boundary, never on a substring.</b> <c>Contains("lever.co")</c>
/// is true of <c>clever.com</c>, and the resulting failure is silent: the loop takes a
/// Lever-shaped path on a stranger's site and reports a vendor that was never there. So a rule
/// matches a host that <i>is</i> the domain or sits under it, and <c>greenhouse.io.example.com</c>
/// - a domain anybody may register - matches nothing.
///
/// <b>It never throws.</b> The input is a string a scraper lifted off somebody's page: it may be
/// blank, truncated, a <c>mailto:</c>, or not a URL in any sense. Every one of those is a value
/// here - <see cref="AtsVendor.Unknown"/> - because this is called inside a projection over the
/// whole queue, where one exception loses every other row with it.
///
/// The tables below are meant to be read in one screen, and they grow from the corpus the way
/// the concept vocabulary does: from what actually recurs in the data, never for symmetry. A
/// short link is listed only where it is certain - <c>grnh.se</c> is Greenhouse's and is here;
/// guesses at the others are not, because a wrong entry silently mislabels an unrelated domain.
/// </remarks>
public static class AtsVendorDetector
{
    /// <summary>
    /// The domain each vendor's pages live under, matched on a label boundary.
    /// </summary>
    /// <remarks>
    /// A registrable domain and not a full host, so <c>boards.greenhouse.io</c>,
    /// <c>job-boards.greenhouse.io</c> and whatever they publish next are one entry - these
    /// vendors move the subdomain and never the domain, and a list of hosts would rot silently
    /// against a list of domains that does not.
    /// </remarks>
    private static readonly (string Domain, AtsVendor Vendor)[] Hosts =
    [
        // Aggregators first. Not because they can collide with a vendor - the sets are disjoint -
        // but because this is the answer the corpus gives most often and hiding it below twelve
        // vendor rules would misrepresent what the table is mostly for.
        ("whatjobs.com", AtsVendor.Aggregator),
        ("linkedin.com", AtsVendor.Aggregator),
        ("indeed.com", AtsVendor.Aggregator),
        ("indeed.co.uk", AtsVendor.Aggregator),
        ("glassdoor.com", AtsVendor.Aggregator),
        ("glassdoor.co.uk", AtsVendor.Aggregator),
        ("ziprecruiter.com", AtsVendor.Aggregator),
        ("ziprecruiter.co.uk", AtsVendor.Aggregator),
        ("totaljobs.com", AtsVendor.Aggregator),
        ("cwjobs.co.uk", AtsVendor.Aggregator),
        ("reed.co.uk", AtsVendor.Aggregator),
        ("cv-library.co.uk", AtsVendor.Aggregator),
        ("jobsite.co.uk", AtsVendor.Aggregator),
        ("monster.com", AtsVendor.Aggregator),
        ("monster.co.uk", AtsVendor.Aggregator),
        ("adzuna.com", AtsVendor.Aggregator),
        ("adzuna.co.uk", AtsVendor.Aggregator),
        ("talent.com", AtsVendor.Aggregator),
        ("jooble.org", AtsVendor.Aggregator),
        ("simplyhired.com", AtsVendor.Aggregator),
        ("simplyhired.co.uk", AtsVendor.Aggregator),
        ("careerjet.co.uk", AtsVendor.Aggregator),
        ("careerbuilder.com", AtsVendor.Aggregator),
        ("jobrapido.com", AtsVendor.Aggregator),
        ("resume-library.com", AtsVendor.Aggregator),

        ("greenhouse.io", AtsVendor.Greenhouse),
        ("grnh.se", AtsVendor.Greenhouse),
        ("lever.co", AtsVendor.Lever),
        ("workable.com", AtsVendor.Workable),
        ("ashbyhq.com", AtsVendor.Ashby),

        // Workday's tenants are subdomains of these two, one per employer, with the data centre
        // in the middle: acme.wd3.myworkdayjobs.com. Matching the domain covers every one.
        ("myworkdayjobs.com", AtsVendor.Workday),
        ("myworkdaysite.com", AtsVendor.Workday),
        ("workday.com", AtsVendor.Workday),

        ("successfactors.com", AtsVendor.SuccessFactors),
        ("successfactors.eu", AtsVendor.SuccessFactors),
        ("sapsf.com", AtsVendor.SuccessFactors),
        ("sapsf.eu", AtsVendor.SuccessFactors),
        ("smartrecruiters.com", AtsVendor.SmartRecruiters),
        ("teamtailor.com", AtsVendor.Teamtailor),
        ("bamboohr.com", AtsVendor.BambooHR),
        ("bamboohr.co.uk", AtsVendor.BambooHR),
        ("icims.com", AtsVendor.Icims),
        ("taleo.net", AtsVendor.Taleo),
        ("pinpointhq.com", AtsVendor.Pinpoint),
    ];

    /// <summary>
    /// Query parameters that name a vendor whose host does not.
    /// </summary>
    /// <remarks>
    /// These are the embed signatures: the employer keeps its own domain and the vendor's widget
    /// carries the job id. <c>gh_jid</c> is the one that matters most by volume;
    /// <c>lever-origin</c> arrives on the apply step of a Lever flow that has been proxied.
    /// A parameter is listed only where the name is the vendor's own and could not plausibly be
    /// somebody else's - which is why nothing generic like <c>jobId</c> is here, even though
    /// several vendors use it.
    /// </remarks>
    private static readonly (string Name, AtsVendor Vendor)[] Parameters =
    [
        ("gh_jid", AtsVendor.Greenhouse),
        ("gh_src", AtsVendor.Greenhouse),
        ("ashby_jid", AtsVendor.Ashby),
        ("lever-origin", AtsVendor.Lever),
        ("lever-source", AtsVendor.Lever),
    ];

    /// <summary>
    /// Whose application system an apply URL leads to.
    /// </summary>
    /// <remarks>
    /// The host decides first and the query only where the host said nothing, which is the
    /// ordering a person would use: a WhatJobs page carrying a <c>gh_jid</c> in its tracking is
    /// still a WhatJobs page, and what the loop opens is the host. The parameters are read as
    /// whole names rather than as text in the query, so <c>utm_content=gh_jid</c> is not
    /// Greenhouse - a URL is full of somebody else's strings.
    /// </remarks>
    public static AtsVendor Detect(string? url)
    {
        if (!TryReadWebAddress(url, out var address))
        {
            return AtsVendor.Unknown;
        }

        var host = CanonicalHost(address.Host);

        foreach (var (domain, vendor) in Hosts)
        {
            if (IsAtOrUnder(host, domain))
            {
                return vendor;
            }
        }

        var names = ParameterNames(address.Query);

        foreach (var (name, vendor) in Parameters)
        {
            if (names.Contains(name))
            {
                return vendor;
            }
        }

        return AtsVendor.Other;
    }

    /// <summary>Whether <paramref name="host"/> is the domain or a subdomain of it.</summary>
    /// <remarks>
    /// The label boundary is the check, and it has to be tested on both sides: the character
    /// before the suffix must be a dot, or <c>clever.com</c> is Lever and <c>notgreenhouse.io</c>
    /// is Greenhouse. Length equality is the other half - a host that <i>is</i> the domain
    /// matches, and there is no dot in front of it to find.
    /// </remarks>
    private static bool IsAtOrUnder(string host, string domain)
        => host.Length == domain.Length
            ? host.Equals(domain, StringComparison.Ordinal)
            : host.Length > domain.Length
                && host[host.Length - domain.Length - 1] == '.'
                && host.EndsWith(domain, StringComparison.Ordinal);

    /// <summary>The parsed address, or false where there is nothing to open.</summary>
    /// <remarks>
    /// <b>Lenient about the scheme and strict about the host</b>, because that is the shape of
    /// the input. Boards publish apply links without a scheme, so <c>careers.example.com/jobs</c>
    /// is read rather than discarded; but a bare word prefixed with <c>https://</c> parses
    /// happily into a host, so the result must still look like one - a dot, and no whitespace -
    /// or "unemployed" becomes an unrecognised ATS.
    ///
    /// Non-web schemes are excluded deliberately and by name. <c>mailto:jobs@acme.com</c> parses
    /// with a host of <c>acme.com</c>, which would otherwise be reported as that employer's
    /// application system when it is an inbox and no form exists to fill.
    /// </remarks>
    private static bool TryReadWebAddress(string? url, out Uri address)
    {
        address = null!;

        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var candidate = url.Trim();

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed)
            && (!LooksLikeBareHost(candidate)
                || !Uri.TryCreate("https://" + candidate, UriKind.Absolute, out parsed)))
        {
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (!IsHostLike(parsed.Host))
        {
            return false;
        }

        address = parsed;

        return true;
    }

    /// <summary>Whether a scheme-less string starts with something that could be a host.</summary>
    private static bool LooksLikeBareHost(string candidate)
    {
        var end = candidate.AsSpan().IndexOfAny('/', '?', '#');

        return IsHostLike(end < 0 ? candidate : candidate[..end]);
    }

    /// <summary>A dot and no whitespace. Deliberately not a grammar for domain names.</summary>
    /// <remarks>
    /// The job is to reject strings that are not addresses at all, not to validate the ones that
    /// are. Anything stricter would have to decide about internationalised domains and new top
    /// level domains, and getting that wrong turns a real employer's careers page into
    /// <see cref="AtsVendor.Unknown"/> - a posting silently dropped rather than one reported as
    /// unrecognised.
    /// </remarks>
    private static bool IsHostLike(ReadOnlySpan<char> host)
    {
        if (!host.Contains('.'))
        {
            return false;
        }

        foreach (var character in host)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The names of the query parameters, lower cased, without their values.</summary>
    /// <remarks>
    /// Split rather than searched, because the values are attacker-adjacent text in the sense
    /// that matters here: they are whatever a board put in a tracking parameter, and one of them
    /// containing <c>gh_jid</c> is not a fact about the employer.
    ///
    /// A trailing <c>[]</c> is stripped, encoded or not. Lever writes <c>lever-source[]</c> for
    /// its repeated parameter, which arrives percent-encoded as <c>lever-source%5B%5D</c> and
    /// would otherwise match nothing at all.
    /// </remarks>
    private static List<string> ParameterNames(string query)
    {
        var names = new List<string>();
        var remaining = query.AsSpan().TrimStart('?');

        while (!remaining.IsEmpty)
        {
            var separator = remaining.IndexOf('&');
            var pair = separator < 0 ? remaining : remaining[..separator];
            remaining = separator < 0 ? default : remaining[(separator + 1)..];

            var equals = pair.IndexOf('=');
            var name = (equals < 0 ? pair : pair[..equals]).Trim();

            if (!name.IsEmpty)
            {
                names.Add(CanonicalName(name.ToString()));
            }
        }

        return names;
    }

    /// <summary>Lower cased and without a trailing dot.</summary>
    /// <remarks>
    /// <see cref="Uri"/> already lower cases a DNS host, so this is belt and braces for the one
    /// case it leaves alone: the fully qualified form <c>greenhouse.io.</c>, whose trailing root
    /// label would defeat every suffix comparison in <see cref="IsAtOrUnder"/> while looking
    /// identical to a reader.
    /// </remarks>
    private static string CanonicalHost(string host)
        => host.TrimEnd('.').ToLowerInvariant();

    /// <summary>Lower cased, percent-decoded, and without a trailing array marker.</summary>
    private static string CanonicalName(string name)
    {
        var decoded = Uri.UnescapeDataString(name);

        if (decoded.EndsWith("[]", StringComparison.Ordinal))
        {
            decoded = decoded[..^2];
        }

        return decoded.ToLowerInvariant();
    }
}
