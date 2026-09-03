import { useCallback, useState } from 'react';
import { ApiError, type JobPlatformApi } from '../api/client';
import type { AnswerQuestionResponse, AnswerScope, OpenQuestion } from '../api/types';
import { ErrorNote, Field } from '../components/Primitives';
import { useApiResource } from '../components/useApiResource';
import { WakingRegion, LoadingRegion } from '../components/WakingRegion';
import type { PageId } from '../routing/route';

/**
 * The words stored for a declined answer, and they are stored.
 *
 * Not a flag, not an empty string, not a dismissal: the value typed into the employer's box is
 * "Prefer not to say", and every EEO section in the corpus offers exactly that option. Storing a
 * blank instead would leave the question looking unanswered, so the next run asks again and the
 * application parks again - which is the loop the queue exists to break.
 */
const DECLINED = 'Prefer not to say';

/**
 * The widest answer the store will take, mirroring `FormAnswerLimits.MaxValueLength`.
 *
 * The server refuses an over-long answer rather than shortening it, because a truncated sentence
 * typed into an application reads as a statement rather than as a bug. So the box stops accepting
 * characters at the bound instead of letting somebody write a paragraph and lose it to a 400.
 */
const MAX_VALUE = 4000;

/**
 * The question queue: what an unattended run could not answer, and where a person answers it.
 *
 * <b>This page is the other half of abstention.</b> Resolution refuses by default - below the
 * confidence floor, on a sensitive field with no stored answer, or where an option set will not
 * map cleanly - because a confident near-miss on somebody's application is worse than an
 * interruption. That is only defensible if the interruption goes somewhere, and this is where.
 * Without it, a run parks an application for a missing answer, offers the same advert next run,
 * parks it again, and does so forever.
 *
 * <b>Every answer here is stamped as the candidate's own, and only here.</b> Everything written
 * through the agent surface is recorded as a client's assertion; the dashboard is the one place
 * that may say a person typed it. That distinction is why the sensitive questions - salary, right
 * to work, everything an EEO section asks - exist in the store at all: nothing in the derived
 * namespace can produce one, so a value of that kind is here because somebody wrote it, and
 * nowhere else because there is nowhere else.
 *
 * <b>Answering is a write about the candidate, never an outward one.</b> Closing a question takes
 * it out of the unanswered set, which is the same set the applyable predicate reads, so the advert
 * parked on it stops being held once nothing else on it is outstanding. It does not apply to
 * anything, and nothing on this page reaches an employer.
 */
export function Questions({ api, answering, onOpen, go }: {
  api: JobPlatformApi;
  /** The question whose form is open, from the URL. Undefined for the queue on its own. */
  answering?: number;
  /** Opens one question, or closes the open one. Pushes, so Back closes the form. */
  onOpen: (questionId?: number) => void;
  go: (page: PageId) => void;
}) {
  const [receipt, setReceipt] = useState<AnswerQuestionResponse>();

  const load = useCallback(() => api.openQuestions().then((r) => r.items), [api]);
  const questions = useApiResource(load);

  if (questions.state.status === 'waking') {
    return <WakingRegion what="Your question queue" onRetry={questions.reload} go={go} />;
  }
  if (questions.state.status === 'error') {
    return <ErrorNote error={questions.state.error} onRetry={questions.reload} />;
  }
  if (questions.state.status === 'loading') return <LoadingRegion what="your question queue" />;

  const items = questions.state.data;
  const sensitive = items.filter((q) => q.sensitive);
  const held = items.filter((q) => q.parked !== null);

  // An id in the URL that is not in the queue. Said out loud rather than ignored: the ordinary
  // way to get here is a link followed after the question was already answered, and a form that
  // silently fails to open reads as a broken page.
  //
  // Except immediately after answering it, when it is this page that took it out of the queue.
  // Closing the form walks history back, which lands after the reload it raced, so without the
  // last clause somebody would be told their own answer had gone missing.
  const missing = answering !== undefined
    && !items.some((q) => q.questionId === answering)
    && receipt?.closedQuestionId !== answering;

  const answered = (result: AnswerQuestionResponse) => {
    setReceipt(result);
    questions.reload();
    onOpen(undefined);
  };

  return (
    <div className="flow">
      <p className="lede">
        {items.length === 0
          ? <>Nothing is waiting on you.</>
          : <>
              <b>{items.length}</b> question{items.length === 1 ? '' : 's'} waiting on you
              {sensitive.length > 0 && <>, <b>{sensitive.length}</b> of which only you can answer</>}
              {held.length > 0 && <>, holding <b>{held.length}</b> application{held.length === 1 ? '' : 's'} back</>}.
            </>}
      </p>

      <p className="lede-note">
        A question lands here when a run met a form field it would not guess at. Answering stores
        what you said and closes the question; once nothing on that advert is still unanswered it
        goes back into the queue the next unattended pass reads. Nothing here applies to anything,
        and nothing on this page is sent to an employer.
      </p>

      {receipt && <Receipt receipt={receipt} onDismiss={() => setReceipt(undefined)} />}

      {missing && (
        <div className="err">
          <strong>That question is no longer in the queue.</strong>
          <div className="muted" style={{ marginTop: 4 }}>
            It was probably answered already — the first answer stands, and answering again
            would not replace it. What is still open is below.
          </div>
        </div>
      )}

      {items.length === 0 && !receipt && (
        <div className="empty">
          Nothing to answer. Questions arrive when an unattended run meets a form field it will
          not guess at, so an empty queue means either that nothing has run or that nothing
          stopped it. What it did instead is on{' '}
          <button className="linkish" onClick={() => go('applications')}>Applications</button>.
        </div>
      )}

      {items.map((question) => (
        <Entry
          key={question.questionId}
          api={api}
          question={question}
          open={answering === question.questionId}
          onOpen={() => onOpen(question.questionId)}
          onClose={() => onOpen(undefined)}
          onAnswered={answered}
        />
      ))}
    </div>
  );
}

/**
 * One question, with the advert that raised it and what it is holding up.
 *
 * The question itself is the heading rather than the job title, which inverts every other list
 * in this dashboard. It is the right way round here: this is a queue of things to answer, and
 * the advert is the context that makes an answer safe to give rather than the thing being
 * listed.
 */
function Entry({ api, question, open, onOpen, onClose, onAnswered }: {
  api: JobPlatformApi;
  question: OpenQuestion;
  open: boolean;
  onOpen: () => void;
  onClose: () => void;
  onAnswered: (result: AnswerQuestionResponse) => void;
}) {
  return (
    <div className="record">
      <div className="record-head">
        <h3>{question.questionText}</h3>

        <span className="right">
          {question.sensitive && (
            <span
              className="stamp warn"
              title="Recognised from the question's own wording as well as from whatever raised it, so the mark does not depend on anything having ticked a box."
            >
              only you can answer
            </span>
          )}
          {question.parked && <span className="stamp">application parked</span>}
          <span className="stamp">asked {waitedFor(question.askedAtUtc)}</span>
          <button className="btn primary" aria-expanded={open} onClick={open ? onClose : onOpen}>
            {open ? 'Close' : 'Answer'}
          </button>
        </span>
      </div>

      <p className="note">
        {question.postingTitle
          ? <>Raised by <b>{question.postingTitle}</b>{question.company ? ` at ${question.company}` : ''}.</>
          : <>Raised without an advert behind it, so it is asked in general rather than about one job.</>}
        {' '}
        {question.parked
          ? <>That application has been parked since{' '}
              {new Date(question.parked.parkedAtUtc).toLocaleDateString()}, waiting on this. It
              goes back into the queue an unattended pass reads once nothing on it is still
              unanswered.</>
          : <>Nothing is parked on it at the moment.</>}
      </p>

      {open && (
        <AnswerForm
          key={question.questionId}
          api={api}
          question={question}
          onAnswered={onAnswered}
        />
      )}
    </div>
  );
}

/**
 * The answer, and how far it should carry.
 *
 * <b>Scope is a choice on the form rather than a default in the code, because the two mistakes
 * are not the same size.</b> An answer scoped too narrowly costs one more interruption the next
 * time the question is asked. An answer scoped too widely is "why do you want to work here",
 * written about one employer, typed into every other employer's form - which is the single most
 * legible way for an application to announce that nobody read it. So the narrowest scope the
 * question came with is preselected, widening is an act with a sentence next to it, and no scope
 * is offered that the question cannot actually carry.
 *
 * <b>The client picks a scope and never an id.</b> The company and posting behind it are read
 * server-side from the question's own advert; a body naming its own ids would let a mistyped
 * number file a salary expectation against an employer nobody applied to, with nothing to check
 * it against.
 */
function AnswerForm({ api, question, onAnswered }: {
  api: JobPlatformApi;
  question: OpenQuestion;
  onAnswered: (result: AnswerQuestionResponse) => void;
}) {
  const scopes = scopesFor(question);

  const [scope, setScope] = useState<AnswerScope>(scopes[0]?.scope ?? 'Global');
  const [value, setValue] = useState('');
  const [confirmed, setConfirmed] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<unknown>();

  const trimmed = value.trim();

  // Whitespace is not an answer and the store refuses one, so the button refuses it first: an
  // empty box would tell every later resolution that this question is settled.
  const ready = trimmed.length > 0 && (!question.sensitive || confirmed);

  const record = () => {
    setSaving(true);
    setError(undefined);

    api.answerQuestion(question.questionId, { value: trimmed, scope })
      .then(onAnswered)
      .catch(setError)
      .finally(() => setSaving(false));
  };

  return (
    <div className="log-wrap">
      {error ? <AnswerError error={error} /> : null}

      {question.sensitive && (
        // Raised onto a card rather than left as another paragraph in the form. This is the case
        // the whole declared/derived split exists for, and a panel that looked like every other
        // panel would leave somebody typing a salary expectation into what reads as an ordinary
        // text box. The oxide left border is not reused here: it means "something just happened
        // that you may want to reverse" on the banner above, and one device saying two things is
        // how both stop being read.
        <div className="card" style={{ marginBottom: 'var(--s4)' }}>
          <h4 className="mini">Nothing here can answer this but you</h4>
          <p className="note" style={{ marginTop: 0 }}>
            The only profile data this system will fill into a form is a short allowlist — your
            name, your contact details, your work history — and it holds no salary expectation,
            no right-to-work status, and nothing an equal-opportunities section asks. That is not
            a setting that could be turned on: there is no field to read. So whatever you type
            here is the only place that answer will ever exist, and it is offered back verbatim
            or not at all — never mapped onto a near-match, because a near-miss on a
            right-to-work question is a false statement made on your behalf.
          </p>
          <label className="check">
            <input
              type="checkbox"
              checked={confirmed}
              onChange={(e) => setConfirmed(e.target.checked)}
            />
            I am answering this myself, and it may be typed into an employer&rsquo;s form.
          </label>
        </div>
      )}

      <h4 className="mini">Who this answer is true for</h4>

      {/* Radios rather than a select, and the whole set visible rather than one line with the
          rest behind a click. What each scope costs is the decision being made here, and a
          closed select shows the default and hides the sentence explaining why it is the
          default. */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: 'var(--s1)' }}>
        {scopes.map((choice) => (
          <label className="check" key={choice.scope} style={{ alignItems: 'flex-start' }}>
            <input
              type="radio"
              name={`scope-${question.questionId}`}
              checked={scope === choice.scope}
              onChange={() => setScope(choice.scope)}
              style={{ marginTop: 3 }}
            />
            <span>
              {choice.label}
              <span className="muted"> — {choice.hint}</span>
            </span>
          </label>
        ))}
      </div>

      {scopes.length === 1 && (
        <p className="note">
          Only one scope is available: this question names no advert, so there is nothing
          narrower than everywhere to file it under.
        </p>
      )}

      {question.options.length > 0 && (
        <>
          <h4 className="mini">What the form offered</h4>
          <div className="chips">
            {question.options.map((option) => (
              <button
                key={option}
                className="btn"
                aria-pressed={value === option}
                onClick={() => setValue(option)}
              >
                {option}
              </button>
            ))}
          </div>
          <p className="note">
            Picking one puts the form&rsquo;s own words in the box, because those are the words
            that go back into it. You can still write your own.
          </p>
        </>
      )}

      <Field
        label="Your answer"
        hint={'Stored word for word and typed back into the form as written, so write what would '
          + 'go in the box rather than a note about it.'}
      >
        <textarea
          rows={3}
          value={value}
          maxLength={MAX_VALUE}
          placeholder="In the words that would be typed into the form"
          onChange={(e) => setValue(e.target.value)}
        />
      </Field>

      <div className="row-actions">
        <button
          className="btn"
          aria-pressed={value === DECLINED}
          onClick={() => setValue(DECLINED)}
        >
          {DECLINED}
        </button>
        <button className="btn primary" disabled={saving || !ready} onClick={record}>
          {saving ? 'Recording…' : 'Record this answer'}
        </button>
      </div>

      <p className="note">
        &ldquo;{DECLINED}&rdquo; is an answer and is stored as one — those words go into the box,
        and the question closes. Leaving it blank is not: nothing is stored, the question stays
        open, and the advert stays parked.
        {question.sensitive && !confirmed && <> Tick the box above to record this one.</>}
      </p>

      <p className="note">
        Answers are superseded rather than overwritten, so changing your mind later keeps what you
        said this time — which is the record to read when an interview goes strangely.
      </p>
    </div>
  );
}

/**
 * What answering did, including the half that is otherwise invisible.
 *
 * The advert leaving the parked state is the causal link this whole queue rests on, and it
 * happens somewhere nobody is looking: a question drops out of the unanswered set, which is the
 * same set the applyable predicate reads, and an advert nobody can see becomes eligible again.
 * Stated as what will happen next rather than as a success message, because "saved" is the one
 * thing about this write that nobody needed telling.
 */
function Receipt({ receipt, onDismiss }: {
  receipt: AnswerQuestionResponse; onDismiss: () => void;
}) {
  return (
    <div className="undobar">
      <span>
        <b>Recorded</b> as your own answer, {inWords(receipt.scope)}.
        {!receipt.created && ' That answer was already stored word for word, so nothing was superseded.'}
        {receipt.returnedToQueue.length > 0 && (
          <> {receipt.returnedToQueue.map((row) => row.postingTitle).join(', ')}{' '}
            {receipt.returnedToQueue.length === 1 ? 'is' : 'are'} no longer held back — the next
            unattended pass can pick {receipt.returnedToQueue.length === 1 ? 'it' : 'them'} up.
            Nothing has been sent.</>
        )}
        {receipt.returnedToQueue.length === 0 && receipt.closedQuestionId !== null
          && ' No application was parked on it, so nothing was waiting to be released.'}
        {receipt.note && <span className="muted"> {receipt.note}</span>}
      </span>
      <button className="btn" onClick={onDismiss}>Dismiss</button>
    </div>
  );
}

/**
 * A refused answer, told apart from a broken one.
 *
 * A 409 is the queue converging rather than failing: the first close stands, because the row
 * records that somebody was asked and what came back, and a second write would erase the
 * timestamp the first is evidence of. Reporting it as an error would have somebody retrying a
 * write that already succeeded — for somebody else, or in another tab.
 */
function AnswerError({ error }: { error: unknown }) {
  if (error instanceof ApiError && error.status === 409) {
    return (
      <div className="err">
        <strong>That question was already answered.</strong>
        <div className="muted" style={{ marginTop: 4 }}>
          The first answer stands and nothing was overwritten. Reload the queue to see what is
          still open; to change what you said, answer the question again when it is next asked —
          answers supersede rather than replace.
        </div>
      </div>
    );
  }

  return <ErrorNote error={error} />;
}

/**
 * The scopes this question can actually carry, narrowest first.
 *
 * Narrowest first is not presentation: the first entry is what the form preselects, which is how
 * "default to the narrowest scope the question came with" is implemented. A scope whose id the
 * question cannot supply is not offered at all rather than shown disabled — an employer-wide
 * answer filed with no employer applies to everybody, which is the failure scoping exists to
 * prevent, arriving through the interface instead.
 */
function scopesFor(question: OpenQuestion): { scope: AnswerScope; label: string; hint: string }[] {
  const choices: { scope: AnswerScope; label: string; hint: string }[] = [];

  if (question.postingId !== null) {
    choices.push({
      scope: 'Posting',
      label: 'Only this advert',
      hint: `offered back for ${question.postingTitle ?? 'this advert'} and for nothing else`,
    });
  }

  if (question.companyId !== null) {
    choices.push({
      scope: 'Company',
      label: `Every form ${question.company ?? 'this employer'} sends`,
      hint: 'filed against the employer rather than the name printed on the advert, so their '
        + 'other listings are covered too',
    });
  }

  choices.push({
    scope: 'Global',
    label: 'Wherever it is asked',
    hint: 'every employer, every form — right for a notice period, wrong for anything written '
      + 'about one job',
  });

  return choices;
}

/** The scope as a person would say it, for the receipt. */
function inWords(scope: AnswerScope): string {
  if (scope === 'Posting') return 'for that advert only';
  if (scope === 'Company') return 'for that employer';

  return 'wherever the question is asked';
}

/**
 * How long a question has been waiting, as an age rather than a timestamp.
 *
 * "Asked 04:12" looks the same whether that was this morning or a week ago, and the length of
 * the wait is the whole reason this queue is ordered oldest first.
 */
function waitedFor(iso: string): string {
  const hours = (Date.now() - new Date(iso).getTime()) / 3_600_000;

  if (hours < 1) return 'in the last hour';

  if (hours < 24) {
    const whole = Math.round(hours);
    return `${whole} hour${whole === 1 ? '' : 's'} ago`;
  }

  const days = Math.round(hours / 24);

  return `${days} day${days === 1 ? '' : 's'} ago`;
}
