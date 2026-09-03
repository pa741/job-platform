import { useCallback, useEffect, useState } from 'react';
import { ApiError, type JobPlatformApi } from '../api/client';
import type {
  ApplicationDetail, ApplicationSummary, OpenQuestion, Submission, SubmissionEvent,
} from '../api/types';
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

/**
 * The park, as the submission projection carries it.
 *
 * Declared here rather than taken from `Submission` because `api/types.ts` does not name these
 * yet, and every member is optional for a reason that outlives the wiring: the page has to
 * render correctly against a server that has not shipped them. A dashboard that throws on a
 * field it asked for early is worse than one that shows nothing, and `undefined` is a third
 * state — "this build cannot say" — which `isParked` must read as "not parked" rather than as
 * parked for a reason it failed to name.
 *
 * **Delete this and `Evidence` below, taking both from the shared types, the moment those carry
 * them.** Two spellings of one contract is how a page ends up reading a field the server
 * renamed, and these are deliberately small so that deleting them is a diff nobody argues with.
 */
interface ParkColumns {
  /** A `ParkReason` name — `Captcha`, `Expired`, … — or null on a submission nobody parked. */
  parkedReason?: string | null;

  parkedAtUtc?: string | null;

  /**
   * When it was let back into the queue. **Set means the park is over, not that it never
   * happened**: the reason and its date stay on the row, because "was never parked" and "was
   * parked for a captcha in March and applied to in April" are different histories.
   */
  unparkedAtUtc?: string | null;
}

/**
 * What a browser captured while claiming an application was made.
 *
 * Every member is optional on the server too: a page can submit and redirect somewhere carrying
 * no reference, a confirmation screen can render after the screenshot was taken, and **an
 * application with no evidence at all is still an application**. So this renders what there is
 * and disappears entirely where there is nothing, rather than showing an empty proof block.
 */
interface Evidence {
  /** The reference the employer's own system showed — "Application #4417290". */
  confirmationRef?: string | null;
  /** Where the browser ended up. Not the apply URL: that is where the attempt started. */
  finalUrl?: string | null;
  /** A pointer to a stored screenshot. **A path, never a link** — see `EvidenceBlock`. */
  screenshotRef?: string | null;
  /** The names of the fields that were filled in. Names, never the answers given to them. */
  submittedFields?: string[] | null;
}

/** A submission as this page reads it: the shared contract plus the park columns above. */
type PipelineRow = Submission & ParkColumns;

/** One logged event, plus whatever was captured while it was claimed. */
type LoggedEvent = SubmissionEvent & { evidence?: Evidence | null };

/**
 * When a parked posting comes back — mirrored from `ParkReasonPolicy.Requeue`, member for member.
 *
 * **This table and Core's must agree, and nothing in either build enforces it.** Core decides
 * what the queue offers an agent tomorrow; this decides what a person is told today. A
 * disagreement is invisible in both directions and bad in both: a posting filed here as
 * finished that quietly returns next run, or one filed as returning that never does and that
 * nobody goes looking for because the page said it would come back on its own.
 * `ParkReasonPolicy` is the authority. When a reason is added there, copy its answer here
 * rather than deriving one from what the reason means — the derivation is exactly what drifts.
 */
const REQUEUE: Record<string, 'Never' | 'WhenAnswered' | 'NextRun'> = {
  Expired: 'Never',
  Duplicate: 'Never',
  MissingAnswer: 'WhenAnswered',
  LoginRequired: 'NextRun',
  Captcha: 'NextRun',
  AccountRequired: 'NextRun',
  FormError: 'NextRun',
  OutOfQuota: 'NextRun',
};

/**
 * Why nothing was sent, and who it is waiting on.
 *
 * `stamp` is the word that travels with the row — a stamp is what survives being read out of
 * its section. `label` completes "nothing was sent: …". `hint` is what a person can do about it,
 * and says plainly that there is nothing to do where there is nothing: "it will be tried again"
 * is something somebody can act on, and an empty line under a blocked application is not.
 */
const PARK: Record<string, { stamp: string; label: string; hint: string }> = {
  Expired: {
    stamp: 'expired',
    label: 'the advert had gone',
    hint: 'A closed vacancy does not reopen at the same link. If this employer advertises it again it arrives as a new posting, which the queue has never seen.',
  },
  Duplicate: {
    stamp: 'duplicate',
    label: 'the same job had already been applied to',
    hint: 'Matched on title, employer and city rather than on the posting id, which is per board and so can never say that two adverts are one vacancy.',
  },
  LoginRequired: {
    stamp: 'login needed',
    label: 'it wanted a signed-in session and there was none',
    hint: "Sign in to that employer's system and the next run gets through.",
  },
  Captcha: {
    stamp: 'captcha',
    label: 'a human challenge stood in the way',
    hint: 'Nothing here tries to defeat one — the park is the handling. The same link is often clean on a later attempt.',
  },
  AccountRequired: {
    stamp: 'account needed',
    label: 'it will not take an application until an account exists',
    hint: 'The account is yours to create; nothing here will create one for you. It goes through on the run after you have made it.',
  },
  MissingAnswer: {
    stamp: 'unanswered',
    label: 'the form asked something nothing could answer',
    hint: 'Resolution refuses by default, because a confident near-miss on your application is worse than an interruption.',
  },
  FormError: {
    stamp: 'form error',
    label: 'the form itself refused',
    hint: 'A validation failure or a step that broke. Most of what lands here is transient, which is why it is retried rather than treated as a dead vacancy.',
  },
  OutOfQuota: {
    stamp: 'cap spent',
    label: "the day's cap was already spent",
    hint: 'The cap resets at midnight UTC. The vacancy was never the problem — this is the one reason that is about this system rather than about the posting.',
  },
};

/**
 * The phase groups, in the order an application moves through them.
 *
 * `unsent` carries a null phase and is not decoration: a submission row is what takes a posting
 * out of the applyable queue, so a row standing here with nothing recorded against it is a
 * vacancy that will never be offered again and an application that was never made. It is also
 * where a park somebody lifted without applying lands, which is why removing this group would
 * reintroduce the disappearance the parked sections exist to fix, one step further along.
 */
const GROUPS: { key: string; label: string; phases: (string | null)[]; note?: string }[] = [
  {
    key: 'unsent',
    label: 'Recorded, nothing sent',
    phases: [null],
    note: 'A submission row with no events on it. The row alone keeps the posting out of the '
      + 'queue, so nothing here will be offered again — usually a send whose event hit the '
      + 'daily cap, or a park lifted without an application following it.',
  },
  { key: 'live', label: 'Live', phases: ['Submitted', 'Acknowledged', 'ScreeningScheduled', 'InterviewScheduled'] },
  { key: 'offer', label: 'Offers', phases: ['OfferReceived'] },
  { key: 'closed', label: 'Closed', phases: ['Rejected', 'Withdrawn'] },
];

/**
 * The parked rows, split by what happens to them next.
 *
 * **Three sections rather than one badge**, because the difference is the whole of what a
 * person does about a park: an expired advert is finished and wants nothing, a captcha wants
 * nothing either but will be tried again, and a missing answer is one sentence away from going
 * back into the queue. Sorting those into one list under one heading makes the reader do the
 * classification that `ParkReasonPolicy` already did.
 *
 * `tone` is reinforcement and never the encoding: every section says in words which of the
 * three it is, and the stamp on each row names the reason. Oxide is this palette's attention
 * colour and is spent here on the only group with something to do.
 */
const PARK_GROUPS: { key: string; requeue: 'WhenAnswered' | 'NextRun' | 'Never'; label: string; tone: string; note: string }[] = [
  {
    key: 'answer',
    requeue: 'WhenAnswered',
    label: 'Parked — waiting on you',
    tone: 'warn',
    note: 'Nothing was sent for these. Each is one answer away from going back into the queue, '
      + 'and until it is answered every run will meet the same question again.',
  },
  {
    key: 'return',
    requeue: 'NextRun',
    label: 'Parked — comes back next run',
    tone: 'on',
    note: 'Nothing was sent, and nothing here needs doing: what stopped the run was about the '
      + 'attempt rather than the vacancy, so the posting is offered again on the next pass.',
  },
  {
    key: 'gone',
    requeue: 'Never',
    label: 'Parked — finished',
    tone: '',
    note: 'Nothing was sent and nothing will be. These are gone rather than blocked, so they '
      + 'are not offered again.',
  },
];

/**
 * Whether the park stands right now.
 *
 * **The pair, never the reason alone** — the same reading `SubmissionRow.IsParked` makes on the
 * server. Nothing on that table is cleared, so a row parked in March and applied to in April
 * still carries the reason it was parked for, and a page reading that column on its own would
 * report a live application as parked forever.
 */
function isParked(row: PipelineRow): boolean {
  return !!row.parkedReason && !row.unparkedAtUtc;
}

/**
 * Whether this row is an application that was actually sent.
 *
 * **A phase alone is not enough, and that was this page's defect.** Parking writes columns on
 * the submission and an event writes the phase, and neither write path reads the other — the
 * REST create-then-record path will happily append a `Submitted` event to a row a run parked
 * this morning. Counting a phase on its own put a posting nobody applied to into the total of
 * applications sent while the page rendered it in no group at all: the number went up and the
 * evidence went missing, both in the same direction.
 *
 * Where the park and the log disagree this refuses rather than picks. A park that still stands
 * is the loop's own account of an afternoon in which it sent nothing; unparking is what says
 * otherwise, which is what `UnparkAsync` is for. Counting such a row would make the headline
 * number assert something no part of the system does — so it is left out of the total and
 * rendered, with its log reachable, under the park it never lost.
 */
function isSent(row: PipelineRow): boolean {
  return row.phase !== null && !isParked(row);
}

/**
 * When a parked posting returns, or `NextRun` for a reason this build has never heard of.
 *
 * The lenient arm, matching the discard in `ParkReasonPolicy.Requeue` and for the same reason
 * measured the same way: an unrecognised reason arrives from a newer server than this bundle,
 * and reading it as finished retires a live vacancy on screen with nothing to notice, where
 * reading it as returning costs one optimistic line that the next run corrects.
 */
function requeueOf(reason: string): 'Never' | 'WhenAnswered' | 'NextRun' {
  return REQUEUE[reason] ?? 'NextRun';
}

/**
 * The open question one parked application is held on, where this page can name it.
 *
 * **Matched on the parked submission first and on the advert second, and allowed to find
 * neither.** One wording is one row however many adverts asked it - the queue folds typography
 * so that the same question with a curly apostrophe is not asked twice - which means the
 * question names the advert that hit it first and every other advert records its waiting on its
 * own parked row. A posting parked on a wording somebody else raised therefore matches neither
 * key. That is not a miss to paper over: the question is real and on the queue, so the row says
 * so and points there rather than guessing which of several open questions is the one.
 */
function questionFor(row: PipelineRow, questions: OpenQuestion[]): OpenQuestion | undefined {
  return questions.find((q) => q.parked?.submissionId === row.id)
    ?? questions.find((q) => q.postingId === row.postingId);
}

function shortDate(iso: string): string {
  return new Date(iso).toLocaleDateString();
}

/**
 * What was written, what was actually sent, and what was put down without being sent.
 *
 * Three lists that belong together. `GET /applications` returns the generated documents and
 * nothing called it, so an entire rendering subsystem - the markdown, the PDF, the emphasis the
 * writer was handed - had no surface at all. A draft nobody can reach is a model call nobody
 * gets anything for.
 *
 * The parked rows are the same failure caught later and one degree worse. A run that meets a
 * captcha, a login wall or a question nobody has answered records that as an attribute on the
 * submission - deliberately not an event, because the fold has nowhere to put one - and this
 * page rendered only the phases it knew, so a parked posting appeared nowhere while its row
 * still kept the vacancy out of every future run. **A park is the loop saying it sent nothing,
 * so the sections below sit above the applications that were sent**: the failure was a parked
 * posting being invisible, and the bottom of a long list is the second-best place to hide one.
 *
 * The status is a fold over the event log and staleness is derived on read, never stored. The
 * platform records what was sent; it never sends anything itself.
 */
export function Applications({ api, go }: { api: JobPlatformApi; go: (page: PageId) => void }) {
  const [expanded, setExpanded] = useState<number>();

  const loadAll = useCallback(
    () => Promise.all([
      api.submissions(),
      api.applications(),

      // Context for one park reason and never this page's subject, so it degrades on its own
      // rather than taking the applications down with it: a MissingAnswer park still says it is
      // held on a question and still points at the queue, it just cannot quote the wording.
      api.openQuestions().catch(() => ({ items: [] as OpenQuestion[] })),
    ])
      .then(([s, a, q]) => ({
        submissions: s.items as PipelineRow[], drafts: a.items, questions: q.items,
      })),
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

  const { submissions, drafts, questions } = data.state.data;

  const parked = submissions.filter(isParked);
  const sent = submissions.filter(isSent);

  // Only a sent application can have gone quiet. Staleness is measured from the row's creation
  // where there are no events, so a park left standing a fortnight reads as stale - which would
  // put "no employer has replied" against a posting no employer was ever written to.
  const quiet = sent.filter((s) => s.isStale);

  // A draft for a posting already recorded as sent is history, not a to-do. A parked posting's
  // draft is emphatically still a to-do: the park is the record of why nothing was sent, and it
  // is the case where a written application most needs somebody to notice it.
  const sentIds = new Set(sent.map((s) => s.postingId));
  const waiting = drafts.filter((d) => !sentIds.has(d.postingId));

  const toggle = (id: number) => setExpanded(expanded === id ? undefined : id);

  return (
    <div className="flow">
      <p className="lede">
        <b>{sent.length}</b> applications sent
        {quiet.length > 0 && <>, <b>{quiet.length}</b> gone quiet</>}
        {parked.length > 0 && <>, <b>{parked.length}</b> parked and never sent</>}
        {waiting.length > 0 && <>, and <b>{waiting.length}</b> draft{waiting.length === 1 ? '' : 's'} waiting on you</>}.
      </p>

      <p className="lede-note">
        The status is a fold over the event log and staleness is derived from it, never stored
        as a column — so it changes the moment anything arrives. Recording an application writes
        one event; it does not submit anything. A parked posting is one a run put down without
        applying: it is not an application sent, and it is not counted as one.
      </p>

      {waiting.length > 0 && (
        <section className="appgroup">
          <h2>Written and unsent</h2>
          {waiting.map((draft) => (
            <Draft key={draft.id} api={api} draft={draft} onSent={data.reload} />
          ))}
        </section>
      )}

      {PARK_GROUPS.map((group) => {
        const rows = parked.filter((s) => requeueOf(s.parkedReason!) === group.requeue);
        if (rows.length === 0) return null;

        return (
          <section className="appgroup" key={group.key}>
            <h2>{group.label}</h2>
            <p className="note">{group.note}</p>
            {rows.map((submission) => (
              <ParkedRow
                key={submission.id}
                api={api}
                submission={submission}
                question={questionFor(submission, questions)}
                tone={group.tone}
                go={go}
                expanded={expanded === submission.id}
                onToggle={() => toggle(submission.id)}
                onChanged={data.reload}
              />
            ))}
          </section>
        );
      })}

      {GROUPS.map((group) => {
        // Parked rows are held out here as well as counted out above, so a row carrying both a
        // park and a phase appears exactly once - under the park - rather than twice or nowhere.
        const rows = submissions.filter((s) => !isParked(s) && group.phases.includes(s.phase));
        if (rows.length === 0) return null;

        return (
          <section className="appgroup" key={group.key}>
            <h2>{group.label}</h2>
            {group.note && <p className="note">{group.note}</p>}
            {rows.map((submission) => (
              <Row
                key={submission.id}
                api={api}
                submission={submission}
                expanded={expanded === submission.id}
                onToggle={() => toggle(submission.id)}
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
  submission: PipelineRow;
  expanded: boolean;
  onToggle: () => void;
  onChanged: () => void;
}) {
  const channel = CHANNEL[submission.channel] ?? CHANNEL['Unknown']!;

  // Staleness on a row with no events measures how long the row has sat there, not how long an
  // employer has been silent - there is no employer in that story yet. "Gone quiet" against a
  // posting nobody was written to is the page inventing a correspondent.
  const quiet = submission.isStale && submission.phase !== null;

  // Green means in flight. A row with nothing recorded against it is not in flight and must not
  // be dressed as one - it is the same miscue as counting it, at the scale of one stamp.
  const live = submission.phase !== null && !submission.isClosed;

  return (
    <div className="record">
      <div className="record-head">
        <h3>{submission.postingTitle}</h3>
        <span className="co">{submission.company}</span>

        <span className="right">
          {quiet && (
            <span className="stamp warn" title="No event for a fortnight. Derived from the log, not a stored flag.">
              gone quiet
            </span>
          )}
          <span className={`stamp ${live ? 'good' : ''}`}>
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
        {quiet && ' Nothing has arrived for a fortnight.'}
      </p>

      {expanded && <Log api={api} submission={submission} onChanged={onChanged} />}
    </div>
  );
}

/**
 * One posting a run put down without applying to it.
 *
 * Reads as its own kind of thing rather than as an application with an unusual status, because
 * it is not an application: the row exists to record that nothing was sent and why. The stamp
 * carries "not sent" alongside the reason so the fact survives the row being read out of its
 * section - a screenshot, a narrow window, somebody scanning stamps down the right-hand edge.
 *
 * The log stays reachable where there is one. A parked row normally has no events, but the
 * combination is exactly the case worth being able to open.
 */
function ParkedRow({ api, submission, question, tone, go, expanded, onToggle, onChanged }: {
  api: JobPlatformApi;
  submission: PipelineRow;
  question: OpenQuestion | undefined;
  tone: string;
  go: (page: PageId) => void;
  expanded: boolean;
  onToggle: () => void;
  onChanged: () => void;
}) {
  const reason = submission.parkedReason!;
  const wording = PARK[reason];

  return (
    <div className="record">
      <div className="record-head">
        <h3>{submission.postingTitle}</h3>
        <span className="co">{submission.company}</span>

        <span className="right">
          <span className={`stamp ${tone}`} title={wording ? `Nothing was sent: ${wording.label}.` : undefined}>
            not sent · {wording ? wording.stamp : reason.toLowerCase()}
          </span>
          {submission.eventCount > 0 && (
            <button className="btn" onClick={onToggle}>
              {expanded ? 'Hide' : `Log (${submission.eventCount})`}
            </button>
          )}
        </span>
      </div>

      <p className="note">
        Nothing was sent{wording ? `: ${wording.label}` : ` — parked as ${reason}`}.
        {submission.parkedAtUtc && ` Put down on ${shortDate(submission.parkedAtUtc)}.`}
        {wording && ` ${wording.hint}`}
      </p>

      {reason === 'MissingAnswer' && (
        question ? (
          <>
            <h4 className="mini">The question it is held on</h4>
            <p className="note" style={{ marginTop: 0 }}>
              &ldquo;{question.questionText}&rdquo;
              {question.options.length > 0 && ` Choices offered: ${question.options.join(', ')}.`}
              {question.sensitive
                && ' It asks for something only you can state, so nothing here will infer it.'}
              {` Asked on ${shortDate(question.askedAtUtc)}. One question stands for every advert`
                + ' that asked that wording, so answering it can release more than this posting.'}
              {' '}
              <button className="linkish" onClick={() => go('questions')}>Answer it</button>.
            </p>
          </>
        ) : (
          <p className="note">
            The wording it could not answer is waiting on the{' '}
            <button className="linkish" onClick={() => go('questions')}>questions</button> queue,
            and this posting is held until that question has an answer. One question stands for
            every advert that asked it, so the wording is filed under whichever advert reached it
            first rather than under this one.
          </p>
        )
      )}

      {submission.phase !== null && (
        // Both claims are on the row and the page shows both rather than choosing. Parking sets
        // columns, recording an event sets the phase, and neither write path reads the other -
        // so this is what an application sent by hand after a run parked it looks like, and the
        // fix is to unpark the row rather than to have the dashboard guess.
        <p className="note">
          This row also carries a log and stands at{' '}
          <b>{PHASE[submission.phase] ?? submission.phase}</b>, which the park says nothing about.
          A park claims nothing was sent and an event claims something was; both are recorded, so
          this is not counted as an application sent until the park is lifted.
        </p>
      )}

      {expanded && <Log api={api} submission={submission} onChanged={onChanged} />}
    </div>
  );
}

function Log({ api, submission, onChanged }: {
  api: JobPlatformApi; submission: PipelineRow; onChanged: () => void;
}) {
  const [events, setEvents] = useState<LoggedEvent[]>();
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
                {event.evidence && <EvidenceBlock evidence={event.evidence} />}
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
 * What was captured while one claim about an application was made.
 *
 * **The answer to "what exactly did we send them", and it has been stored and shown nowhere.**
 * The confirmation reference is the one value here that means something outside this database -
 * it is what the employer's own record is keyed on, and what a person quotes when they chase -
 * and the final URL is where the browser actually ended up, which is not the apply URL on the
 * submission: that is where the attempt started, copied in at creation so a later edit to the
 * posting cannot rewrite history.
 *
 * **Blank counts as nothing**, the same reading `SubmissionEvidence.IsEmpty` makes on the way
 * in. A selector that matched an empty element yields `""` rather than null, and a block
 * rendered from those would be an evidence panel with nothing in it - a claim to have proof.
 *
 * **The screenshot is named, never linked.** It is stored as a path because a user-delegation
 * SAS expires, and an expired URL in an append-only log is a dead pointer that still looks like
 * evidence: a reader cannot tell "the screenshot is gone" from "the link aged out". Saying it
 * was kept is true for as long as the blob is.
 */
function EvidenceBlock({ evidence }: { evidence: Evidence }) {
  const reference = evidence.confirmationRef?.trim();
  const finalUrl = evidence.finalUrl?.trim();
  const screenshot = evidence.screenshotRef?.trim();
  const fields = evidence.submittedFields?.filter((name) => name.trim().length > 0) ?? [];

  if (!reference && !finalUrl && !screenshot && fields.length === 0) return null;

  return (
    <span style={{ display: 'block', marginTop: 'var(--s1)' }}>
      <span style={{ display: 'flex', gap: 'var(--s2)', flexWrap: 'wrap', alignItems: 'baseline' }}>
        {reference && (
          <span
            className="stamp on"
            title="The reference the employer's own system showed. Quote this when you chase it."
          >
            ref {reference}
          </span>
        )}
        {finalUrl && (
          <span>
            ended at <a href={finalUrl} target="_blank" rel="noreferrer" title={finalUrl}>{hostOf(finalUrl)}</a>
          </span>
        )}
        {screenshot && (
          <span
            className="stamp"
            title="A screenshot was kept. Held as a stored path rather than a link, because a signed URL expires and a dead link still looks like evidence."
          >
            screenshot kept
          </span>
        )}
      </span>
      {fields.length > 0 && (
        <span className="muted" style={{ display: 'block', marginTop: 'var(--s1)' }}>
          Filled in: {fields.join(', ')} — field names, never the answers given to them.
        </span>
      )}
    </span>
  );
}

/** The host, or the whole string where it will not parse. A URL is its own link text only when it is short. */
function hostOf(url: string): string {
  try {
    return new URL(url).host;
  } catch {
    return url;
  }
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
          tomorrow, or spread the dates to match when they actually went. An unattended run parks
          what it could not send as <em>OutOfQuota</em>, which is how those postings come back.
        </div>
      </div>
    );
  }

  return <ErrorNote error={error} />;
}
