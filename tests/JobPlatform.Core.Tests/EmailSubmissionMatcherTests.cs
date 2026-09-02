using System.Reflection;
using JobPlatform.Core.Applications;
using JobPlatform.Core.Submissions;
using Xunit;

namespace JobPlatform.Core.Tests;

/// <summary>
/// Which application a recruiter message is about, and - mostly - when to refuse to say.
/// </summary>
/// <remarks>
/// The interesting half of this suite is the abstentions, because the obvious implementation
/// answers all of them: it takes the highest score, breaks the tie on recency, and reads a
/// vendor's own domain as though it named an application. Each of those is written out here
/// against the case that makes it wrong, so the assertion is a check rather than a restatement.
///
/// A wrong answer is not symmetrical with no answer. It writes a rejection onto an application
/// that was never rejected, into a log with no eraser, and the event recording it is true - the
/// message really did say that. Every threshold below is set from that asymmetry.
/// </remarks>
public sealed class EmailSubmissionMatcherTests
{
    private static readonly DateTimeOffset Received = new(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Applied = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);

    private static EmailIdentityTokens Message(
        string? subject = null,
        string? senderName = null,
        string? senderDomain = null,
        string? senderVendor = null,
        IReadOnlyList<string>? mentions = null,
        DateTimeOffset? receivedAtUtc = null)
        => new(receivedAtUtc ?? Received, subject, senderName, senderDomain, senderVendor, mentions);

    private static EmailMatchCandidate Application(
        long id,
        string company,
        string? applyHost = null,
        string? vendor = null,
        DateTimeOffset? createdAtUtc = null)
        => new(id, company, applyHost, vendor, createdAtUtc ?? Applied);

    [Fact]
    public void The_employer_naming_itself_in_the_sender_name_is_enough_to_match()
    {
        var result = EmailSubmissionMatcher.Match(
            Message(subject: "An update on your application", senderName: "Acme Robotics Careers"),
            [Application(41, "Acme Robotics"), Application(42, "Umbrella Health")]);

        Assert.Equal(EmailMatchOutcome.Matched, result.Outcome);
        Assert.Equal(41, result.Match!.SubmissionId);
        Assert.Contains(EmailMatchSignal.CompanyInSenderName, result.Match.Signals);

        // The employer that was never named carried no evidence at all, so it is not a candidate
        // the matcher is choosing between - it is not in the ranking.
        Assert.Single(result.Ranked);
    }

    /// <summary>
    /// The case the whole design turns on.
    /// </summary>
    /// <remarks>
    /// Two live applications to one employer, and nothing a message carries is about a posting -
    /// the company, the sending domain and the vendor are all facts about the employer. The
    /// tempting answer is the more recent application, which is why recency is never scored.
    /// </remarks>
    [Fact]
    public void Two_live_applications_to_the_same_employer_are_ambiguous_rather_than_guessed()
    {
        var result = EmailSubmissionMatcher.Match(
            Message(senderName: "Acme Robotics Careers", subject: "Thank you for applying"),
            [
                Application(41, "Acme Robotics", createdAtUtc: Applied),
                Application(42, "Acme Robotics", createdAtUtc: Applied.AddDays(5)),
            ]);

        Assert.Equal(EmailMatchOutcome.Ambiguous, result.Outcome);
        Assert.True(result.Abstained);
        Assert.Null(result.Match);

        // Both are handed back, because an abstention is only useful if it comes with the
        // shortlist to put in front of a person - and the newer one is first, which is an
        // ordering rather than a verdict.
        Assert.Equal(2, result.Ranked.Count);
        Assert.Equal(42, result.Ranked[0].SubmissionId);
        Assert.Equal(result.Ranked[0].Confidence, result.Ranked[1].Confidence, 2);
    }

    /// <summary>
    /// Being well ahead does not make an employer-level signal into a posting-level one.
    /// </summary>
    /// <remarks>
    /// One of the two applications was made on the employer's own site and the message came from
    /// the employer's own domain, which puts it a long way clear of the ambiguity margin. It is
    /// still not evidence about <i>which application</i>: the recruiter writes from that domain
    /// whichever of the two they are writing about.
    /// </remarks>
    [Fact]
    public void A_sibling_application_at_the_same_employer_is_ambiguous_even_when_one_is_far_ahead()
    {
        var result = EmailSubmissionMatcher.Match(
            Message(senderName: "Acme Robotics", senderDomain: "acme-robotics.com"),
            [
                Application(41, "Acme Robotics", applyHost: "jobs.acme-robotics.com"),
                Application(42, "Acme Robotics", applyHost: "boards.greenhouse.io"),
            ]);

        Assert.Equal(EmailMatchOutcome.Ambiguous, result.Outcome);
        Assert.Null(result.Match);

        // Far beyond the margin, and the ceiling of what this function will ever claim.
        Assert.Equal(EmailSubmissionMatcher.Ceiling, result.Ranked[0].Confidence, 2);
        Assert.True(result.Ranked[0].Confidence - result.Ranked[1].Confidence > EmailSubmissionMatcher.AmbiguityMargin);
    }

    /// <summary>
    /// An employer whose name is an ordinary word is not named by prose containing that word.
    /// </summary>
    /// <remarks>
    /// The specific failure: exactly one application went through Workday, a Workday message
    /// arrives, and the subject line happens to say "next steps". The vendor is worth less than
    /// the floor on its own, so any weight at all on the coincidence is the weight that decides
    /// it - and it would have filed the message against Next.
    /// </remarks>
    [Fact]
    public void An_employer_named_by_an_ordinary_word_is_not_matched_on_a_subject_line()
    {
        var result = EmailSubmissionMatcher.Match(
            Message(
                subject: "Next steps on your application",
                senderDomain: "myworkdayjobs.com",
                senderVendor: "Workday"),
            [Application(41, "Next", vendor: "Workday")]);

        Assert.Equal(EmailMatchOutcome.NotConfident, result.Outcome);
        Assert.Null(result.Match);

        // Seen, and deliberately not counted. Both signals are on the record so that the reason
        // is readable afterwards rather than inferred from an unexpectedly low number.
        Assert.Contains(EmailMatchSignal.CompanyInSubject, result.Ranked[0].Signals);
        Assert.Contains(EmailMatchSignal.CompanyIsOrdinaryWord, result.Ranked[0].Signals);
        Assert.Contains(EmailMatchSignal.SenderVendorMatchesApplyVendor, result.Ranked[0].Signals);
    }

    /// <summary>The same message shape, with a name that is a name.</summary>
    [Fact]
    public void A_distinctive_employer_name_in_the_same_place_does_match()
    {
        var result = EmailSubmissionMatcher.Match(
            Message(
                subject: "Acme Robotics: next steps on your application",
                senderDomain: "myworkdayjobs.com",
                senderVendor: "Workday"),
            [Application(41, "Acme Robotics", vendor: "Workday")]);

        Assert.Equal(EmailMatchOutcome.Matched, result.Outcome);
        Assert.Equal(41, result.Match!.SubmissionId);
        Assert.DoesNotContain(EmailMatchSignal.CompanyIsOrdinaryWord, result.Match.Signals);
    }

    [Fact]
    public void A_message_that_matches_nothing_abstains_rather_than_taking_the_only_candidate()
    {
        var result = EmailSubmissionMatcher.Match(
            Message(
                subject: "Your order has shipped",
                senderName: "Parcel Tracking",
                senderDomain: "shipping.example.com"),
            [Application(41, "Acme Robotics", applyHost: "boards.greenhouse.io", vendor: "Greenhouse")]);

        // Not NotConfident: nothing pointed at it at all, and a client should file this message
        // somewhere else entirely rather than show somebody a shortlist of one.
        Assert.Equal(EmailMatchOutcome.NoEvidence, result.Outcome);
        Assert.Null(result.Match);
        Assert.Empty(result.Ranked);
    }

    /// <summary>
    /// The sending system is one fact, however many ways it can be observed.
    /// </summary>
    /// <remarks>
    /// The apply URL's host being Greenhouse and its vendor being Greenhouse are the same
    /// observation written twice. Summed they would clear the floor with nothing about the
    /// employer in evidence at all, which would file every Greenhouse acknowledgement against
    /// whichever Greenhouse application happened to be first in the list.
    /// </remarks>
    [Fact]
    public void The_sending_system_alone_never_reaches_the_confidence_floor()
    {
        var result = EmailSubmissionMatcher.Match(
            Message(
                subject: "We have received your application",
                senderDomain: "boards.greenhouse.io",
                senderVendor: "Greenhouse"),
            [Application(41, "Acme Robotics", applyHost: "boards.greenhouse.io", vendor: "Greenhouse")]);

        Assert.Equal(EmailMatchOutcome.NotConfident, result.Outcome);
        Assert.Null(result.Match);
        Assert.Contains(EmailMatchSignal.SenderHostMatchesApplyHost, result.Ranked[0].Signals);
        Assert.Contains(EmailMatchSignal.SenderVendorMatchesApplyVendor, result.Ranked[0].Signals);
        Assert.True(result.Ranked[0].Confidence < EmailSubmissionMatcher.MatchFloor);
    }

    /// <summary>
    /// A signal every candidate carries picks none of them out.
    /// </summary>
    /// <remarks>
    /// Measured against the candidate set rather than against a list of known-generic domains,
    /// which is what makes it right for a vendor nobody has heard of yet. The same message,
    /// against a set where only one application went through that vendor, is worth more.
    /// </remarks>
    [Fact]
    public void Sender_evidence_shared_by_every_candidate_counts_for_less_than_evidence_that_is_not()
    {
        var message = Message(
            subject: "Interview invitation for the Acme Robotics role",
            senderDomain: "greenhouse.io",
            senderVendor: "Greenhouse");

        var shared = EmailSubmissionMatcher.Match(
            message,
            [
                Application(41, "Acme Robotics", vendor: "Greenhouse"),
                Application(42, "Umbrella Health", vendor: "Greenhouse"),
            ]);

        var distinguishing = EmailSubmissionMatcher.Match(
            message,
            [
                Application(41, "Acme Robotics", vendor: "Greenhouse"),
                Application(42, "Umbrella Health", vendor: "Workday"),
            ]);

        Assert.Equal(41, shared.Match!.SubmissionId);
        Assert.Equal(41, distinguishing.Match!.SubmissionId);

        Assert.Contains(EmailMatchSignal.SenderEvidenceSharedWithOtherCandidates, shared.Match.Signals);
        Assert.DoesNotContain(EmailMatchSignal.SenderEvidenceSharedWithOtherCandidates, distinguishing.Match.Signals);
        Assert.True(shared.Match.Confidence < distinguishing.Match.Confidence);

        // The employer the message never named is still ranked where the vendor agreed with it,
        // because that is exactly the fact a person needs in order to overrule this.
        Assert.Equal(2, shared.Ranked.Count);
        Assert.Single(distinguishing.Ranked);
    }

    /// <summary>
    /// A message cannot be a reply to an application that did not exist when it arrived.
    /// </summary>
    /// <remarks>
    /// Ruling out is the safe direction and the abstention is the cost of it: somebody importing
    /// last year's applications today does get last year's messages ruled out, which is a worse
    /// answer than a person would give and a better one than filing a rejection against a job
    /// that had not been applied for.
    /// </remarks>
    [Fact]
    public void An_application_created_after_the_message_arrived_is_not_a_candidate_for_it()
    {
        var result = EmailSubmissionMatcher.Match(
            Message(senderName: "Acme Robotics Careers"),
            [Application(41, "Acme Robotics", createdAtUtc: Received.AddDays(2))]);

        Assert.Equal(EmailMatchOutcome.NoEvidence, result.Outcome);
        Assert.Empty(result.Ranked);
    }

    [Fact]
    public void An_application_created_within_the_grace_of_the_message_is_still_a_candidate()
    {
        // The grace is for sloppy clocks and date-only timestamps, not for backfilled history.
        var result = EmailSubmissionMatcher.Match(
            Message(senderName: "Acme Robotics Careers"),
            [Application(41, "Acme Robotics", createdAtUtc: Received + (EmailSubmissionMatcher.CreationGrace / 2))]);

        Assert.Equal(EmailMatchOutcome.Matched, result.Outcome);
        Assert.Equal(41, result.Match!.SubmissionId);
    }

    /// <summary>
    /// The one input this function refuses outright.
    /// </summary>
    /// <remarks>
    /// Recruiter addresses are discarded at parse time because this repository is public. A
    /// matcher that helpfully trimmed the local part off would be the route by which one came
    /// back, and it would arrive by way of a caller who believed it was passing a domain.
    /// </remarks>
    [Fact]
    public void A_sender_address_is_refused_where_a_domain_was_asked_for()
    {
        var refused = Assert.Throws<ArgumentException>(() => EmailSubmissionMatcher.Match(
            Message(senderName: "Acme Robotics", senderDomain: "recruiter@acme-robotics.com"),
            [Application(41, "Acme Robotics")]));

        Assert.Equal("tokens", refused.ParamName);
    }

    /// <summary>
    /// The token type's shape is the contract, so a field for the message is a red build.
    /// </summary>
    /// <remarks>
    /// A note in this system is bounded text a person reads. The failure this pins is somebody
    /// adding <c>Body</c> or <c>Snippet</c> here "just for matching", after which a pasted
    /// recruiter message - a name, a direct line, a signature block - is one field away from
    /// being written into a database that holds none of that today.
    /// </remarks>
    [Fact]
    public void The_token_type_has_nowhere_to_put_a_message_body()
    {
        var fields = typeof(EmailIdentityTokens)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal);

        Assert.Equal(
            [
                "CompanyMentions",
                "ReceivedAtUtc",
                "SenderAtsVendor",
                "SenderDisplayName",
                "SenderDomain",
                "Subject",
            ],
            fields);
    }

    [Fact]
    public void A_legal_form_on_the_posting_does_not_stop_the_employer_matching()
    {
        // The posting says "Acme Robotics Ltd" and the message says "Acme Robotics", every time.
        var result = EmailSubmissionMatcher.Match(
            Message(senderName: "Acme Robotics Careers"),
            [Application(41, "Acme Robotics Ltd")]);

        Assert.Equal(EmailMatchOutcome.Matched, result.Outcome);
        Assert.Equal(41, result.Match!.SubmissionId);
    }

    [Fact]
    public void An_employer_name_scattered_through_a_sentence_is_not_a_match()
    {
        // Both words are there and the employer is not. A name split across a sentence is a
        // coincidence, and this file exists to refuse coincidences.
        var result = EmailSubmissionMatcher.Match(
            Message(subject: "Acme is hiring robotics engineers this autumn"),
            [Application(41, "Acme Robotics")]);

        Assert.Equal(EmailMatchOutcome.NoEvidence, result.Outcome);
    }

    [Fact]
    public void A_subdomain_of_the_sending_domain_is_the_same_employer()
    {
        var result = EmailSubmissionMatcher.Match(
            Message(senderName: "Acme Robotics Recruitment", senderDomain: "acme-robotics.co.uk"),
            [Application(41, "Acme Robotics", applyHost: "jobs.acme-robotics.co.uk")]);

        Assert.Equal(EmailMatchOutcome.Matched, result.Outcome);
        Assert.Contains(EmailMatchSignal.SenderHostMatchesApplyHost, result.Match!.Signals);
        Assert.Equal(EmailSubmissionMatcher.Ceiling, result.Match.Confidence, 2);
    }

    /// <summary>
    /// Sharing a public suffix is not sharing an employer.
    /// </summary>
    /// <remarks>
    /// Without the guard, "the shorter domain is a suffix of the longer" reads <c>co.uk</c> as an
    /// organisation and agrees every British employer with every other one - which would put a
    /// company the message never mentioned into the ranking on the strength of its country.
    /// </remarks>
    [Fact]
    public void Two_employers_under_one_public_suffix_do_not_share_a_domain()
    {
        var result = EmailSubmissionMatcher.Match(
            Message(senderName: "Acme Robotics", senderDomain: "acme-robotics.co.uk"),
            [
                Application(41, "Acme Robotics"),
                Application(42, "Umbrella Health", applyHost: "careers.umbrella-health.co.uk"),
            ]);

        Assert.Equal(EmailMatchOutcome.Matched, result.Outcome);
        Assert.Equal(41, result.Match!.SubmissionId);
        Assert.Single(result.Ranked);

        // And a bare public suffix, arriving as a sending domain, agrees with nothing at all.
        var bare = EmailSubmissionMatcher.Match(
            Message(senderDomain: "co.uk"),
            [Application(42, "Umbrella Health", applyHost: "careers.umbrella-health.co.uk")]);

        Assert.Equal(EmailMatchOutcome.NoEvidence, bare.Outcome);
    }

    [Fact]
    public void An_employer_the_caller_read_out_of_the_message_is_corroboration_rather_than_an_answer()
    {
        // A name an extractor found in a body is a claim about the message, and claims about the
        // message are what this function is here to weigh rather than to believe.
        var result = EmailSubmissionMatcher.Match(
            Message(subject: "Following up", mentions: ["Acme Robotics Ltd"]),
            [Application(41, "Acme Robotics")]);

        Assert.Equal(EmailMatchOutcome.NotConfident, result.Outcome);
        Assert.Null(result.Match);
        Assert.Contains(EmailMatchSignal.CompanyInMention, result.Ranked[0].Signals);
    }

    [Fact]
    public void Only_the_start_of_an_over_long_subject_is_read()
    {
        // A subject line is one line. Something arriving at six hundred characters is a caller
        // pasting a message into the only string-shaped hole the token type has.
        var buried = EmailSubmissionMatcher.Match(
            Message(subject: new string('x', 600) + " Acme Robotics"),
            [Application(41, "Acme Robotics")]);

        var stated = EmailSubmissionMatcher.Match(
            Message(subject: "Acme Robotics " + new string('x', 600)),
            [Application(41, "Acme Robotics")]);

        Assert.Equal(EmailMatchOutcome.NoEvidence, buried.Outcome);
        Assert.Contains(EmailMatchSignal.CompanyInSubject, stated.Ranked[0].Signals);
    }

    /// <summary>
    /// Two applications whose vendor is unknown do not share a vendor.
    /// </summary>
    /// <remarks>
    /// The vendor arrives as a string so that this file need not own the catalogue of them, and
    /// the cost is a caller writing an enum's <c>Unknown</c> into it. Read as agreement, the least
    /// informative fact in the system becomes its most decisive one.
    /// </remarks>
    [Fact]
    public void An_unresolved_vendor_does_not_agree_with_another_unresolved_vendor()
    {
        var result = EmailSubmissionMatcher.Match(
            Message(senderName: "Acme Robotics Careers", senderVendor: "Unknown"),
            [Application(41, "Acme Robotics", vendor: "Unknown")]);

        Assert.Equal(EmailMatchOutcome.Matched, result.Outcome);
        Assert.DoesNotContain(EmailMatchSignal.SenderVendorMatchesApplyVendor, result.Match!.Signals);
    }

    [Fact]
    public void Nothing_to_match_against_is_its_own_answer()
    {
        // Distinct from "nothing matched": an empty candidate set is a question the caller has
        // not asked properly, and the fix is a different query rather than a person's attention.
        var result = EmailSubmissionMatcher.Match(Message(senderName: "Acme Robotics Careers"), []);

        Assert.Equal(EmailMatchOutcome.NoCandidates, result.Outcome);
        Assert.Null(result.Match);
        Assert.Empty(result.Ranked);
    }

    /// <summary>
    /// Every name <see cref="AtsVendor"/> can produce is read the way this matcher intends.
    /// </summary>
    /// <remarks>
    /// The vendor crosses a module boundary as a string, so that this file need not own the
    /// catalogue of vendors - and the cost of that choice is a contract no compiler checks. This
    /// is the check. It walks the real enum rather than a list written out here, so a vendor
    /// added to <see cref="AtsVendorDetector"/> is either evidence or explicitly not, and cannot
    /// arrive as a fourth thing that happens to agree with itself.
    ///
    /// The failure it exists for is silent and one-directional: were <c>Unknown</c> to count,
    /// every pair of applications whose vendor nobody established would agree, making the least
    /// informative fact in the system its most decisive one.
    /// </remarks>
    [Theory]
    [MemberData(nameof(EveryAtsVendor))]
    public void A_vendor_that_names_no_vendor_is_not_evidence_that_two_things_agree(AtsVendor vendor)
    {
        var names = vendor.ToString();

        var result = EmailSubmissionMatcher.Match(
            Message(subject: "Your application", senderVendor: names),
            [Application(71, "Acme Robotics", vendor: names)]);

        var agreed = result.Ranked.Any(row => row.Signals.Contains(EmailMatchSignal.SenderVendorMatchesApplyVendor));

        var isRealVendor = vendor is not (AtsVendor.Unknown or AtsVendor.Other or AtsVendor.Aggregator);

        Assert.Equal(isRealVendor, agreed);
    }

    public static TheoryData<AtsVendor> EveryAtsVendor()
    {
        var data = new TheoryData<AtsVendor>();

        foreach (var vendor in Enum.GetValues<AtsVendor>())
        {
            data.Add(vendor);
        }

        return data;
    }
}
