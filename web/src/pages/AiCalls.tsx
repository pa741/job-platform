import { useCallback, useEffect, useState } from 'react';
import type { JobPlatformApi } from '../api/client';
import type { AiCallResponse, AiCallTotalsResponse } from '../api/types';
import { Card, ErrorNote, StatTile } from '../components/Primitives';
import { useAiFailures } from '../feed/useAiFailures';

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
 *
 * **The live tail is the point of the realtime piece.** The two passes that matter run at 03:00
 * and 03:30, and until now the only way to learn that a sweep had lost batches was to open this
 * page later and compare two numbers. A failure now arrives while it is still happening. The
 * page works identically without it — the feed is optional, `unavailable` is a normal state, and
 * everything below the tail is the same authenticated read it always was.
 */
export function AiCalls({ api }: { api: JobPlatformApi }) {
  return (
    <div className="stack">
      <p className="lede">What the model was asked, and what came back.</p>

      <p className="lede-note">
        There is no money on this page. The ledger records tokens, no unit price is stored
        anywhere in the system, and a pound figure would be a number this dashboard invented.
        Discards are the number to watch instead: a batch can report success and still lose most
        of its work, and that was invisible until it was counted. The live tail is a fact about
        the system rather than about you — it reaches every signed-in client, and after a cold
        start the first message has been measured at three minutes, so it is not instantaneous.
      </p>

      <div className="grid">
        <LiveTail api={api} />
        <Totals api={api} />
        <Failures api={api} />
      </div>
    </div>
  );
}

/**
 * Failures as they are recorded, over the change feed.
 *
 * Renders nothing at all when there is no feed and nothing has arrived. A card saying "no live
 * failures" is a card that is right 99% of the time and therefore never read — and worse, it
 * would make a deployment with no realtime service look broken rather than simply quieter.
 */
function LiveTail({ api }: { api: JobPlatformApi }) {
  const { state, failures } = useAiFailures(api, true);

  if (failures.length === 0) {
    return null;
  }

  return (
    <Card
      title="Live"
      subtitle={state === 'live'
        ? 'Pushed as each call is recorded'
        : 'Connection lost — the list below is still accurate'}
    >
      <div className="scroll-x">
        <table>
          <thead>
            <tr>
              <th>When</th>
              <th>Pass</th>
              <th>Outcome</th>
              <th className="num">Asked</th>
              <th className="num">Back</th>
              <th className="num">Lost</th>
              <th>Why</th>
            </tr>
          </thead>
          <tbody>
            {failures.map((f, i) => (
              <tr key={`${f.occurredAtUtc}-${i}`}>
                <td>{new Date(f.occurredAtUtc).toLocaleTimeString()}</td>
                <td>{f.operation}</td>
                <td><span className="pill critical">{f.outcome}</span></td>
                <td className="num">{f.requested}</td>
                <td className="num">{f.returned}</td>
                <td className="num">{f.discarded}</td>
                <td title={f.reason ?? undefined}>{f.reason ?? '—'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </Card>
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
