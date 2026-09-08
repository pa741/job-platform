using System.Linq.Expressions;
using JobPlatform.Core.Model;
using JobPlatform.Data.Sql.Entities;

namespace JobPlatform.Data.Sql;

/// <summary>
/// <see cref="PostingAge.PostedSince"/> as the two predicates the database can run.
/// </summary>
/// <remarks>
/// <b>Two spellings of one rule, deliberately, and they are held together by test.</b> The corpus
/// search filters postings and the shortlist filters matches, so the same question is asked from
/// two roots; EF translates an expression tree rather than executing it, so neither query can
/// call <see cref="PostingAge.PostedSince"/> and neither can be written in terms of the other
/// without a rewriter nobody would want to read afterwards. The codebase has already made this
/// trade once, for the apply channel, and on the same terms: write it twice, put the two next to
/// each other, and let a test that runs both over the same rows be the thing that stops them
/// drifting. That test is <c>PostingRecencyTests</c>, and it asserts against the pure rule rather
/// than against the other spelling, so a drift in either is caught rather than only a
/// disagreement between them.
///
/// <b>The date is materialised before the lambda closes over it.</b> Doing the conversion inside
/// the expression would send a client-side call into the translator, which fails at runtime and
/// only for the provider that cannot do it.
/// </remarks>
public static class PostingRecency
{
    /// <summary>Postings at or after the cutoff, for the corpus search.</summary>
    public static Expression<Func<JobPostingEntity, bool>> Postings(DateTimeOffset cutoff)
    {
        var day = DateOnly.FromDateTime(cutoff.UtcDateTime);

        return p => (p.DatePosted != null && p.DatePosted >= day)
            || (p.DatePosted == null && p.FirstSeenUtc >= cutoff);
    }

    /// <summary>The same rule read through a match's posting, for the shortlist and the queue.</summary>
    public static Expression<Func<JobMatchEntity, bool>> Matches(DateTimeOffset cutoff)
    {
        var day = DateOnly.FromDateTime(cutoff.UtcDateTime);

        return m => (m.Posting!.DatePosted != null && m.Posting.DatePosted >= day)
            || (m.Posting!.DatePosted == null && m.Posting.FirstSeenUtc >= cutoff);
    }
}
