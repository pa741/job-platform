import { useCallback, useEffect, useState } from 'react';
import type { JobPlatformApi } from '../api/client';
import type { Submission, SubmissionEvent } from '../api/types';
import { Card, ErrorNote } from '../components/Primitives';

/**
 * The phases, in the order an application moves through them.
 *
 * The API returns stable identifiers and the wording lives here, following the axis and relation
 * labels on the Matches page. The two terminal phases are last because that is where a closed
 * application belongs on screen, not because of anything in the enum.
 */
const PHASE: Record<string, string> = {
  Submitted: 'Sent',
  Acknowledged: 'Acknowledged',
  ScreeningScheduled: 'Screening booked',
  InterviewScheduled: 'Interview booked',
  OfferReceived: 'Offer',
  Rejected: 'Rejected',
  Withdrawn: 'Withdrawn',
};

/** The order the groups appear in. Not started sits first: it is the pile that needs doing. */
const GROUPS: { key: string; label: string; phases: (string | null)[] }[] = [
  { key: 'open', label: 'Not sent yet', phases: [null] },
  { key: 'live', label: 'Live', phases: ['Submitted', 'Acknowledged', 'ScreeningScheduled', 'InterviewScheduled'] },
  { key: 'offer', label: 'Offers', phases: ['OfferReceived'] },
  { key: 'closed', label: 'Closed', phases: ['Rejected', 'Withdrawn'] },
];

/** Who takes the application. `Unknown` is honest rather than apologetic — see below. */
const CHANNEL: Record<string, string> = {
  Ats: 'employer',
  Board: 'job board',
  Unknown: 'link',
};

/**
 * What each channel actually claims.
 *
 * `Unknown` means neither a direct link nor an offsite flag was established — not that the board
 * hosts it. Those were conflated until 2026-09-01, when a missing link was found to mean nothing
 * at all: LinkedIn had stopped publishing apply URLs, and all 4,470 of its postings read as Easy
 * Apply. The scraper now reads the board's own offsite marker, so `Ats` can be true with only the
 * board's URL to show for it — the employer takes the application, and the posting says where.
 */
const CHANNEL_HINT: Record<string, string> = {
  Ats: "The employer's own application system — the posting says where",
  Board: 'You recorded that you applied through the board',
  Unknown: 'Neither a direct link nor an offsite flag — this opens the board listing',
};

/** Who asserted an event. Worth showing: an inbox reader gets things wrong, and a person does not. */
const SOURCE: Record<string, string> = {
  Candidate: 'you',
  Client: 'an agent',
  Email: 'read from email',
};

/**
 * The submission pipeline: what was actually sent, and what came back.
 *
 * **This page exists before the tools that will write to it, deliberately.** Driven by hand it is
 * already a tracker; and if the pipeline is not legible to a person, an agent writing to it is
 * writing somewhere nobody is looking.
 *
 * **Nothing here sends anything.** The server records that an application was submitted and never
 * submits one — applying is irreversible and outward-facing, so it stays outside this system
 * entirely. The apply link opens in a new tab and the candidate does the rest.
 *
 * **The status is a fold over the event log, not a column.** So "stale" is computed on read
 * rather than written by a timer, a rejected application is never stale, and a phase of nothing
 * means nothing has happened rather than that the application failed.
 */
export function Submissions({ api }: { api: JobPlatformApi }) {
  const [items, setItems] = useState<Submission[]>();
  const [error, setError] = useState<unknown>();
  const [expanded, setExpanded] = useState<number>();

  const load = useCallback(() => {
    setError(undefined);
    api.submissions()
      .then((result) => setItems(result.items))
      .catch(setError);
  }, [api]);

  useEffect(load, [load]);

  if (error) {
    return <ErrorNote error={error} onRetry={load} />;
  }

  if (!items) {
    return <div className="empty">Loading…</div>;
  }

  if (items.length === 0) {
    return (
      <div className="empty">
        Nothing sent yet. Open a match, apply through the employer’s own link, and record it
        here — this page is the record of what went out, not a way of sending it.
      </div>
    );
  }

  const stale = items.filter((s) => s.isStale).length;

  return (
    <div className="grid">
      {stale > 0 && (
        <div className="empty">
          {stale === 1 ? 'One application has' : `${stale} applications have`} had no news for a
          fortnight. Closed ones are never counted here.
        </div>
      )}

      {GROUPS.map((group) => {
        const rows = items.filter((s) => group.phases.includes(s.phase));

        if (rows.length === 0) {
          return null;
        }

        return (
          <Card key={group.key} title={group.label} subtitle={subtitle(group.key, rows.length)}>
            <div className="scroll-x">
              <table>
                <thead>
                  <tr>
                    <th>Role</th>
                    <th>Company</th>
                    <th>Where</th>
                    <th>Stage</th>
                    <th>Last news</th>
                    <th />
                  </tr>
                </thead>
                <tbody>
                  {rows.map((submission) => (
                    <Row
                      key={submission.id}
                      api={api}
                      submission={submission}
                      expanded={expanded === submission.id}
                      onToggle={() => setExpanded(expanded === submission.id ? undefined : submission.id)}
                      onChanged={load}
                    />
                  ))}
                </tbody>
              </table>
            </div>
          </Card>
        );
      })}
    </div>
  );
}

function subtitle(key: string, count: number) {
  switch (key) {
    case 'open':
      return 'Recorded but not yet marked as sent.';
    case 'live':
      return `${count} in progress. Ordered by when something last happened.`;
    case 'closed':
      return 'Kept, never deleted — the log is the record.';
    default:
      return undefined;
  }
}

function Row({ api, submission, expanded, onToggle, onChanged }: {
  api: JobPlatformApi;
  submission: Submission;
  expanded: boolean;
  onToggle: () => void;
  onChanged: () => void;
}) {
  return (
    <>
      <tr>
        <td>
          {submission.postingTitle}
          {submission.isStale && (
            <span
              className="pill warning"
              style={{ marginLeft: 8 }}
              title="No event for a fortnight. Derived from the log, not a stored flag."
            >
              quiet
            </span>
          )}
        </td>
        <td>{submission.company ?? '—'}</td>
        <td>
          {submission.applyUrl
            ? (
              <a
                href={submission.applyUrl}
                target="_blank"
                rel="noreferrer noopener"
                title={CHANNEL_HINT[submission.channel]}
              >
                {CHANNEL[submission.channel] ?? submission.channel}
              </a>
            )
            : CHANNEL[submission.channel] ?? submission.channel}
        </td>
        <td>
          {submission.phase ? PHASE[submission.phase] ?? submission.phase : '—'}
          {submission.stage && <span className="muted"> · {submission.stage}</span>}
        </td>
        <td title={submission.lastActivityUtc}>{when(submission.lastActivityUtc)}</td>
        <td>
          <button className="btn" onClick={onToggle}>
            {expanded ? 'Hide' : `Log (${submission.eventCount})`}
          </button>
        </td>
      </tr>
      {expanded && (
        <tr>
          <td colSpan={6}>
            <Log api={api} submission={submission} onChanged={onChanged} />
          </td>
        </tr>
      )}
    </>
  );
}

/**
 * One application's whole history, and the form that appends to it.
 *
 * Append-only: there is nothing here that edits or removes an event, because the log is the
 * record and a log with an eraser is not worth auditing. A mistake is corrected by recording
 * what actually happened, which is also what a person would want to see afterwards.
 */
function Log({ api, submission, onChanged }: {
  api: JobPlatformApi;
  submission: Submission;
  onChanged: () => void;
}) {
  const [events, setEvents] = useState<SubmissionEvent[]>();
  const [type, setType] = useState('Submitted');
  const [stage, setStage] = useState('');
  const [note, setNote] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<unknown>();

  const load = useCallback(() => {
    api.submissionEvents(submission.id)
      .then((result) => setEvents(result.items))
      .catch(setError);
  }, [api, submission.id]);

  useEffect(load, [load]);

  const record = () => {
    setSaving(true);
    setError(undefined);

    api.recordSubmissionEvent(submission.id, {
      type,
      stage: stage.trim() || undefined,
      note: note.trim() || undefined,
    })
      .then(() => {
        setStage('');
        setNote('');
        load();
        // The status is folded from the events, so the row above is now wrong until the list
        // is re-read. Refreshing here rather than patching state locally keeps one source of
        // truth for a value nothing on this page computes.
        onChanged();
      })
      .catch(setError)
      .finally(() => setSaving(false));
  };

  return (
    <div className="grid">
      {error ? <ErrorNote error={error} onRetry={load} /> : null}

      {!events && <div className="empty">Loading…</div>}

      {events?.length === 0 && (
        <div className="empty">Nothing recorded yet. Mark it sent once you have applied.</div>
      )}

      {events && events.length > 0 && (
        <ol className="timeline">
          {events.map((event) => (
            <li key={`${event.atUtc}-${event.type}-${event.note ?? ''}`}>
              <strong>{PHASE[event.type] ?? event.type}</strong>
              {event.stage && <span> · {event.stage}</span>}
              <span className="muted"> · {when(event.atUtc)} · {SOURCE[event.source] ?? event.source}</span>
              {event.note && <div className="muted">{event.note}</div>}
            </li>
          ))}
        </ol>
      )}

      <div className="filters">
        <label>
          What happened
          <select className="btn" value={type} onChange={(e) => setType(e.target.value)}>
            {Object.entries(PHASE).map(([value, label]) => (
              <option key={value} value={value}>{label}</option>
            ))}
          </select>
        </label>

        <label>
          Round
          <input
            type="text"
            value={stage}
            maxLength={120}
            placeholder="Tech round 2"
            onChange={(e) => setStage(e.target.value)}
          />
        </label>

        <label>
          Note
          <input
            type="text"
            value={note}
            maxLength={1000}
            onChange={(e) => setNote(e.target.value)}
          />
        </label>

        <button className="btn" disabled={saving} onClick={record}>
          {saving ? 'Recording…' : 'Record'}
        </button>
      </div>
    </div>
  );
}

/** Days rather than a timestamp: the question this page answers is "how long has it been". */
function when(iso: string) {
  const days = Math.floor((Date.now() - new Date(iso).getTime()) / 86_400_000);

  if (days <= 0) return 'today';
  if (days === 1) return 'yesterday';
  if (days < 31) return `${days} days ago`;

  return new Date(iso).toLocaleDateString();
}
