import { useCallback, useEffect, useState } from 'react';
import { ApiError, type JobPlatformApi } from '../api/client';
import type { ApplicationDetail, ApplicationSummary, Submission, SubmissionEvent } from '../api/types';
import { ErrorNote } from '../components/Primitives';
import { useApiResource } from '../components/useApiResource';
import { WakingRegion, LoadingRegion } from '../components/WakingRegion';
import type { PageId } from '../routing/route';

/**
 * The phases, in the order an application moves through them.
 *
 * The API returns stable identifiers and the wording lives here. The two terminal phases are
 * last because that is where a closed application belongs on screen, not because of anything
 * in the enum.
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

/** Who asserted an event. Worth showing: an inbox reader gets things wrong, a person does not. */
const SOURCE: Record<string, string> = {
  Candidate: 'you',
  Client: 'an agent',
  Email: 'read from email',
};

/**
 * What each channel actually claims.
 *
 * `Unknown` means neither a direct link nor an offsite flag was established — not that the
 * board hosts it. Those were conflated until a missing link was found to mean nothing at all:
 * LinkedIn had stopped publishing apply URLs, and all 4,470 of its postings read as Easy Apply.
 */
const CHANNEL: Record<string, { label: string; hint: string }> = {
  Ats: { label: 'the employer', hint: "The employer's own application system — the posting says where" },
  Board: { label: 'the job board', hint: 'You recorded that you applied through the board' },
  Unknown: { label: 'a link', hint: 'Neither a direct link nor an offsite flag — this opens the board listing' },
};

const GROUPS: { key: string; label: string; phases: (string | null)[] }[] = [
  { key: 'live', label: 'Live', phases: ['Submitted', 'Acknowledged', 'ScreeningScheduled', 'InterviewScheduled'] },
  { key: 'offer', label: 'Offers', phases: ['OfferReceived'] },
  { key: 'closed', label: 'Closed', phases: ['Rejected', 'Withdrawn'] },
];

/**
 * What was written, and what was actually sent.
 *
 * Two lists that belong together and were never on one page: `GET /applications` returns the
 * generated documents and nothing called it, so an entire rendering subsystem - the markdown,
 * the PDF, the emphasis the writer was handed - had no surface at all. A draft nobody can
 * reach is a model call nobody gets anything for.
 *
 * The status is a fold over the event log and staleness is derived on read, never stored. The
 * platform records what was sent; it never sends anything itself.
 */
export function Applications({ api, go }: { api: JobPlatformApi; go: (page: PageId) => void }) {
  const [expanded, setExpanded] = useState<number>();

  const loadAll = useCallback(
    () => Promise.all([api.submissions(), api.applications()])
      .then(([s, a]) => ({ submissions: s.items, drafts: a.items })),
    [api],
  );

  const data = useApiResource(loadAll);

  if (data.state.status === 'waking') {
    return <WakingRegion what="Your applications" onRetry={data.reload} go={go} />;
  }
  if (data.state.status === 'error') {
    return <ErrorNote error={data.state.error} onRetry={data.reload} />;
  }
  if (data.state.status === 'loading') return <LoadingRegion what="your applications" />;

  const { submissions, drafts } = data.state.data;

  // A draft for a posting already recorded as sent is history, not a to-do.
  const sentIds = new Set(submissions.filter((s) => s.phase !== null).map((s) => s.postingId));
  const waiting = drafts.filter((d) => !sentIds.has(d.postingId));
  const quiet = submissions.filter((s) => s.isStale);

  return (
    <div className="flow">
      <p className="lede">
        <b>{submissions.filter((s) => s.phase !== null).length}</b> applications sent
        {quiet.length > 0 && <>, <b>{quiet.length}</b> gone quiet</>}
        {waiting.length > 0 && <>, and <b>{waiting.length}</b> draft{waiting.length === 1 ? '' : 's'} waiting on you</>}.
      </p>

      <p className="lede-note">
        The status is a fold over the event log and staleness is derived from it, never stored
        as a column — so it changes the moment anything arrives. Recording an application writes
        one event; it does not submit anything.
      </p>

      {waiting.length > 0 && (
        <section className="appgroup">
          <h2>Written and unsent</h2>
          {waiting.map((draft) => (
            <Draft key={draft.id} api={api} draft={draft} onSent={data.reload} />
          ))}
        </section>
      )}

      {GROUPS.map((group) => {
        const rows = submissions.filter((s) => group.phases.includes(s.phase));
        if (rows.length === 0) return null;

        return (
          <section className="appgroup" key={group.key}>
            <h2>{group.label}</h2>
            {rows.map((submission) => (
              <Row
                key={submission.id}
                api={api}
                submission={submission}
                expanded={expanded === submission.id}
                onToggle={() => setExpanded(expanded === submission.id ? undefined : submission.id)}
                onChanged={data.reload}
              />
            ))}
          </section>
        );
      })}

      {submissions.length === 0 && waiting.length === 0 && (
        <div className="empty">
          Nothing sent and nothing drafted. Open a match on the{' '}
          <button className="linkish" onClick={() => go('shortlist')}>shortlist</button> and write
          an application for it.
        </div>
      )}
    </div>
  );
}

/**
 * One generated application, with the documents it produced.
 *
 * The download is the point. `MarkdownPdfRenderer` is a whole subsystem — an embedded font
 * resolver and all — built because the markdown is the record and the PDF is rendered per
 * request, and until now nothing in the UI asked it for one.
 */
function Draft({ api, draft, onSent }: {
  api: JobPlatformApi; draft: ApplicationSummary; onSent: () => void;
}) {
  const [detail, setDetail] = useState<ApplicationDetail>();
  const [downloading, setDownloading] = useState<string>();
  const [recording, setRecording] = useState(false);
  const [error, setError] = useState<unknown>();

  const download = (kind: 'cv' | 'cover-letter', filename: string) => {
    setDownloading(kind);
    setError(undefined);

    api.applicationPdf(draft.id, kind)
      .then((blob) => {
        const url = URL.createObjectURL(blob);
        const link = document.createElement('a');
        link.href = url;
        link.download = filename;
        link.click();
        URL.revokeObjectURL(url);
      })
      .catch(setError)
      .finally(() => setDownloading(undefined));
  };

  const markSent = () => {
    setRecording(true);
    setError(undefined);

    api.createSubmission(draft.postingId)
      .then((submission) => api.recordSubmissionEvent(submission.id, { type: 'Submitted' }))
      .then(onSent)
      .catch(setError)
      .finally(() => setRecording(false));
  };

  return (
    <div className="record">
      <div className="record-head">
        <h3>{draft.postingTitle}</h3>
        <span className="co">{draft.company}</span>
        <span className="right">
          <span className="stamp">revision {draft.revision}</span>
          <button className="btn" onClick={() => detail ? setDetail(undefined) : void api.application(draft.id).then(setDetail).catch(setError)}>
            {detail ? 'Hide the draft' : 'Read the draft'}
          </button>
          <button className="btn" disabled={downloading === 'cv'} onClick={() => download('cv', `CV-${draft.postingTitle}.pdf`)}>
            {downloading === 'cv' ? 'Preparing…' : 'CV (PDF)'}
          </button>
          <button
            className="btn" disabled={downloading === 'cover-letter'}
            onClick={() => download('cover-letter', `Cover-letter-${draft.postingTitle}.pdf`)}
          >
            {downloading === 'cover-letter' ? 'Preparing…' : 'Cover letter (PDF)'}
          </button>
          <button className="btn primary" disabled={recording} onClick={markSent}>
            {recording ? 'Recording…' : 'I sent this'}
          </button>
        </span>
      </div>

      {error ? <WriteError error={error} /> : null}

      {draft.instructions && (
        <p className="note">Written with your steer: &ldquo;{draft.instructions}&rdquo;</p>
      )}

      {detail && (
        <>
          {detail.emphasised.length > 0 && (
            <>
              <h4 className="mini">This draft leads with</h4>
              <ul className="tight">{detail.emphasised.map((x) => <li key={x}>{x}</li>)}</ul>
            </>
          )}
          <h4 className="mini">CV</h4>
          <pre className="markdown">{detail.curriculumVitaeMarkdown}</pre>
          <h4 className="mini">Cover letter</h4>
          <pre className="markdown">{detail.coverLetterMarkdown}</pre>
        </>
      )}
    </div>
  );
}

function Row({ api, submission, expanded, onToggle, onChanged }: {
  api: JobPlatformApi;
  submission: Submission;
  expanded: boolean;
  onToggle: () => void;
  onChanged: () => void;
}) {
  const channel = CHANNEL[submission.channel] ?? CHANNEL['Unknown']!;

  return (
    <div className="record">
      <div className="record-head">
        <h3>{submission.postingTitle}</h3>
        <span className="co">{submission.company}</span>

        <span className="right">
          {submission.isStale && (
            <span className="stamp warn" title="No event for a fortnight. Derived from the log, not a stored flag.">
              gone quiet
            </span>
          )}
          <span className={`stamp ${submission.isClosed ? '' : 'good'}`}>
            {submission.phase ? PHASE[submission.phase] ?? submission.phase : 'not sent'}
            {submission.stage && ` · ${submission.stage}`}
          </span>
          <button className="btn" onClick={onToggle}>
            {expanded ? 'Hide' : `Log (${submission.eventCount})`}
          </button>
        </span>
      </div>

      <p className="note">
        Goes to <span title={channel.hint}>{channel.label}</span>.
        {submission.isStale && ' Nothing has arrived for a fortnight.'}
      </p>

      {expanded && <Log api={api} submission={submission} onChanged={onChanged} />}
    </div>
  );
}

function Log({ api, submission, onChanged }: {
  api: JobPlatformApi; submission: Submission; onChanged: () => void;
}) {
  const [events, setEvents] = useState<SubmissionEvent[]>();
  const [type, setType] = useState('Acknowledged');
  const [stage, setStage] = useState('');
  const [note, setNote] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<unknown>();

  const load = useCallback(() => {
    api.submissionEvents(submission.id).then((r) => setEvents(r.items)).catch(setError);
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
        // The phase is a server-side fold over these events, so the parent has to re-read.
        onChanged();
      })
      .catch(setError)
      .finally(() => setSaving(false));
  };

  return (
    <div className="log-wrap">
      {error ? <WriteError error={error} /> : null}

      {events && (
        <ol className="timeline">
          {[...events].reverse().map((event, i) => (
            <li key={`${event.atUtc}-${i}`}>
              <span className="when">{new Date(event.atUtc).toLocaleDateString()}</span>
              <span>
                {PHASE[event.type] ?? event.type}
                {event.stage && ` · ${event.stage}`}
                {event.note && <span className="muted"> — {event.note}</span>}
              </span>
              <span className="who">{SOURCE[event.source] ?? event.source}</span>
            </li>
          ))}
        </ol>
      )}

      <div className="row-actions">
        <select value={type} onChange={(e) => setType(e.target.value)} aria-label="What happened">
          {Object.entries(PHASE).map(([value, label]) => (
            <option key={value} value={value}>{label}</option>
          ))}
        </select>
        <input value={stage} maxLength={120} placeholder="Round (optional)" onChange={(e) => setStage(e.target.value)} />
        <input value={note} maxLength={1000} placeholder="Note (optional)" onChange={(e) => setNote(e.target.value)} />
        <button className="btn" disabled={saving} onClick={record}>
          {saving ? 'Recording…' : 'Record'}
        </button>
      </div>

      <p className="note">
        Append-only. Nothing here edits or deletes an event — withdrawing is an event of its own.
      </p>
    </div>
  );
}

/**
 * A refused write, told apart from a broken one.
 *
 * The daily cap answers 429, and a 429 is not a mistake: the request is well formed and would
 * be accepted tomorrow. It counts by the event's own timestamp, so importing a real history
 * can reach it too — which is worth saying, because otherwise the number looks arbitrary.
 */
function WriteError({ error }: { error: unknown }) {
  if (error instanceof ApiError && error.status === 429) {
    return (
      <div className="err">
        <strong>That is today&rsquo;s twenty-fifth application.</strong>
        <div className="muted" style={{ marginTop: 4 }}>
          The cap counts <em>Sent</em> events by the date on the event rather than by when it was
          recorded, so backdating a history reaches it too. Nothing was lost — record the rest
          tomorrow, or spread the dates to match when they actually went.
        </div>
      </div>
    );
  }

  return <ErrorNote error={error} />;
}
