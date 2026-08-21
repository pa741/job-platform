namespace JobPlatform.Data.Sql.Entities;

/// <summary>
/// Which searches turned up a posting, and when each of them last did.
/// </summary>
/// <remarks>
/// A posting is one row keyed by <see cref="JobPostingEntity.SourceKey"/>, but the same
/// posting legitimately matches more than one configured search — a role can be both a
/// "software engineer" and a "python developer" hit. Attribution therefore cannot live on
/// the posting: a single <c>SearchTerm</c> column was overwritten by whichever search
/// ingested last, which dropped the posting out of the other term's list entirely.
///
/// The run ids are per term for the same reason. <c>BuildDailyRollupAsync</c> counts what a
/// day's runs surfaced by looking at which run first and last saw a posting; taken globally,
/// a posting first seen by one search would never count as new for any other, however long
/// that other search had been running.
/// </remarks>
public sealed class JobPostingSearchTerm
{
    public long PostingId { get; set; }

    /// <summary>Slug recovered from the blob name. Doubles as the Cosmos partition key.</summary>
    public required string SearchTerm { get; set; }

    /// <summary>The run of <em>this</em> search that first surfaced the posting.</summary>
    public int FirstSeenRunId { get; set; }

    /// <summary>The run of this search that last surfaced it.</summary>
    public int LastSeenRunId { get; set; }

    public DateTimeOffset FirstSeenUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }

    /// <summary>Runs of this search that have seen the posting.</summary>
    public int SeenCount { get; set; }

    public JobPostingEntity? Posting { get; set; }
    public ScrapeRun? FirstSeenRun { get; set; }
    public ScrapeRun? LastSeenRun { get; set; }
}
