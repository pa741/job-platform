import { useCallback, useEffect, useState } from 'react';
import type { JobPlatformApi } from '../api/client';
import type { AiCallResponse, AiCallTotalsResponse } from '../api/types';
import { Card, ErrorNote, StatTile } from '../components/Primitives';

/**
 * What the model was asked to do, and what came back.
 *
 * **The failures are the subject.** Every AI path in this system degrades silently by design —
 * a provider failure must not take down endpoints with nothing to do with AI — and for a long
 * time that was implemented as recording nothing at all. A nightly sweep sent 90 pairs and wrote
 * 40, with no exception, no error and a success report; a backfill spent its calls on HTTP 429s
 * and extracted almost nothing. Both showed up as a count nobody was comparing to anything.
 *
 * So this page shows failures first and pairs requested with returned everywhere. **A returned
 * count on its own cannot show a loss** — 40 assessments reads as a small night whether 40 or 90
 * were paid for, which is precisely how a 55% loss went unnoticed for a day.
 *
 * No chart. The question here is "what broke and what did it cost", which is a number and a
 * list; a time series would be decoration over ten rows.
 */
export function AiCalls({ api }: { api: JobPlatformApi }) {
  return (
    <div className="grid">
      <Totals api={api} />
      <Failures api={api} />
    </div>
  );
}

const WINDOW_DAYS = 7;

function Totals({ api }: { api: JobPlatformApi }) {
  const [totals, setTotals] = useState<AiCallTotalsResponse[]>();
  const [error, setError] = useState<unknown>();

  const load = useCallback(() => {
    setError(undefined);
    api.aiCallSummary(WINDOW_DAYS).then((r) => setTotals(r.items)).catch(setError);
  }, [api]);

  useEffect(load, [load]);

  if (error) {
    return <ErrorNote error={error} onRetry={load} />;
  }

  if (!totals) {
    return <div className="empty">Loading…</div>;
  }

  if (totals.length === 0) {
    return (
      <Card title="Model calls" subtitle={`Last ${WINDOW_DAYS} days`}>
        <div className="empty">
          No model calls recorded. Either nothing has run, or no AI provider is configured —
          the platform still scores and ranks without one, just without the judgement layer.
        </div>
      </Card>
    );
  }

  return (
    <Card
      title="Model calls"
      subtitle={`Last ${WINDOW_DAYS} days, by pass`}
      actions={<button className="btn" onClick={load}>Refresh</button>}
    >
      {totals.map((t) => (
        <section key={t.operation} style={{ marginBottom: 20 }}>
          <h3 style={{ margin: '0 0 8px' }}>{t.operation}</h3>

          <div className="grid kpi">
            <StatTile label="Calls" value={t.calls} hint={`${t.failedCalls} lost something`} />
            <StatTile label="Sent" value={t.requested} hint="What these calls cost" />
            <StatTile label="Usable" value={t.returned} hint="What came back" />
            <StatTile
              label="Discarded"
              value={t.discarded}
              hint={t.requested > 0
                ? `${Math.round((t.discarded / t.requested) * 100)}% of what was paid for`
                : undefined}
            />
            {/* Duration is not cost. A batch of ten adverts and a batch of one differ by an
                order of magnitude in tokens and barely at all in wall clock. */}
            <StatTile
              label="Tokens"
              value={t.totalTokens.toLocaleString()}
              hint={t.reasoningTokens > 0
                ? `${Math.round((t.reasoningTokens / t.totalTokens) * 100)}% spent reasoning`
                : 'none reported as reasoning'}
            />
          </div>
        </section>
      ))}

      <p className="muted">
        Discarded work is retried by the next pass, so this is money and latency lost rather than
        data. An answer the model could not be correlated back to its document is dropped rather
        than guessed at — placing it against the wrong posting would be wrong, self-consistent
        and impossible to spot afterwards.
      </p>
    </Card>
  );
}

function Failures({ api }: { api: JobPlatformApi }) {
  const [calls, setCalls] = useState<AiCallResponse[]>();
  const [error, setError] = useState<unknown>();
  const [showAll, setShowAll] = useState(false);

  const load = useCallback(() => {
    setError(undefined);
    api.aiCalls({ days: WINDOW_DAYS, failuresOnly: !showAll, limit: 100 })
      .then((r) => setCalls(r.items))
      .catch(setError);
  }, [api, showAll]);

  useEffect(load, [load]);

  if (error) {
    return <ErrorNote error={error} onRetry={load} />;
  }

  return (
    <Card
      title={showAll ? 'Every call' : 'Calls that lost something'}
      subtitle={`Last ${WINDOW_DAYS} days, newest first`}
      actions={(
        <button className="btn" onClick={() => setShowAll((v) => !v)}>
          {showAll ? 'Failures only' : 'Show all'}
        </button>
      )}
    >
      {!calls && <div className="empty">Loading…</div>}

      {calls?.length === 0 && (
        <div className="empty">
          {showAll
            ? 'No model calls in this window.'
            : 'Nothing lost in this window. Every call was correlated back in full.'}
        </div>
      )}

      {calls && calls.length > 0 && (
        <div style={{ overflowX: 'auto' }}>
          <table>
            <thead>
              <tr>
                <th scope="col">When</th>
                <th scope="col">Pass</th>
                <th scope="col">Deployment</th>
                <th scope="col">Outcome</th>
                <th scope="col">Usable</th>
                <th scope="col">Took</th>
                <th scope="col">Tokens</th>
                <th scope="col">Why, and what it lost</th>
              </tr>
            </thead>
            <tbody>
              {calls.map((call, i) => (
                <tr key={`${call.occurredAtUtc}-${i}`}>
                  <td>{new Date(call.occurredAtUtc).toLocaleString()}</td>
                  <td>{call.operation}</td>
                  <td>{call.deployment ?? '—'}</td>
                  <td>{readOutcome(call.outcome)}</td>
                  {/* Paired, never alone. This column is the whole point of the page. */}
                  <td>{call.returned} of {call.requested}</td>
                  <td>{(call.durationMs / 1000).toFixed(1)}s</td>
                  <td>
                    {call.totalTokens > 0 ? call.totalTokens.toLocaleString() : '—'}
                    {call.reasoningTokens > 0 && (
                      <div className="muted">{call.reasoningTokens.toLocaleString()} reasoning</div>
                    )}
                  </td>
                  <td>
                    {call.reason ?? '—'}
                    {call.affectedIds.length > 0 && (
                      <div className="muted">
                        Retried next pass: {call.affectedIds.slice(0, 10).join(', ')}
                        {call.affectedIds.length > 10 && `, +${call.affectedIds.length - 10} more`}
                      </div>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Card>
  );
}

/**
 * The outcome in words rather than the enum name.
 *
 * `PartiallyDiscarded` is the one worth spelling out: it is not a shade of success, and reading
 * it as one is how a call that threw away half its answer got filed as a good night.
 */
function readOutcome(outcome: string): string {
  switch (outcome) {
    case 'Succeeded':
      return 'All usable';
    case 'PartiallyDiscarded':
      return 'Partly discarded';
    case 'Failed':
      return 'Nothing usable';
    default:
      return outcome;
  }
}
