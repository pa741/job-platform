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
}
