import { useCallback, useEffect, useState, type ReactNode } from 'react';
import { ApiError, type JobPlatformApi } from '../api/client';
import type { PipelineSettingsRequest, PipelineSettingsResponse } from '../api/types';
import { Card, ErrorNote, Field } from '../components/Primitives';
import type { PageId } from '../routing/route';

/**
 * The seven levers this page edits.
 *
 * Seven of the nine, and the two it leaves out are not an omission: `dailySendCap` and
 * `chaseAfterDays` take effect on the next write and the next read, so they live on Applications
 * beside the figures they move. What decides which page a lever is edited on is *when a saved
 * value takes effect*, never what it is about — the reasoning is written out on
 * `PipelineSettingsRequest` in `types.ts` and is not restated here.
 */
type Lever =
  | 'assessmentsPerNight' | 'assessmentThreshold' | 'recentSharePercent' | 'recentWindowDays'
  | 'draftsPerNight' | 'draftMinAssessmentScore' | 'draftPostedWithinDays';

/**
 * How a sentence of the server's refusal is attributed to the box that caused it.
 *
 * <b>The server is the enforcement and its answer is what gets displayed.</b> A refusal arrives
 * as one `detail` string — `PipelineSettingsValidation.Validate` returns every problem it found
 * and the endpoint joins them with a space, exactly as `SearchEndpoints` already does — so this
 * page splits that string on sentence boundaries and shows a sentence under the control it
 * names. The patterns below are the distinctive noun of each message rather than a parse of it.
 *
 * <b>An unmatched sentence is never dropped and never guessed at.</b> The whole `detail` is
 * rendered in full by `ErrorNote` at the top of the page whatever happens here, so the worst
 * this attribution can do is fail to repeat a sentence beside a box — it cannot hide one. That
 * is why there is no cleverer rule: a heuristic that decided an unrecognised sentence "probably
 * belongs to the last field that matched" would put somebody else's message under a number that
 * is fine, which is worse than saying nothing twice.
 *
 * <b>Two of the messages are cross-field and name two values, so they match two levers and
 * appear under both.</b> That is the point rather than a duplicate: a save can be refused with
 * every individual box on this page inside its own range, and a message shown under only one of
 * the two numbers it is about sends somebody to change the wrong one.
 *
 * `draftPostedWithinDays` carries two patterns because its message is two sentences and the
 * second — "Leave it unset for postings of every age" — is the actionable half.
 * `draftMinAssessmentScore` matches "drafting floor" as well as "drafting assessment floor",
 * because the cross-field message's second sentence uses the short form.
 */
const REFUSAL_PHRASES: Record<Lever, RegExp[]> = {
  assessmentsPerNight: [/assessments per night/i],
  assessmentThreshold: [/assessment threshold/i],
  recentSharePercent: [/recent share/i],
  recentWindowDays: [/recent window/i],
  draftsPerNight: [/drafts per night/i],
  draftMinAssessmentScore: [/drafting (assessment )?floor/i],
  draftPostedWithinDays: [/drafting may only be limited/i, /leave it unset/i],
};

/**
 * The pipeline's nightly budgets, under System beside Searches.
 *
 * <b>Per-person, and deliberately not part of the dashboard's bootstrap</b>, exactly like
 * Searches and the profile: this reads Azure SQL, and the search-term picker every corpus page
 * waits on is served from Cosmos precisely so that opening the dashboard cannot wait on a
 * database that pauses when idle. A page somebody opened may wait; the bootstrap may not. So
 * `pipeline` is not in `CORPUS_PAGES` in `App.tsx`, and putting it there would make a page about
 * one person's own configuration wait on a call it has no use for.
 *
 * <b>The single most important thing on the page is that nothing on it is instant.</b> Every
 * other filter in this dashboard answers as it moves — the Shortlist's minimum score re-queries
 * the list as it is dragged — and these answer at 03:30 and 04:30 UTC tomorrow. Somebody who
 * cannot tell those apart concludes the control is broken and changes it again, which is why the
 * judgement budget is here rather than beside that slider, and why the lede says when these land
 * before it says anything else.
 *
 * <b>A save is a whole-record replace and this page renders seven of the nine levers.</b> The
 * two it does not render are read, carried and written back untouched — see `toRequest`, which
 * is where that trap is written down. There is no Reset button, for a related reason: the
 * defaults are `PipelineSettings.Default` on the server and `types.ts` deliberately restates
 * neither the bounds nor the shipped values, so a reset here would have to hard-code nine
 * numbers with nothing on this side able to hold them to the record. Resetting is a save of the
 * defaults, and it is not worth a second copy of them to offer.
 */
export function Pipeline({ api, go }: { api: JobPlatformApi; go: (page: PageId) => void }) {
  const [form, setForm] = useState<PipelineSettingsRequest>();
  const [stored, setStored] = useState<PipelineSettingsResponse>();
  const [error, setError] = useState<unknown>();
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);

  // Bumped whenever the server hands back a record - a load, or a save that was accepted - and
  // used as the `key` on every box. Each box holds what is being typed into it as text rather
  // than as a number, because `Number('')` is 0 and a box mid-edit is legitimately empty;
  // re-keying is how that local text is re-seeded from a new answer, without an effect that
  // fights the typing to do it.
  const [revision, setRevision] = useState(0);

  const load = useCallback(() => {
    setError(undefined);

    api.pipelineSettings()
      .then((result) => {
        setStored(result);
        setForm(toRequest(result));
        setRevision((n) => n + 1);
        setSaved(false);
      })
      .catch(setError);
  }, [api]);

  useEffect(load, [load]);

  const set = <K extends keyof PipelineSettingsRequest>(
    key: K,
    value: PipelineSettingsRequest[K],
  ) => {
    setSaved(false);
    setForm((current) => (current ? { ...current, [key]: value } : current));
  };

  const save = () => {
    if (!form) return;

    setSaving(true);
    setSaved(false);
    // Cleared here and nowhere else. A refusal is deliberately not cleared on the first
    // keystroke: it is the instruction for the edit being made, and a message that disappears
    // the moment you touch the box it belongs to is one you have to provoke again to read.
    setError(undefined);

    api.savePipelineSettings(form)
      .then((result) => {
        setStored(result);
        setForm(toRequest(result));
        setRevision((n) => n + 1);
        setSaved(true);
      })
      .catch(setError)
      .finally(() => setSaving(false));
  };

  if (error && !form) return <ErrorNote error={error} onRetry={load} />;
  if (!form || !stored) return <div className="empty">Loading…</div>;

  // Only an `ApiError` carries the server's `detail`. A timeout or a network fault has nothing
  // to attribute to a field, and `ErrorNote` already says the useful thing about both.
  const detail = error instanceof ApiError ? error.detail : undefined;
  const refused = (lever: Lever) => sentencesFor(lever, detail);

  const dirty = JSON.stringify(form) !== JSON.stringify(toRequest(stored));

  return (
    <div className="flow">
      <p className="lede">
        Everything on this page takes effect on the next nightly pass, not now. The match sweep
        runs at <b>03:30 UTC</b> and the drafting pass at <b>04:30 UTC</b>, so a number saved
        this afternoon changes nothing you can see until tomorrow morning.
      </p>

      <p className="lede-note">
        Worth reading twice, because every other filter in this dashboard is instant — the
        Shortlist&rsquo;s minimum score re-queries the list as you drag it. These do not, and it
        is why they are not on that page: a control that looks instant and answers fifteen hours
        later gets changed again by somebody who reasonably concludes it is broken. What you set
        here is what tonight&rsquo;s two passes are allowed to spend. None of it changes what a
        match means, or how anything is scored.
      </p>

      {error ? <ErrorNote error={error} /> : null}

      <Card
        title="Your pipeline"
        subtitle={savedSummary(stored)}
        actions={
          <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
            {saved && <span className="pill">Saved</span>}
            {dirty && <span className="pill warning">Unsaved</span>}
            <button className="btn" onClick={save} disabled={saving || !dirty}>
              {saving ? 'Saving…' : 'Save'}
            </button>
          </div>
        }
      >
        <p className="muted" style={{ fontSize: 13, margin: 0 }}>
          There are two scores below and they are not one score shown twice. The first decides
          which postings are worth paying a model to <b>read</b>. The second decides which of the
          postings it read are worth paying a more expensive model to <b>write</b> for. They come
          out of two passes an hour apart, they are read from different columns, and neither is a
          stricter version of the other.
        </p>
        <p className="muted" style={{ fontSize: 13, marginBottom: 0 }}>
          A refused save stores nothing and says why. These numbers are checked against each
          other as well as against their own bounds, so a save can be turned down with every box
          on this page inside its range — and nothing is quietly corrected on the way past,
          because a setting that is silently clamped is a setting that lies to whoever typed it.
        </p>
      </Card>

      <Card
        title="What gets judged"
        subtitle="The match sweep, 03:30 UTC. Scoring every posting against you is free arithmetic and happens either way; this is what gets spent on verdicts."
      >
        <div className="form-grid">
          <LeverField
            label="Judgements a night"
            hint="How many postings a model actually reads and gives a verdict on. 40 is what the sweep has always bought. 0 is a real setting and the honest off switch — the scoring pass still runs, so your shortlist is unchanged and nothing is spent on verdicts. Well above 40 the night starts running out of clock rather than budget, and a sweep cut off part-way leaves verdicts empty with nothing saying why."
            problems={refused('assessmentsPerNight')}
          >
            <NumberBox
              key={revision}
              value={form.assessmentsPerNight}
              min={0}
              max={200}
              invalid={refused('assessmentsPerNight').length > 0}
              onCommit={(next) => { if (next !== null) set('assessmentsPerNight', next); }}
            />
          </LeverField>

          <LeverField
            label="Match score that earns a judgement"
            hint="The score the arithmetic gave a posting — the number on the Shortlist — before buying a verdict about it is worth it. 45, and low on purpose: the scorer under-rates somebody whose relevant experience is buried in prose, and the model is there to catch exactly that. 0 does not mean judge everything; it means every scored posting is in the draw and the budget beside this one decides how many are taken."
            problems={refused('assessmentThreshold')}
          >
            <NumberBox
              key={revision}
              value={form.assessmentThreshold}
              min={0}
              max={100}
              invalid={refused('assessmentThreshold').length > 0}
              onCommit={(next) => { if (next !== null) set('assessmentThreshold', next); }}
            />
          </LeverField>
        </div>

        <p className="note">
          The point of the system is to answer a day&rsquo;s postings on the day they appear, so
          part of each night&rsquo;s budget is reserved for postings that have only just arrived
          and the rest of the corpus fills whatever that reservation leaves. The two boxes below
          are one control and have to be read together: a share, and what it counts as new.
        </p>

        <div className="form-grid">
          <LeverField
            label="Reserved for new postings (%)"
            hint="67 — two thirds, which is what the sweep has always reserved. A reservation and not an ordering: whatever the recent draw cannot fill goes back to the draw over everything, so a quiet day costs nothing and a backlog still drains. 0 reserves nothing and takes the best scores wherever they are. 100 spends the whole night on new postings, and on any day busy enough to fill the shortlist the backlog then stops draining altogether."
            problems={refused('recentSharePercent')}
          >
            <NumberBox
              key={revision}
              value={form.recentSharePercent}
              min={0}
              max={100}
              invalid={refused('recentSharePercent').length > 0}
              onCommit={(next) => { if (next !== null) set('recentSharePercent', next); }}
            />
          </LeverField>

          <LeverField
            label="What counts as new (days)"
            hint="3. This window belongs to the reservation beside it and to nothing else: the posted-within filters on the Shortlist, Postings and Applications pages answer to the system-wide age rule and do not move with it. It merely starts as the same number, which is not the same as being it."
            problems={refused('recentWindowDays')}
          >
            <NumberBox
              key={revision}
              value={form.recentWindowDays}
              min={1}
              max={30}
              invalid={refused('recentWindowDays').length > 0}
              onCommit={(next) => { if (next !== null) set('recentWindowDays', next); }}
            />
          </LeverField>
        </div>
      </Card>

      <Card
        title="What gets drafted"
        subtitle="The writing pass, 04:30 UTC. It writes the covering letter and the answers for a posting; the CV is chosen from your library rather than written."
      >
        <div className="form-grid">
          <LeverField
            label="Drafts a night"
            hint="How many postings get a letter and answers written for them. This is the expensive pass — it runs on a deployment priced at roughly twenty-five times the one that does the judging. Its real ceiling is the lower of 25 and your daily send cap: a night that writes more letters than a day can send is buying prose for adverts nobody will reach, and because the corpus is re-scraped nightly the surplus is tailored to adverts that will have gone. 0 stops the pass."
            problems={refused('draftsPerNight')}
          >
            <NumberBox
              key={revision}
              value={form.draftsPerNight}
              min={0}
              max={25}
              invalid={refused('draftsPerNight').length > 0}
              onCommit={(next) => { if (next !== null) set('draftsPerNight', next); }}
            />
          </LeverField>

          <LeverField
            label="Assessment score a posting must earn before it is written for"
            hint="80. The score the model gave the posting when it judged it — a different number from the match score on the card above, from a different pass and a different column, and not a stricter version of it. It cannot sit below the score that earns a judgement: a posting under that line is never judged at all, so it carries no assessment score for this floor to read, and lowering it past there widens the band by nothing."
            problems={refused('draftMinAssessmentScore')}
          >
            <NumberBox
              key={revision}
              value={form.draftMinAssessmentScore}
              min={0}
              max={100}
              invalid={refused('draftMinAssessmentScore').length > 0}
              onCommit={(next) => { if (next !== null) set('draftMinAssessmentScore', next); }}
            />
          </LeverField>

          <LeverField
            label="Only draft for postings from the last (days)"
            hint="Leave it empty for postings of any age, which is the default and what the pass has always done. Empty is not zero: zero would mean posted since this instant, would select almost nothing, and is refused — so an empty box sends nothing at all rather than a nought. Between 1 and 90 when you do set one, and past about 45 it selects nothing extra, because nothing older than that has been scored."
            problems={refused('draftPostedWithinDays')}
          >
            <NumberBox
              key={revision}
              value={form.draftPostedWithinDays}
              min={1}
              max={90}
              allowEmpty
              placeholder="Any age"
              invalid={refused('draftPostedWithinDays').length > 0}
              onCommit={(next) => set('draftPostedWithinDays', next)}
            />
          </LeverField>
        </div>

        <p className="note">
          Two levers are deliberately not on this page: how many applications may be recorded as
          sent in a day, and how long silence lasts before one wants chasing. Both take effect on
          the next write and the next read rather than overnight, so they sit on{' '}
          <button className="linkish" onClick={() => go('applications')}>Applications</button>,
          beside the figures they move. The send cap is also the ceiling on drafts above, which is
          why a save can be refused here over a number set there. Saving this page carries both of
          them through exactly as it read them.
        </p>
      </Card>
    </div>
  );
}

/**
 * One box, its explanation, and whatever the server said about it.
 *
 * The refusal renders between the input and the hint — closest to the control it is about — and
 * leads with a word rather than only a colour, on the same reasoning as `StatTile`'s arrow: the
 * meaning has to survive for a reader who cannot separate the red from the grey.
 */
function LeverField({ label, hint, problems, children }: {
  label: string;
  hint: string;
  problems: string[];
  children: ReactNode;
}) {
  return (
    <Field label={label} hint={hint}>
      {children}
      {problems.map((problem) => (
        <span
          key={problem}
          className="field-hint"
          role="alert"
          style={{ color: 'var(--status-critical)' }}
        >
          <b>Refused.</b> {problem}
        </span>
      ))}
    </Field>
  );
}

/**
 * A whole-number box that keeps what is being typed while it is being typed.
 *
 * <b>Local text rather than a number read straight off the event, because `Number('')` is 0.</b>
 * Every one of these levers means something at 0 — the off switch, or "let the budget decide" —
 * so a box cleared on the way to typing 40 would commit a real and quite different setting as it
 * passed through empty, and on `draftPostedWithinDays` it would turn the one absence the record
 * can express into the one value validation refuses. The text is seeded on mount and re-seeded
 * by the `key` the page bumps whenever the server hands back a record, which is the only moment
 * an answer arrives that this box did not produce.
 *
 * <b>Anything integral is sent, including a number out of bounds.</b> `min` and `max` drive the
 * spinner and nothing else: the server owns the bounds and its refusal is what gets displayed,
 * so a mistyped 500 has to reach it in order to come back as a sentence naming the ceiling, and
 * a negative goes for the same reason. What is <i>not</i> sent is a value the request body could
 * not carry at all — 4.5 into an `int` is a deserialisation failure rather than a refusal
 * anybody can act on — so a non-integral entry is restored to the stored number when the box
 * loses focus, rather than being rounded into a request nobody typed.
 */
function NumberBox({ value, min, max, invalid, onCommit, allowEmpty = false, placeholder }: {
  value: number | null;
  min: number;
  max: number;
  invalid: boolean;
  onCommit: (value: number | null) => void;
  allowEmpty?: boolean;
  placeholder?: string;
}) {
  const [text, setText] = useState(value === null ? '' : String(value));

  const entered = (next: string) => {
    setText(next);

    const trimmed = next.trim();

    // Empty is a value only where the record has an absence to express. On the other six it is a
    // box mid-edit, and committing anything for it - 0 least of all - invents a setting nobody
    // typed. The `null` this can emit is therefore only ever reachable on the one lever that
    // takes it; the call sites of the other six still guard, because a prop is a weaker promise
    // than a type.
    if (trimmed === '') {
      if (allowEmpty) onCommit(null);
      return;
    }

    if (/^-?\d+$/.test(trimmed)) onCommit(Number(trimmed));
  };

  const settle = () => {
    const trimmed = text.trim();
    const committable = trimmed === '' ? allowEmpty : /^-?\d+$/.test(trimmed);

    setText(committable ? trimmed : value === null ? '' : String(value));
  };

  return (
    <input
      type="number"
      inputMode="numeric"
      step={1}
      min={min}
      max={max}
      placeholder={placeholder}
      value={text}
      aria-invalid={invalid || undefined}
      onChange={(e) => entered(e.target.value)}
      onBlur={settle}
    />
  );
}

/**
 * The sentences of the server's refusal that name one lever.
 *
 * Split on a full stop followed by whitespace, which is how the list `Validate` returns is
 * joined - `string.Join(" ", problems)`, the spelling `SearchEndpoints` already uses and the
 * settings route follows. A message that runs to two sentences matches on both where both name
 * it and contributes nothing where they do not; the whole `detail` is rendered untouched at the
 * top of the page either way.
 */
function sentencesFor(lever: Lever, detail: string | undefined): string[] {
  if (!detail) return [];

  return detail
    .split(/(?<=\.)\s+/)
    .map((sentence) => sentence.trim())
    .filter((sentence) => sentence.length > 0
      && REFUSAL_PHRASES[lever].some((phrase) => phrase.test(sentence)));
}

/**
 * What the header says about whether anybody has configured this.
 *
 * `updatedUtc` is the whole of that answer and there is no boolean beside it. Null does not mean
 * the read failed and it does not mean the nine numbers are missing: an unconfigured candidate
 * runs on `PipelineSettings.Default`, whose every value is the constant the shipped code already
 * used, so the form below is filled in either way. Saying which of the two it is, is the
 * difference between "this is what has always run" and a form somebody reads as empty.
 */
function savedSummary(stored: PipelineSettingsResponse): string {
  return stored.updatedUtc
    ? `Last saved ${new Date(stored.updatedUtc).toLocaleString()}.`
    : 'Never configured. Every number below is the one this pipeline has always run on, so saving them unchanged changes nothing.';
}

/**
 * The stored record as a save would send it.
 *
 * <b>Nine fields go back and this page renders seven.</b> The PUT is a replace and not a merge,
 * and the record's own initialisers fill in anything a body leaves out — so omitting
 * `dailySendCap` and `chaseAfterDays` here would not leave them alone, it would reset them to
 * the shipped 25 and 14 the first time somebody changed a drafting budget on this page. They are
 * read, carried and written back untouched. It is the same trap `model.md` records for keeping
 * these levers off the profile row in the first place: a value stored beside a form that does
 * not carry it is erased by the first save of that form.
 *
 * The residual is worth stating rather than hiding. Those two are carried as they were *read*,
 * so a change made to them on Applications in another tab since this page loaded would be
 * overwritten by a save here. Re-reading immediately before the PUT would narrow that window
 * without closing it and would buy a round trip on every save, against a record that is one
 * person's own settings edited from one place at a time.
 */
function toRequest(stored: PipelineSettingsResponse): PipelineSettingsRequest {
  const { updatedUtc, ...levers } = stored;
  void updatedUtc;

  return levers;
}
