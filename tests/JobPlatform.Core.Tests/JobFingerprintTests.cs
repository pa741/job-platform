using JobPlatform.Core.Dedup;
using JobPlatform.Core.Model;
using Xunit;

namespace JobPlatform.Core.Tests;

/// <summary>
/// The two fingerprints, and why they are two.
/// </summary>
/// <remarks>
/// <c>ContentHash</c> answers "did this posting change", is stored on every row, and gates
/// embedding staleness. <c>CrossBoardKey</c> answers "is this the same job as that one".
/// Collapsing them is what broke the cross-board count: the stored hash folds in the raw
/// location string, boards write locations differently, and so it never collided across them.
/// </remarks>
public sealed class JobFingerprintTests
{
    private static JobPosting Posting(string title, string? company, string? location) => new()
    {
        Site = "linkedin",
        ExternalId = "x",
        Title = title,
        Company = company,
        Location = location,
        JobUrl = "https://example.invalid",
    };

    [Fact]
    public void The_stored_content_hash_still_does_not_cross_boards_and_must_not()
    {
        // Pinned deliberately. Widening ContentHash would mark every embedded posting stale -
        // EmbeddingRepository compares it - so the fix had to be a second key rather than a
        // change to this one, and a future "simplification" that merges them fails here.
        var a = JobFingerprint.ContentHash(Posting("Backend Engineer", "Northwind", "London, England, United Kingdom"));
        var b = JobFingerprint.ContentHash(Posting("Backend Engineer", "Northwind", "London, UK"));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void The_cross_board_key_matches_the_same_job_written_two_ways()
    {
        // The case the live corpus is full of and the stored hash could not see.
        var a = JobFingerprint.CrossBoardKey(Posting("Backend Engineer", "Northwind", "London, England, United Kingdom"));
        var b = JobFingerprint.CrossBoardKey(Posting("Backend  ENGINEER", "northwind.", "London, UK"));

        Assert.NotNull(a);
        Assert.Equal(a, b);
    }

    [Fact]
    public void One_title_at_one_employer_in_two_cities_is_two_jobs()
    {
        // Measured on the live corpus: title and employer alone matched 285 postings, and
        // adding the city left 211 - so 74 were this. Merging them would hand somebody the
        // apply link for a vacancy in the wrong city, which is worse than having no link.
        var london = JobFingerprint.CrossBoardKey(Posting("Backend Engineer", "Northwind", "London, UK"));
        var dublin = JobFingerprint.CrossBoardKey(Posting("Backend Engineer", "Northwind", "Dublin, IE"));

        Assert.NotEqual(london, dublin);
    }

    [Fact]
    public void A_posting_with_no_city_or_no_employer_has_no_cross_board_identity()
    {
        // Null rather than a key built from what is present. Two unlocated postings matching
        // each other is the collision above with nothing left to prevent it.
        Assert.Null(JobFingerprint.CrossBoardKey(Posting("Backend Engineer", "Northwind", null)));
        Assert.Null(JobFingerprint.CrossBoardKey(Posting("Backend Engineer", "Northwind", "   ")));
        Assert.Null(JobFingerprint.CrossBoardKey(Posting("Backend Engineer", null, "London, UK")));
    }

    /// <summary>
    /// The four ways the boards spell one city are one city.
    /// </summary>
    /// <remarks>
    /// Measured on the live corpus: London 4,323, London Area 1,542, Greater London 322, City Of
    /// London 66 - "London Area" is LinkedIn's spelling and "Greater London" Indeed's. Cloudflare's
    /// VoidZero Engineer reached the shortlist twice on nothing but that difference, which is the
    /// case this pins.
    /// </remarks>
    [Theory]
    [InlineData("London, UK")]
    [InlineData("London Area, United Kingdom")]
    [InlineData("Greater London, England, United Kingdom")]
    [InlineData("City Of London, England")]
    public void One_city_written_four_ways_is_one_job(string location)
    {
        var canonical = JobFingerprint.CrossBoardKey(Posting("Backend Engineer", "Northwind", "London, UK"));

        Assert.Equal(canonical, JobFingerprint.CrossBoardKey(Posting("Backend Engineer", "Northwind", location)));
    }

    /// <summary>The folding is by shape, so it holds for cities nobody wrote a rule for.</summary>
    [Fact]
    public void The_same_folding_works_for_a_city_no_rule_names()
    {
        var manchester = JobFingerprint.CrossBoardKey(Posting("Backend Engineer", "Northwind", "Manchester, UK"));

        Assert.Equal(manchester, JobFingerprint.CrossBoardKey(
            Posting("Backend Engineer", "Northwind", "Greater Manchester, United Kingdom")));
    }

    /// <summary>
    /// Seniority is part of the title and stays there, however tempting the reference number is.
    /// </summary>
    /// <remarks>
    /// Harnham advertised requisition 197637 four times - Junior, plain, Senior and Lead - and the
    /// proposal that asked for clustering read the middle two as one job listed twice. They are a
    /// ladder, and merging them hides a rung somebody might have wanted. There are 127 such pairs
    /// in the corpus, against the 74 bad merges that were reason enough to keep the city required.
    /// </remarks>
    [Fact]
    public void A_seniority_ladder_at_one_employer_is_not_one_job()
    {
        var mid = JobFingerprint.CrossBoardKey(
            Posting("C# Full Stack Engineer (197637)", "Harnham", "London Area, United Kingdom"));

        var senior = JobFingerprint.CrossBoardKey(
            Posting("Senior C# Full Stack Engineer (197637)", "Harnham", "London Area, United Kingdom"));

        Assert.NotEqual(mid, senior);
    }
}
