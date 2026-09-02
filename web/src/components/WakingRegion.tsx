import type { PageId } from '../routing/route';

/**
 * What a region shows while the database it reads is waking up.
 *
 * <b>Per region, not per page.</b> Metrics and the model-call ledger come from Cosmos, which
 * does not pause; postings, matches, the profile, searches and submissions come from
 * serverless SQL, which does. That split is the whole reason the Briefing was moved to Cosmos
 * in the first place, and rendering one page-wide spinner throws the benefit away: half the
 * dashboard can answer while the other half waits.
 *
 * It says what still works, and links there. "Try again later" is not a state anybody can act
 * on; "the Briefing and Model calls are unaffected, here they are" is.
 */
export function WakingRegion({ what, onRetry, go }: {
  /** The thing that is missing, as a sentence subject: "Your shortlist". */
  what: string;
  onRetry: () => void;
  go?: (page: PageId) => void;
}) {
  return (
    <div className="waking">
      <h2>The database did not respond in time</h2>
      <p>
        {what} is served from SQL, which pauses when it is idle and can take up to a minute to
        wake. If this was the first request in a while, retrying usually succeeds.
      </p>

      <button className="btn primary" onClick={onRetry}>Retry</button>

      {go && (
        <p className="waking-aside">
          Metrics are unaffected — they come from Cosmos, which does not pause. The{' '}
          <button className="linkish" onClick={() => go('briefing')}>Briefing</button> and{' '}
          <button className="linkish" onClick={() => go('calls')}>Model calls</button> still
          work.
        </p>
      )}
    </div>
  );
}

/**
 * The same region, still loading.
 *
 * Separate from the waking state on purpose: "this is taking a while because the database was
 * asleep" and "this has not come back yet" are different facts, and the first one is only
 * worth saying once it is true.
 */
export function LoadingRegion({ what }: { what: string }) {
  return <div className="empty">Loading {what.toLowerCase()}…</div>;
}
