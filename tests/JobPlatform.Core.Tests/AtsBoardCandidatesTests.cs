using JobPlatform.Core.Applications;
using Xunit;

namespace JobPlatform.Core.Tests;

/// <summary>
/// Which board tokens are worth a request, and which boards may be believed.
/// </summary>
/// <remarks>
/// The cases worth pinning are the measured ones. Three names resolved correctly on the live
/// corpus - "Capital on Tap", "Orbital Industries" and "Eucalyptus" - and four resolved to
/// strangers' boards - "Dex", "Fin", "Kernel" and "Orbital". Every assertion here is one of those,
/// a shape that would silently reintroduce one of them, or the boundary a constant sits on.
///
/// Two of them do double duty. <c>For</c> refusing "Orbital" and <c>Confirm</c> refusing a board
/// called "Orbital" are the same floor read twice, and the test that names both is what would fail
/// if somebody relaxed one of them alone.
/// </remarks>
public sealed class AtsBoardCandidatesTests
{
    [Theory]
    [InlineData("Capital on Tap", "capitalontap")]
    [InlineData("Orbital Industries", "orbitalindustries")]
    [InlineData("Eucalyptus", "eucalyptus")]
    [InlineData("Cloudflare", "cloudflare")]
    public void For_leads_with_the_token_that_resolved_against_the_live_corpus(string company, string expected)
        => Assert.Equal(expected, AtsBoardCandidates.For(company)[0]);

    [Fact]
    public void For_offers_the_concatenated_spelling_before_the_hyphenated_one()
    {
        // A prior rather than a measurement, and written down here so it can be re-derived from a
        // corpus of resolved tokens if one ever exists. Greenhouse and Ashby tokens are
        // overwhelmingly run together.
        Assert.Equal(
            ["capitalontap", "capital-on-tap"],
            AtsBoardCandidates.For("Capital on Tap"));
    }

    [Fact]
    public void For_returns_one_candidate_for_a_one_word_name()
    {
        // The hyphenated and concatenated spellings of a single word are the same string, and a
        // caller should not spend a request on the second one discovering that.
        Assert.Equal(["eucalyptus"], AtsBoardCandidates.For("Eucalyptus"));
    }

    [Theory]
    [InlineData("Monzo Bank Ltd")]
    [InlineData("Monzo Bank Limited")]
    [InlineData("Monzo Bank, Inc.")]
    [InlineData("MONZO BANK PLC")]
    public void For_strips_a_legal_form_and_never_offers_it_back(string company)
    {
        // Nobody registers monzobankltd, so a legal form is noise in every candidate rather than a
        // spelling worth a second request.
        Assert.Equal(["monzobank", "monzo-bank"], AtsBoardCandidates.For(company));
    }

    [Fact]
    public void For_offers_the_untrimmed_form_behind_a_generic_tail()
    {
        // Which of the two is right is not knowable from the name: "Deliveroo Group" is deliveroo
        // on its board, and "Orbital Labs" is plausibly orbitallabs on its own. So both are tried,
        // stripped first.
        Assert.Equal(
            ["deliveroo", "deliveroogroup", "deliveroo-group"],
            AtsBoardCandidates.For("Deliveroo Group plc"));
    }

    [Fact]
    public void For_keeps_a_generic_tail_when_stripping_it_leaves_a_word_too_common_to_probe()
    {
        // The stripped form is dropped rather than the whole name: "acme" is four letters of
        // common noun, and "orbital" is one of the four measured false positives.
        Assert.Equal(
            ["acmetechnologies", "acme-technologies"],
            AtsBoardCandidates.For("Acme Technologies Ltd"));

        Assert.Equal(
            ["orbitallabs", "orbital-labs"],
            AtsBoardCandidates.For("Orbital Labs"));
    }

    [Theory]
    [InlineData("Dex")]
    [InlineData("Fin")]
    [InlineData("Kernel")]
    [InlineData("Orbital")]
    [InlineData("IBM")]
    [InlineData("Acme")]
    [InlineData("Monzo")]
    public void For_yields_nothing_for_a_name_too_short_or_too_common_to_probe(string company)
    {
        // The four measured false positives, an initialism, and two names this deliberately gives
        // up on. "Monzo" is a real employer and a real board; five letters is not evidence, and
        // the learned path - a token lifted from a link already held - is how they are reached.
        Assert.Empty(AtsBoardCandidates.For(company));
    }

    [Theory]
    [InlineData("Orbital Industries", "orbital")]
    [InlineData("Dex Media", "dex")]
    [InlineData("Kernel Analytics", "kernel")]
    public void For_never_truncates_a_multi_word_name_to_its_first_word(string company, string forbidden)
    {
        // The whole difference between a recovery and a misdirected application. Each of these
        // first words is a real board belonging to somebody, so a helpful extra candidate here is
        // an application sent to a company the candidate never applied to.
        var candidates = AtsBoardCandidates.For(company);

        Assert.NotEmpty(candidates);
        Assert.DoesNotContain(forbidden, candidates);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ltd")]
    [InlineData("Limited")]
    [InlineData("- , . -")]
    [InlineData("株式会社")]
    public void For_returns_nothing_when_there_is_no_name_left_to_read(string? company)
    {
        // A name that is nothing but a legal form, or that folds away entirely because it is
        // written in a script with no ASCII form, leaves no token to guess at. Half a token is
        // worse than none.
        Assert.Empty(AtsBoardCandidates.For(company));
    }

    [Fact]
    public void For_folds_case_accents_and_punctuation()
    {
        // An ASCII fold rather than JobFingerprint's, because the output is a URL path segment.
        // The accent is dropped on its own rather than taking its letter with it.
        Assert.Equal(
            ["societegenerale", "societe-generale"],
            AtsBoardCandidates.For("Société Générale"));

        Assert.Equal("capitalontap", AtsBoardCandidates.For("  CAPITAL   on/Tap!  ")[0]);

        // The decomposed spelling of the same name has to fold to the same token, or a board
        // resolves for one board's copy of an employer's adverts and not for another's. The
        // second literal below is the decomposed spelling and is indistinguishable from the first
        // on screen - that is the point of it, and it is why this pair cannot be eyeballed.
        Assert.Equal(
            AtsBoardCandidates.For("Société Générale"),
            AtsBoardCandidates.For("Société Générale"));
    }

    [Fact]
    public void For_reads_an_ampersand_as_the_word_it_is_spoken_as()
    {
        // Every other normaliser here folds an ampersand to a separator, and it is wrong for this
        // one output: benjerrys and bq are tokens nobody has registered.
        Assert.Equal(["benandjerrys", "ben-and-jerrys"], AtsBoardCandidates.For("Ben & Jerry's"));
        Assert.Equal(["bandq", "b-and-q"], AtsBoardCandidates.For("B&Q"));
    }

    [Fact]
    public void For_keeps_an_apostrophe_from_splitting_a_word_in_two()
    {
        // The same argument as ApplicationPackFile's: Jerry's is one word, and separating on the
        // apostrophe produces ben-and-jerry-s.
        Assert.Equal(
            AtsBoardCandidates.For("Ben & Jerrys"),
            AtsBoardCandidates.For("Ben & Jerry’s"));
    }

    [Fact]
    public void For_bounds_the_list_to_what_a_strangers_api_should_be_asked_for()
    {
        // Four is the most the rules can produce, and it is a promise rather than a truncation: a
        // caller sizing a budget multiplies this by the vendors it tries.
        Assert.Equal(
            [
                "capitalontap",
                "capital-on-tap",
                "capitalontaptechnologies",
                "capital-on-tap-technologies",
            ],
            AtsBoardCandidates.For("Capital on Tap Technologies"));

        Assert.Equal(AtsBoardCandidates.MaxCandidates, AtsBoardCandidates.For("Capital on Tap Technologies").Count);
    }

    [Fact]
    public void For_refuses_a_name_too_long_to_be_a_board_token()
    {
        // Dropped whole rather than truncated. A prefix of a company's name is a plausible
        // different company's name, so truncating manufactures the very collision the floors
        // refuse.
        Assert.Single(AtsBoardCandidates.For(new string('a', AtsBoardCandidates.MaximumLength)));
        Assert.Empty(AtsBoardCandidates.For(new string('a', AtsBoardCandidates.MaximumLength + 1)));
    }

    [Fact]
    public void Confirm_defaults_to_refusing()
    {
        // The one property this enum's numbering exists for: a value nobody set - a struct
        // default, a column added later, a deserialiser that saw no member - costs a recovered
        // link rather than sending a covering letter to a stranger.
        Assert.Equal(AtsBoardConfidence.Unconfirmed, default(AtsBoardConfidence));
    }

    [Theory]
    [InlineData("Cloudflare", "Cloudflare")]
    [InlineData("Capital on Tap", "Capital on Tap Ltd")]
    [InlineData("Capital on Tap Ltd", "Capital on Tap")]
    [InlineData("Société Générale", "Societe Generale")]
    [InlineData("Acme Technologies Ltd", "Acme")]
    public void Confirm_accepts_a_board_that_names_the_employer_on_the_advert(string company, string board)
    {
        // Both sides are folded identically, tails included. A board writes "Ltd" as often as an
        // advert omits it, and the last pair is one company shortening its own name past a
        // descriptor - which is not the same thing as two companies sharing a word.
        Assert.Equal(AtsBoardConfidence.NameAgrees, AtsBoardCandidates.Confirm(company, board));
    }

    [Fact]
    public void Confirm_raises_its_answer_when_the_board_carries_the_posting_the_advert_described()
    {
        // The live verification of 2026-09-07: a Greenhouse board answering to "Cloudflare" that
        // also lists VoidZero Engineer, the posting held here as 3020 and the one LinkedIn
        // withheld a URL for.
        Assert.Equal(
            AtsBoardConfidence.NameAndPostingAgree,
            AtsBoardCandidates.Confirm(
                "Cloudflare",
                "Cloudflare",
                "VoidZero Engineer",
                ["Account Executive", "VoidZero Engineer, London"]));

        // ...and the decoration sits on either side of the match.
        Assert.Equal(
            AtsBoardConfidence.NameAndPostingAgree,
            AtsBoardCandidates.Confirm(
                "Cloudflare",
                "Cloudflare",
                "Senior Platform Engineer (Remote)",
                ["Senior Platform Engineer"]));
    }

    [Fact]
    public void Confirm_stays_at_a_name_match_when_the_board_lists_no_such_posting()
    {
        // A missing title never lowers the answer. A board that closed the vacancy is still that
        // employer's board, and the advert this system holds may be days older than the listing.
        Assert.Equal(
            AtsBoardConfidence.NameAgrees,
            AtsBoardCandidates.Confirm("Cloudflare", "Cloudflare", "VoidZero Engineer", ["Account Executive"]));

        Assert.Equal(
            AtsBoardConfidence.NameAgrees,
            AtsBoardCandidates.Confirm("Cloudflare", "Cloudflare", "VoidZero Engineer", []));
    }

    [Theory]
    [InlineData("Dex Media", "Dex Labs")]
    [InlineData("Orbital Industries", "Orbital")]
    [InlineData("Cloudflare", "Cloudfare Media")]
    [InlineData("Capital on Tap", "Capital One")]
    public void Confirm_refuses_a_board_that_names_somebody_else(string company, string board)
    {
        // The function's whole job. A probe answers 200 for whoever owns the token, and a 200 read
        // as a confirmation is how an application reaches a company the candidate never applied to.
        // The last pair is the one a per-character prefix test would wave through: "Capital One"
        // and "Capital on Tap" share five letters and disagree on the second word.
        Assert.Equal(AtsBoardConfidence.Unconfirmed, AtsBoardCandidates.Confirm(company, board));
    }

    [Fact]
    public void Confirm_accepts_a_distinctive_prefix_and_leaves_the_rest_to_the_posting_title()
    {
        // The known residual, pinned rather than hidden. An advert saying "Cloudflare" against a
        // board calling itself "Cloudflare Capital Partners" agrees here, and it is not fixable
        // from the names: the identical shape is "Monzo" against "Monzo Bank", which is one
        // company. So the names give NameAgrees and nothing more...
        Assert.Equal(
            AtsBoardConfidence.NameAgrees,
            AtsBoardCandidates.Confirm("Cloudflare", "Cloudflare Capital Partners LLP"));

        // ...and it is the posting that separates them, which is the whole reason
        // NameAndPostingAgree is a bar a cautious caller can raise to rather than decoration.
        Assert.Equal(
            AtsBoardConfidence.NameAgrees,
            AtsBoardCandidates.Confirm(
                "Cloudflare",
                "Cloudflare Capital Partners LLP",
                "VoidZero Engineer",
                ["Investment Associate", "Fund Controller"]));

        Assert.Equal(
            AtsBoardConfidence.NameAndPostingAgree,
            AtsBoardCandidates.Confirm(
                "Cloudflare",
                "Cloudflare, Inc.",
                "VoidZero Engineer",
                ["VoidZero Engineer"]));
    }

    [Theory]
    [InlineData("Cloudflare", null)]
    [InlineData("Cloudflare", "   ")]
    [InlineData(null, "Cloudflare")]
    public void Confirm_refuses_a_board_that_will_not_name_itself(string? company, string? board)
    {
        // Lever's public postings feed carries no company name, so on this evidence a Lever board
        // is never confirmed. That is a stated cost, not an oversight: the fix is to pass the name
        // from wherever that vendor does publish one, and never the token - which For built out of
        // the company name, so comparing it back would confirm every probe including every wrong
        // one.
        Assert.Equal(AtsBoardConfidence.Unconfirmed, AtsBoardCandidates.Confirm(company, board));

        // ...and a posting match does not rescue it either.
        Assert.Equal(
            AtsBoardConfidence.Unconfirmed,
            AtsBoardCandidates.Confirm(company, board, "VoidZero Engineer", ["VoidZero Engineer"]));
    }

    [Fact]
    public void Confirm_will_not_let_a_title_rescue_a_name_that_disagrees()
    {
        // Titles are generic - "Senior Platform Engineer" is on every board there is - so a title
        // match on a stranger's board is not weak evidence, it is the collision wearing evidence's
        // clothes.
        Assert.Equal(
            AtsBoardConfidence.Unconfirmed,
            AtsBoardCandidates.Confirm(
                "Orbital Industries",
                "Orbital",
                "Senior Platform Engineer",
                ["Senior Platform Engineer", "Staff Engineer"]));
    }

    [Fact]
    public void Confirm_needs_more_than_one_word_of_title_to_corroborate()
    {
        // One generic noun is satisfied by every board on earth, so it may not move the answer.
        Assert.Equal(
            AtsBoardConfidence.NameAgrees,
            AtsBoardCandidates.Confirm("Cloudflare", "Cloudflare", "Engineer", ["Senior Engineer"]));
    }

    [Fact]
    public void Confirm_matches_a_title_on_whole_words_and_not_on_a_substring()
    {
        // The discipline AtsVendorDetector.IsAtOrUnder applies to a host, applied to a title:
        // "Data Engineer" is not "Data Engineering Manager", and an unguarded Contains says it is.
        Assert.Equal(
            AtsBoardConfidence.NameAgrees,
            AtsBoardCandidates.Confirm("Cloudflare", "Cloudflare", "Data Engineer", ["Data Engineering Manager"]));
    }

    [Theory]
    [InlineData("Dex")]
    [InlineData("Fin")]
    [InlineData("Kernel")]
    [InlineData("Orbital")]
    public void Confirm_rests_a_prefix_agreement_on_the_same_floor_that_decides_what_is_probed(string word)
    {
        // One rule with two readers, asserted together so that relaxing either one alone is a red
        // build. These four words are the measured false positives: each is a real board belonging
        // to somebody, so neither half of this class may treat one as naming an employer.
        Assert.Empty(AtsBoardCandidates.For(word));

        Assert.Equal(
            AtsBoardConfidence.Unconfirmed,
            AtsBoardCandidates.Confirm(word + " Industries", word));
    }
}
