using JobPlatform.Core.Applications;

namespace JobPlatform.Ingestion.Ats;

/// <summary>
/// Reads one employer's public board from one vendor's applicant tracking system.
/// </summary>
/// <remarks>
/// <b>One interface over four differently shaped JSON documents, because everything above this
/// line has to be able to not care.</b> Greenhouse answers <c>{"jobs":[{"absolute_url":...}]}</c>,
/// Lever answers a bare array whose title lives in <c>text</c>, Ashby and SmartRecruiters answer
/// two more shapes again - and <c>AtsListingMatcher</c>, which decides which vacancy a person is
/// sent to, must be assertable against a fixture with no network in the room. So the vendor's shape
/// stops here and what leaves is <c>AtsListing</c>: title, place, apply URL, and nothing else.
///
/// <b>Implementations may not throw.</b> A vendor being down is a pass that recovers fewer links,
/// never a pass that fails - the same discipline <c>ScraperConfigPublisher</c> follows for a failed
/// publish and <c>AiCallLogRepository</c> for a failed record. Every failure this can meet is a
/// value: a 404 is <see cref="AtsBoardReadOutcome.NotABoard"/> and everything else is
/// <see cref="AtsBoardReadOutcome.Unavailable"/>. <see cref="AtsBoardClient"/> holds that net in one
/// place so four vendors cannot each forget a different half of it.
///
/// <b>Nothing an implementation sends may carry a credential, a cookie or a session, on any
/// host.</b> These five vendors publish these boards <i>to job seekers</i>, unauthenticated and
/// documented, and that is the entire reason reading them is a different kind of act from driving a
/// signed-in page - see <c>mcp_handoff.md</c> 3.2 and 3.2a, where the authenticated route is closed
/// rather than merely unbuilt. A change here that seems to need a header with a secret in it is the
/// design being worked around.
/// </remarks>
public interface IAtsBoardClient
{
    /// <summary>Whose applicant tracking system this reads. One client per vendor.</summary>
    AtsVendor Vendor { get; }

    /// <summary>
    /// Everything that board publishes, or why nothing came back.
    /// </summary>
    /// <remarks>
    /// <b>One call per board per pass, never one per posting.</b> A board answers its whole
    /// catalogue in a single request - Cloudflare's Greenhouse returned 333 jobs on 2026-09-07 -
    /// so asking per posting would be 333 requests for the same bytes, against an API that is a
    /// service to that vendor's paying customers rather than to us. <see cref="AtsBoardReader"/>
    /// is what makes that true across a pass; this is what makes it true per employer.
    /// </remarks>
    Task<AtsBoardRead> ReadAsync(AtsBoard board, CancellationToken cancellationToken = default);
}
