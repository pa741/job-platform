import { useCallback, useState } from 'react';
import { ApiError, type JobPlatformApi } from '../api/client';
import type {
  CvGap, CvGapBriefResponse, CvLibraryCapacity, CvLibraryStaleness,
  CvVariantDetail, CvVariantSummary,
} from '../api/types';
import { ErrorNote, Field } from '../components/Primitives';
import { useApiResource } from '../components/useApiResource';
import { WakingRegion, LoadingRegion } from '../components/WakingRegion';
import type { PageId } from '../routing/route';

/**
 * The widest label the store will take, mirroring `CvVariantLimits.MaxLabelLength`.
 *
 * The box stops accepting characters at the bound rather than letting somebody write a phrase
 * and lose it to a 400 - the same decision the question queue makes about an answer, and for
 * the same reason: the server refuses rather than shortening, because a truncated value is a
 * statement somebody did not make.
 */
const MAX_LABEL = 60;

/**
 * The longest CV the store will take, mirroring `CvVariantLimits.MaxMarkdownLength`.
 *
 * Twenty thousand characters is roughly ten pages, past which the document has stopped being a
 * CV and the one controlled template stops producing something a recruiter reads. What the
 * bound is really refusing is a paste of something else entirely - a portfolio site, the text
 * dump of a PDF - which is what somebody does when they misread the box. There is no minimum,
 * deliberately: a three-line CV is a bad CV rather than an invalid one, and this system has no
 * business telling somebody their own document is too short.
 */
const MAX_MARKDOWN = 20_000;

/**
 * The one name every rendered CV is saved under, whichever variant produced it.
 *
 * <b>The label is never in a filename, and this is the client end of that rule.</b>
 * `Pablo_De_Groot_AI_Engineer_CV.pdf` tells an employer that a different CV is kept for other
 * roles - true, none of their business, and read off a file list before anybody opens the
 * document. A blob URL carries no `Content-Disposition`, so the anchor's `download` attribute
 * is the only name there is here; building it out of `variant.label` would be a caller
 * constructing filenames, which is exactly the shape `ApplicationPackFile` refuses to offer a
 * parameter for. This download lands on the candidate's own machine rather than in an
 * employer's file list, and it still uses the stable name: a client that builds filenames from
 * labels somewhere is the client that eventually builds one in the place that matters.
 */
const DOWNLOAD_STEM = 'Curriculum_Vitae';

/** What a write did, held until it is dismissed, so the consequence is stated rather than implied. */
type Receipt =
  | { kind: 'created' | 'saved' | 'renamed' | 'archived' | 'restored'; variant: CvVariantDetail };

/**
 * The CV library: the documents the candidate wrote, and the ones they have not.
 *
 * <b>This page exists because a model wrote a sentence no candidate would.</b> Asked what else
 * an employer should know, the application writer answered with the candidate's citizenship -
 * correctly, out of their own summary - and then added "I am an AI and they should have seen
 * this". It was stored, served through the pack, and one form submission away from a real
 * employer. A guard now drops that class of sentence, and a guard is a net under a trapeze.
 * This is the other half: the CV is written by the person whose name is on it, and a pass
 * chooses among what they wrote.
 *
 * <b>So there is no "regenerate", no "improve this for me" and no "write one from my profile"
 * anywhere on this page.</b> That is the feature rather than an omission, and it is the thing
 * most likely to be added back by somebody being helpful - the staleness notice below is
 * precisely where a "fix these for me" button looks obvious, and building it would put the
 * model back into the one document it was taken out of, on a nudge or worse on a timer. The
 * correct response to a stale CV is a sentence and a person deciding whether the change was one
 * their CV needed to mention.
 *
 * <b>Two things on one page, and they are one thing seen from both ends.</b> The library is
 * what selection may choose from; the brief is what selection could not choose for. A person
 * who reads only the first has a tidy shelf and no idea why the loop applied to eleven fewer
 * postings than it considered; a person who reads only the second has a work item and nowhere
 * to do it. They are the same afternoon, so they are the same page.
 *
 * <b>Nothing here reaches an employer, and nothing here applies to anything.</b> Writing a CV
 * stores markdown, renders it and reads it for selection. A document leaves this tenant only
 * when an application chooses it and somebody uploads it.
 */
export function CvLibrary({ api, editing, gapSeed, onOpen, go }: {
  api: JobPlatformApi;
  /** The variant whose editor is open, or the string `new`. From the URL, so Back closes it. */
  editing?: string;
  /** The gap a new CV is being written for, named by its seed concept key. From the query. */
  gapSeed?: string;
  /** Opens one editor, or closes the open one. Pushes, so Back closes rather than leaving. */
  onOpen: (target?: string, gapSeed?: string) => void;
  go: (page: PageId) => void;
}) {
  const [receipt, setReceipt] = useState<Receipt>();

  const loadLibrary = useCallback(() => api.cvLibrary(), [api]);
  const library = useApiResource(loadLibrary);

  // A second read rather than a field on the first, and the split is what keeps the page
  // usable. The brief is a different query over the blocked queue, it is the one read here
  // that can be slow, and a failure in it must still leave somebody able to open and edit
  // their own CVs - the same "per region, not per page" rule WakingRegion is written around.
  const loadGaps = useCallback(() => api.cvGaps(), [api]);
  const gaps = useApiResource(loadGaps);

  if (library.state.status === 'waking') {
    return <WakingRegion what="Your CV library" onRetry={library.reload} go={go} />;
  }
  if (library.state.status === 'error') {
    return <ErrorNote error={library.state.error} onRetry={library.reload} />;
  }
  if (library.state.status === 'loading') return <LoadingRegion what="your library" />;

  const { items, staleness, capacity } = library.state.data;
  const inUse = items.filter((variant) => !variant.isArchived);
  const archived = items.filter((variant) => variant.isArchived);

  const brief = gaps.state.status === 'ok' ? gaps.state.data : undefined;

  // An id in the URL that names no CV. Said out loud rather than ignored: the ordinary way to
  // get here is a link followed after the variant was archived from another tab, and an editor
  // that silently fails to open reads as a broken page.
  const missing = editing !== undefined
    && editing !== 'new'
    && !items.some((variant) => String(variant.variantId) === editing);

  const wrote = (kind: Receipt['kind'], variant: CvVariantDetail) => {
    setReceipt({ kind, variant });
    library.reload();

    // The brief is a function of what selection could not send, so a new CV changes it and a
    // rename does not. Reloading on every write would spend the expensive read to redraw an
    // identical list; reloading on none of them would leave "eleven postings are waiting" on
    // screen underneath the CV that was just written to unblock them.
    if (kind === 'created' || kind === 'saved' || kind === 'archived' || kind === 'restored') {
      gaps.reload();
    }

    if (kind === 'created') onOpen(undefined);
  };

  return (
    <div className="flow">
      <p className="lede">
        {items.length === 0
          ? <>You have not written a CV yet, and nothing here will write one for you.</>
          : <>
              <b>{capacity.inUse}</b> CV{capacity.inUse === 1 ? '' : 's'} in use of the{' '}
              <b>{capacity.cap}</b> this library holds
              {archived.length > 0 && <>, and <b>{archived.length}</b> archived</>}
              {staleness.anyStale && <>, <b>{staleness.stale}</b> of which predate
                {staleness.stale === 1 ? 's' : ''} your last profile change</>}.
            </>}
      </p>

      <p className="lede-note">
        A CV here is yours: you write the markdown, the system lays it out into the PDF and DOCX
        an employer is handed, and a pass chooses among them per posting rather than writing a
        new one. Nothing on this page writes or rewrites a word of a document - that is the
        point of the feature and not a missing button. Nothing here is sent anywhere either: a
        CV reaches an employer only when an application chooses it and somebody uploads it.
      </p>

      {receipt && <ReceiptNote receipt={receipt} onDismiss={() => setReceipt(undefined)} />}

      {missing && (
        <div className="err">
          <strong>That CV is not in your library.</strong>
          <div className="muted" style={{ marginTop: 4 }}>
            Nothing here deletes one, so it was almost certainly archived somewhere else -
            archived CVs are listed below and can be put back. What you have is underneath.
          </div>
        </div>
      )}

      <Capacity
        capacity={capacity}
        open={editing === 'new'}
        onOpen={() => onOpen('new')}
        onClose={() => onOpen(undefined)}
      />

      {editing === 'new' && (
        <NewVariant
          // Re-seeded rather than merged when the gap changes: the label is a default and a
          // default that arrives after somebody has started typing is a field that changes
          // under them.
          key={`new-${gapSeed ?? ''}`}
          api={api}
          gap={findGap(brief, gapSeed)}
          onCreated={(variant) => wrote('created', variant)}
          onCancel={() => onOpen(undefined)}
        />
      )}

      {items.length === 0 && !receipt && editing !== 'new' && (
        <div className="empty">
          Nothing to choose from yet. Until there is a CV here, every posting an unattended run
          would otherwise apply to is parked for want of one - which is what the brief below
          counts. Write the first one and the loop has something to send.
        </div>
      )}

      <Staleness staleness={staleness} count={inUse.length} />

      {inUse.length > 0 && (
        <div className="appgroup">
          <h2>In use</h2>
          {inUse.map((variant) => (
            <Variant
              key={variant.variantId}
              api={api}
              variant={variant}
              open={editing === String(variant.variantId)}
              onOpen={() => onOpen(String(variant.variantId))}
              onClose={() => onOpen(undefined)}
              onWrote={wrote}
            />
          ))}
        </div>
      )}

      {archived.length > 0 && (
        <div className="appgroup">
          <h2>Archived</h2>
          <p className="note" style={{ marginTop: 0 }}>
            Out of selection and out of the cap&rsquo;s count, and kept for good. An application
            made last year names the CV it sent, so &ldquo;what exactly did we send them&rdquo;
            has to stay answerable after the document has been retired - which is the ordinary
            end of a CV&rsquo;s life. Archiving is not a delete, and there is no delete.
          </p>
          {archived.map((variant) => (
            <Variant
              key={variant.variantId}
              api={api}
              variant={variant}
              open={editing === String(variant.variantId)}
              onOpen={() => onOpen(String(variant.variantId))}
              onClose={() => onOpen(undefined)}
              onWrote={wrote}
            />
          ))}
        </div>
      )}

      <GapBrief
        state={gaps.state.status}
        error={gaps.state.error}
        brief={brief}
        hasRoom={capacity.hasRoomForAnother}
        onRetry={gaps.reload}
        onWrite={(gap) => onOpen('new', gap.concepts[0]?.key)}
      />
    </div>
  );
}

/**
 * How much of the library is spent, and the one button that spends more of it.
 *
 * <b>The cap is stated before it refuses, which is the whole reason this block exists.</b> A
 * person who has written six CVs and is told "no" on the seventh has to work out for themselves
 * that archiving is the answer and that archived CVs do not count against the number. Saying it
 * here costs a sentence and a six-pixel rule, and it turns a refusal into something nobody meets
 * by surprise.
 *
 * The number is written out beside the track rather than encoded only by its length, the rule
 * every mark in this dashboard follows: at four of six the bar is the reinforcement and "four
 * of six" is the datum.
 */
function Capacity({ capacity, open, onOpen, onClose }: {
  capacity: CvLibraryCapacity;
  open: boolean;
  onOpen: () => void;
  onClose: () => void;
}) {
  const share = capacity.cap > 0 ? capacity.inUse / capacity.cap : 0;

  return (
    <div className="card">
      <header style={{ display: 'flex', alignItems: 'flex-start', gap: 12 }}>
        <div style={{ flex: 1 }}>
          <h2>The library</h2>
          <div className="sub">
            {capacity.inUse} of {capacity.cap} in use.{' '}
            {capacity.hasRoomForAnother
              ? `Room for ${capacity.cap - capacity.inUse} more.`
              : 'Full - archive one to make room.'}
          </div>
        </div>
        <button
          className="btn primary"
          aria-expanded={open}
          disabled={!capacity.hasRoomForAnother && !open}
          title={capacity.hasRoomForAnother
            ? undefined
            : 'The library is full. Archiving a CV retires it from selection and keeps its '
              + 'words, its files and their hash - it is not a delete, and it frees a place.'}
          onClick={open ? onClose : onOpen}
        >
          {open ? 'Cancel' : 'Write a new CV'}
        </button>
      </header>

      <div className="rank">
        <div>
          <div className="rank-row">
            <span className="nm">Places used</span>
            <span className="vl">{capacity.inUse} of {capacity.cap}</span>
          </div>
          <div className="measure">
            <i style={{ width: `${Math.min(100, Math.max(0, share * 100))}%` }} />
          </div>
        </div>
      </div>

      <p className="note">
        The cap is about how many documents one person will keep current rather than about
        storage. A stale CV looks exactly like a fresh one in a picker, and the system will send
        it - so the limit is set at the number somebody will still work through on the afternoon
        a profile edit puts every one of them behind.
      </p>
    </div>
  );
}

/**
 * How much of the library has fallen behind the profile, and what to do about it.
 *
 * <b>A nudge, and nothing that acts on it.</b> There is no button here, and there must not be:
 * rewriting the candidate's document with a model is exactly what this design removes, and a
 * staleness notice is the most natural-looking place in the product to put one. The response to
 * a CV that predates a profile change is a person reading it and deciding whether the change
 * was one that document needed to mention - which is sometimes no.
 *
 * <b>The three states are different facts rather than three phrasings of one.</b> A profile that
 * has never recorded a change and a library that is entirely current both count zero stale, for
 * opposite reasons; a page that could not separate them would say nothing on the day the feature
 * ships and say nothing on the day it mattered.
 */
function Staleness({ staleness, count }: { staleness: CvLibraryStaleness; count: number }) {
  if (count === 0) return null;

  if (staleness.profileUpdatedUtc === null) {
    return (
      <p className="note">
        Your profile has never recorded a change, so there is nothing yet for these to be behind.
        Once you save it, every CV written before that moment says so here.
      </p>
    );
  }

  const changed = new Date(staleness.profileUpdatedUtc).toLocaleDateString();

  if (!staleness.anyStale) {
    return (
      <p className="note">
        All {staleness.considered} of your CVs in use were written after your last profile
        change, on {changed}.
      </p>
    );
  }

  return (
    <div className="card" style={{ borderLeft: '2px solid var(--oxide)' }}>
      <h4 className="mini">
        {staleness.stale} of your {staleness.considered} CVs predate your last profile change
      </h4>
      <p className="note" style={{ marginTop: 0 }}>
        You changed your profile on {changed}, and {staleness.stale === 1 ? 'this one was' : 'these were'}{' '}
        written before it. That is not a fault and they are still sendable - a CV written last
        month is still a CV worth sending, and taking them out of selection would leave every
        posting parked over an edit made at lunchtime. It is a question: does the change belong
        in the document?
      </p>
      <p className="note">
        Nothing rewrites them. Read one, decide, and press save - saving is still an edit even if
        you change nothing, which is how you tell this notice you have dealt with it.
        {staleness.current > 0 && ` The other ${staleness.current} are level with the profile.`}
      </p>
    </div>
  );
}

/**
 * One CV: what it is called, when it was written, whether it can be sent, and its own editor.
 *
 * <b>Three badges for three different facts, and they are separate because they go wrong
 * separately.</b> Stale means the profile moved underneath it and it is still perfectly
 * sendable. Needs rendering means the stored files are not these words, so selection will pass
 * it over until a save succeeds. Archived means it was retired on purpose. One badge saying
 * "needs attention" would collapse a nudge, a fault and a decision into a single shrug.
 */
function Variant({ api, variant, open, onOpen, onClose, onWrote }: {
  api: JobPlatformApi;
  variant: CvVariantSummary;
  open: boolean;
  onOpen: () => void;
  onClose: () => void;
  onWrote: (kind: Receipt['kind'], variant: CvVariantDetail) => void;
}) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>();

  const setArchived = (archived: boolean) => {
    setBusy(true);
    setError(undefined);

    api.setCvVariantArchived(variant.variantId, archived)
      .then((stored) => onWrote(archived ? 'archived' : 'restored', stored))
      .catch(setError)
      .finally(() => setBusy(false));
  };

  return (
    <div className="record">
      <div className="record-head">
        <h3>{variant.label}</h3>

        <span className="right">
          {variant.isArchived && <span className="stamp">archived</span>}
          {variant.isStale && (
            <span
              className="stamp warn"
              title="Written before your last profile change. Still sendable - this is a question about whether the change belongs in it, not a fault."
            >
              predates your profile
            </span>
          )}
          {!variant.isRenderCurrent && (
            <span
              className="stamp warn"
              title="The stored PDF is not these words, so a pass will not choose this. Saving it again renders it."
            >
              needs rendering
            </span>
          )}
          <span className="stamp" title={new Date(variant.authoredAtUtc).toLocaleString()}>
            written {authoredAgo(variant.authoredAtUtc)}
          </span>

          <button className="btn primary" aria-expanded={open} onClick={open ? onClose : onOpen}>
            {open ? 'Close' : 'Open'}
          </button>
          <button
            className="btn"
            disabled={busy}
            onClick={() => setArchived(!variant.isArchived)}
            title={variant.isArchived
              ? 'Puts it back into selection. Refused if the library is full or its name has '
                + 'since been taken.'
              : 'Retires it from selection and frees a place. Its words, its files and their '
                + 'hash are kept - this is not a delete.'}
          >
            {busy ? 'Working…' : variant.isArchived ? 'Put back into use' : 'Archive'}
          </button>
        </span>
      </div>

      <p className="note">
        {variant.isArchived
          ? <>Out of selection. Kept because an application that sent it has to stay explicable.</>
          : variant.isSendable
            ? <>In selection: a pass may choose this for a posting it fits.</>
            : <>Out of selection until a render succeeds - the words are stored and safe, but
                there is no current file to upload, so nothing will choose it.</>}
        {variant.renderedAtUtc
          ? <> Last rendered {new Date(variant.renderedAtUtc).toLocaleDateString()}.</>
          : <> Never rendered.</>}
        {variant.sha256 && (
          <>
            {' '}
            <span
              className="stamp"
              title={`SHA-256 of the rendered PDF: ${variant.sha256}. This is what answers "what exactly did we send them" about a file already sitting in somebody else's system.`}
            >
              {variant.sha256.slice(0, 12)}
            </span>
          </>
        )}
      </p>

      {error ? <WriteError error={error} /> : null}

      {open && (
        <Editor
          key={variant.variantId}
          api={api}
          variant={variant}
          onWrote={onWrote}
        />
      )}
    </div>
  );
}

/**
 * The words, and the two writes that are deliberately not one.
 *
 * <b>Renaming and rewriting are separate acts because the server keeps them separate, and it
 * keeps them separate for a reason worth restating here.</b> A rename must not move the
 * authoring date: that timestamp is the whole of the staleness signal and half of the render
 * comparison, so a document nobody has edited would otherwise look freshly written and last
 * week's PDF would be quietly marked current - a paragraph the candidate deleted, going to an
 * employer under their name. Two buttons is also two failures instead of one half-failure: a
 * single save doing both would leave a renamed CV with unsaved words when the second call lost.
 *
 * <b>Saving unchanged words is allowed on purpose.</b> Somebody who reads a CV this page flagged
 * as stale, decides it is still accurate and presses save has answered the notice, so the button
 * refuses only a blank document. Renaming to the same name is refused, because that write means
 * nothing at all.
 *
 * The document is fetched when this opens rather than carried on every row: it is unbounded
 * text, archived variants are kept forever, and a library nobody has pruned would otherwise put
 * every retired CV on the wire to draw a list of labels.
 */
function Editor({ api, variant, onWrote }: {
  api: JobPlatformApi;
  variant: CvVariantSummary;
  onWrote: (kind: Receipt['kind'], variant: CvVariantDetail) => void;
}) {
  const load = useCallback(() => api.cvVariant(variant.variantId), [api, variant.variantId]);
  const detail = useApiResource(load);

  // Told apart from a slow load, the way every other region in this dashboard tells them
  // apart: "this has not come back yet" and "the database was asleep and is getting up" are
  // different facts, and only the second one is worth a retry button. Not the full
  // WakingRegion, which offers the pages that still work - the rest of this page is one of
  // them and is on screen behind this.
  if (detail.state.status === 'waking') {
    return (
      <div className="log-wrap">
        <div className="empty">
          The database pauses when idle and can take up to a minute to wake. Your words are
          stored and are not going anywhere.{' '}
          <button className="linkish" onClick={detail.reload}>Try again</button>.
        </div>
      </div>
    );
  }
  if (detail.state.status === 'error') {
    return (
      <div className="log-wrap">
        <ErrorNote error={detail.state.error} onRetry={detail.reload} />
      </div>
    );
  }
  if (detail.state.status === 'loading') {
    return <div className="log-wrap"><LoadingRegion what="this document" /></div>;
  }

  return <EditorForm api={api} detail={detail.state.data} onWrote={onWrote} />;
}

/** The form, mounted once the stored words are in hand so the boxes start from what is stored. */
function EditorForm({ api, detail, onWrote }: {
  api: JobPlatformApi;
  detail: CvVariantDetail;
  onWrote: (kind: Receipt['kind'], variant: CvVariantDetail) => void;
}) {
  // What is on the server, tracked separately from what is in the boxes and updated by every
  // write that lands. Comparing against the fetched row instead would leave the rename button
  // enabled after a successful rename - offering a write that means nothing, on a form whose
  // other button means something precisely when nothing has changed.
  const [stored, setStored] = useState<CvVariantDetail>(detail);
  const [label, setLabel] = useState(detail.label);
  const [markdown, setMarkdown] = useState(detail.markdown);
  const [saving, setSaving] = useState<'label' | 'markdown'>();
  const [error, setError] = useState<unknown>();

  const trimmedLabel = label.trim();
  const renameable = trimmedLabel.length > 0 && trimmedLabel !== stored.label;

  const rename = () => {
    setSaving('label');
    setError(undefined);

    api.renameCvVariant(detail.variantId, trimmedLabel)
      .then((updated) => { setStored(updated); onWrote('renamed', updated); })
      .catch(setError)
      .finally(() => setSaving(undefined));
  };

  const save = () => {
    setSaving('markdown');
    setError(undefined);

    api.reauthorCvVariant(detail.variantId, markdown)
      .then((updated) => { setStored(updated); onWrote('saved', updated); })
      .catch(setError)
      .finally(() => setSaving(undefined));
  };

  return (
    <div className="log-wrap">
      {error ? <WriteError error={error} /> : null}

      <div style={{ display: 'flex', gap: 8, alignItems: 'flex-end', flexWrap: 'wrap' }}>
        <div style={{ flex: 1, minWidth: 220 }}>
          <Field
            label="What you call it"
            hint={'Yours and the pack’s, never an employer’s: every application uploads '
              + 'the same filename whichever CV it chose, so this name is seen by you and by the '
              + 'run that explains which one it sent.'}
          >
            <input
              value={label}
              maxLength={MAX_LABEL}
              onChange={(e) => setLabel(e.target.value)}
            />
          </Field>
        </div>
        <button className="btn" disabled={saving !== undefined || !renameable} onClick={rename}>
          {saving === 'label' ? 'Renaming…' : 'Rename'}
        </button>
      </div>

      <p className="note">
        Renaming is its own act and leaves the document dated as it is. A rename that moved the
        date would make a CV nobody has edited look freshly written, and mark a stale PDF as
        current.
      </p>

      <Field
        label="Your CV"
        hint={'Markdown, in your own words. Headings, lists, bold and links are laid out by the '
          + 'same renderer that produces what an employer is handed.'}
      >
        <textarea
          rows={18}
          value={markdown}
          maxLength={MAX_MARKDOWN}
          spellCheck
          onChange={(e) => setMarkdown(e.target.value)}
        />
      </Field>

      <div className="row-actions">
        <span className="muted" style={{ fontSize: 13, marginRight: 'auto' }}>
          {markdown.length.toLocaleString()} of {MAX_MARKDOWN.toLocaleString()} characters
        </span>
        <button
          className="btn primary"
          disabled={saving !== undefined || markdown.trim().length === 0}
          onClick={save}
        >
          {saving === 'markdown' ? 'Saving…' : 'Save these words'}
        </button>
      </div>

      <p className="note">
        Saving dates the document as of now, lays it out again into both formats, and reads it
        for the concepts a pass selects on - which go nowhere near your profile, and never widen
        what you are judged to have. Saving without changing a word still counts: if this CV was
        flagged as predating your profile and you have decided it is still accurate, that is how
        you say so.
      </p>

      {/* `stored` rather than the row this form opened with: a save re-renders the document,
          and the buttons below fetch bytes. Reading the older row would offer a download of a
          file the last save replaced, or hide one it has just produced. */}
      <Downloads api={api} variant={stored} />
    </div>
  );
}

/**
 * A new CV, written from nothing by the person whose name is on it.
 *
 * <b>The document box starts empty and there is nothing on this page that would fill it.</b>
 * That is the feature. The gap panel above it, where one is in play, names concepts and counts
 * postings - it says what a CV would have to speak to and how many applications are waiting on
 * it, and it writes not one word of the answer.
 *
 * The label is seeded from the gap, and a seeded label is not the same thing as seeded prose:
 * it is a filing name, it is the default the form opens with rather than a value anybody is
 * held to, and it is the same courtesy the question queue extends by preselecting the narrowest
 * scope. Nothing about it reaches an employer - every application uploads the same filename
 * whichever CV was chosen.
 */
function NewVariant({ api, gap, onCreated, onCancel }: {
  api: JobPlatformApi;
  gap?: CvGap;
  onCreated: (variant: CvVariantDetail) => void;
  onCancel: () => void;
}) {
  const [label, setLabel] = useState(() => suggestedLabel(gap));
  const [markdown, setMarkdown] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<unknown>();

  const ready = label.trim().length > 0 && markdown.trim().length > 0;

  const create = () => {
    setSaving(true);
    setError(undefined);

    api.createCvVariant({ label: label.trim(), markdown })
      .then(onCreated)
      .catch(setError)
      .finally(() => setSaving(false));
  };

  return (
    <div className="card">
      <h2>A new CV</h2>

      {gap && (
        <div className="card" style={{ marginBottom: 'var(--s4)' }}>
          <h4 className="mini">What this one has to cover</h4>
          <div className="chips">
            {gap.concepts.map((concept) => (
              <span key={concept.key} className="pill" title={concept.key}>
                {concept.label} · {concept.postings}
              </span>
            ))}
          </div>
          <p className="note">
            <b>{gap.postings}</b> applyable posting{gap.postings === 1 ? '' : 's'}{' '}
            {gap.postings === 1 ? 'is' : 'are'} waiting on a CV that speaks to these, and the
            number beside each one is how many of them ask for it. Nothing here writes any of it:
            the concepts are what the adverts asked for, and the document is yours.
          </p>
        </div>
      )}

      {error ? <WriteError error={error} /> : null}

      <Field
        label="What you will call it"
        hint="Short, and distinguishable at a glance - Backend .NET, AI and data platforms."
      >
        <input
          value={label}
          maxLength={MAX_LABEL}
          placeholder="Backend .NET"
          onChange={(e) => setLabel(e.target.value)}
        />
      </Field>

      <Field
        label="Your CV"
        hint={'Markdown, in your own words. This box is the source of truth: the PDF and the '
          + 'DOCX an employer is handed are both laid out from it, and nothing rewrites it.'}
      >
        <textarea
          rows={20}
          value={markdown}
          maxLength={MAX_MARKDOWN}
          spellCheck
          placeholder={'# Your name\n\nA line about what you do.\n\n## Experience\n\n'
            + '**Job title**, Employer — 2022 to now\n\n- What you did, and what came of it.'}
          onChange={(e) => setMarkdown(e.target.value)}
        />
      </Field>

      <div className="row-actions">
        <span className="muted" style={{ fontSize: 13, marginRight: 'auto' }}>
          {markdown.length.toLocaleString()} of {MAX_MARKDOWN.toLocaleString()} characters
        </span>
        <button className="btn" disabled={saving} onClick={onCancel}>Cancel</button>
        <button className="btn primary" disabled={saving || !ready} onClick={create}>
          {saving ? 'Storing…' : 'Add it to the library'}
        </button>
      </div>

      <p className="note">
        Storing it lays it out into both formats and reads it for the concepts a pass selects on.
        Those concepts are for choosing between your CVs and for nothing else - they never reach
        your profile, never move a match score, and never widen what you are judged to have. A CV
        is written from the profile, so letting its reading back in would let a document inflate
        the record it came from.
      </p>
    </div>
  );
}

/**
 * The rendered files, so somebody can see what an employer is actually handed.
 *
 * <b>A library you cannot open is a library you have to trust.</b> The template is the part of
 * this system most likely to disappoint, and the design's answer to a disappointing template is
 * to fix it once for every variant at the same time - which nobody does without looking at the
 * output. So the file is reachable from the row that produced it.
 *
 * <b>The PDF is required and the DOCX is not, and the row does not say which the render got.</b>
 * A variant with only a PDF can be uploaded to most forms and one with no PDF can be uploaded
 * nowhere, so the renderer records nothing at all when the PDF fails and records a null DOCX
 * path when only that one does. The list response carries the PDF's digest and no DOCX flag, so
 * the second button here can fail where the first will not. Reported when it happens rather than
 * pre-empted by hiding it: a missing DOCX is worth knowing about, because Workday parses that
 * format more reliably than it parses a PDF.
 *
 * A blob and a synthetic link rather than a bare anchor, for the reason the application download
 * uses one: these bytes are somebody's CV, the route requires a bearer token, and an anchor
 * cannot carry a header.
 */
function Downloads({ api, variant }: { api: JobPlatformApi; variant: CvVariantSummary }) {
  const [busy, setBusy] = useState<'pdf' | 'docx'>();
  const [error, setError] = useState<unknown>();

  if (!variant.sha256) {
    return (
      <p className="note">
        There are no rendered files for this one yet, so there is nothing to look at. Saving it
        lays it out; if that keeps failing, the words are still safe here.
      </p>
    );
  }

  const fetchFile = (format: 'pdf' | 'docx') => {
    setBusy(format);
    setError(undefined);

    api.cvVariantFile(variant.variantId, format)
      .then((blob) => {
        const url = URL.createObjectURL(blob);
        const link = document.createElement('a');
        link.href = url;
        link.download = `${DOWNLOAD_STEM}.${format}`;
        link.click();
        URL.revokeObjectURL(url);
      })
      .catch(setError)
      .finally(() => setBusy(undefined));
  };

  return (
    <div className="draft">
      <h4 className="mini">What an employer gets</h4>

      {error ? <ErrorNote error={error} /> : null}

      <div className="row-actions" style={{ justifyContent: 'flex-start' }}>
        <button className="btn" disabled={busy !== undefined} onClick={() => fetchFile('pdf')}>
          {busy === 'pdf' ? 'Fetching…' : 'The PDF'}
        </button>
        <button className="btn" disabled={busy !== undefined} onClick={() => fetchFile('docx')}>
          {busy === 'docx' ? 'Fetching…' : 'The DOCX'}
        </button>
      </div>

      <p className="note">
        Both are laid out from the words above by one controlled template, so the bytes are the
        same here as in the container an application uploads from - which is what makes the
        digest beside this CV an answer to &ldquo;what exactly did we send them&rdquo;. The file
        saves as <b>{DOWNLOAD_STEM}</b>, the one name every application sends whichever CV it
        chose: a filename carrying &ldquo;AI engineer&rdquo; would tell an employer that a
        different CV is kept for other roles, in the file list, before anybody opens it.
        {!variant.isRenderCurrent && <> These files are older than the words above; save to lay
          them out again.</>}
      </p>
    </div>
  );
}

/**
 * CVs you are missing: the postings nothing could be sent to, and what would unblock them.
 *
 * <b>This is the half that earns the change.</b> An abstention that says only "no" throws away
 * the most useful signal in the system: the pass that declined to send a CV has just computed,
 * for every posting it parked, exactly what would have made it possible. So this reads as a
 * brief with a business case attached rather than as a list of failures - the postings are not
 * errors, and the person reading has not done anything wrong.
 *
 * <b>Aggregated, never per posting.</b> Fifty "could not apply" notices is a queue nobody reads;
 * one ranked list of three is a Saturday afternoon with an obvious payoff. That is enforced
 * server-side - there is no per-posting version and no limit to raise - and the floor and the
 * ceiling are stated below so that a gap blocking a single posting being absent reads as the
 * floor working rather than as something missing.
 *
 * <b>Its own request states, and they do not take the page down.</b> The library above is
 * somebody's own documents; this is a query over the blocked queue. A slow or broken brief must
 * leave the CVs editable, which is the same split WakingRegion exists to make.
 */
function GapBrief({ state, error, brief, hasRoom, onRetry, onWrite }: {
  state: 'loading' | 'ok' | 'waking' | 'error';
  error: unknown;
  brief: CvGapBriefResponse | undefined;
  hasRoom: boolean;
  onRetry: () => void;
  onWrite: (gap: CvGap) => void;
}) {
  return (
    <div className="appgroup">
      <h2>CVs you are missing</h2>

      {state === 'loading' && <LoadingRegion what="the postings nothing could be sent to" />}

      {state === 'waking' && (
        <div className="empty">
          The blocked queue is served from SQL, which pauses when it is idle and can take up to a
          minute to wake. Your CVs above are unaffected.{' '}
          <button className="linkish" onClick={onRetry}>Try the brief again</button>.
        </div>
      )}

      {state === 'error' && <ErrorNote error={error} onRetry={onRetry} />}

      {brief && brief.blockedPostings === 0 && (
        <div className="empty">
          No applyable posting is waiting on a CV you have not written. Everything the loop can
          reach, it has something to send to.
        </div>
      )}

      {brief && brief.blockedPostings > 0 && brief.gaps.length === 0 && (
        <div className="card">
          <h4 className="mini">Worth reporting exactly as it stands</h4>
          <p className="note" style={{ marginTop: 0 }}>
            <b>{brief.blockedPostings}</b> applyable posting
            {brief.blockedPostings === 1 ? ' is' : 's are'} parked for want of a CV, and none of
            them names a concept a CV could be written about - what is blocking them is generic
            tags, or keys this system&rsquo;s vocabulary does not carry. That is a fault in the
            selection or in the vocabulary rather than a document you can write, so there is
            nothing here for you to do about it.
          </p>
        </div>
      )}

      {brief && brief.gaps.length > 0 && (
        <>
          <p className="lede">
            <b>{brief.blockedPostings}</b> applyable posting
            {brief.blockedPostings === 1 ? ' is' : 's are'} waiting on a CV you have not written.
          </p>

          {brief.gaps.map((gap, index) => (
            <Gap
              key={gap.concepts[0]?.key ?? index}
              gap={gap}
              top={brief.gaps[0]?.postings ?? gap.postings}
              rank={index}
              hasRoom={hasRoom}
              onWrite={() => onWrite(gap)}
            />
          ))}

          <p className="note">
            Ranked greedily, so the second gap counts only the postings the first would not have
            unblocked - two gaps of nine over the same nine adverts would otherwise read as
            eighteen applications of payoff. The total above is larger than the gaps add up to,
            deliberately: it also counts gaps below the floor of{' '}
            {brief.minimumPostingsPerGap} posting
            {brief.minimumPostingsPerGap === 1 ? '' : 's'}, gaps past the{' '}
            {brief.maxGaps === 3 ? 'third' : `${brief.maxGaps}th`}, and postings whose
            requirements cannot be named. None of those three is something to write a CV about,
            so none of them is broken out.
          </p>

          <p className="note">
            A posting parked for want of a CV stays out of the queue until one that covers it
            exists, and comes back on the first run afterwards - so writing one of these returns
            the applications it names, rather than merely making the next run try again.
          </p>
        </>
      )}
    </div>
  );
}

/**
 * One CV worth writing, with the concepts it has to speak to and what it would unblock.
 *
 * The concepts are named rather than the postings, and that is the design's own decision: this
 * is a brief for a document, so it says what the document must be about. A list of eleven job
 * titles would be a queue again.
 */
function Gap({ gap, top, rank, hasRoom, onWrite }: {
  gap: CvGap;
  top: number;
  rank: number;
  hasRoom: boolean;
  onWrite: () => void;
}) {
  return (
    <div className="record">
      <div className="record-head">
        <h3>{inWords(gap.concepts.map((concept) => concept.label))}</h3>

        <span className="right">
          <span className="stamp">
            {gap.postings} posting{gap.postings === 1 ? '' : 's'}
          </span>
          <button
            className="btn primary"
            disabled={!hasRoom}
            title={hasRoom
              ? undefined
              : 'Your library is full. Archive a CV you no longer send and this becomes '
                + 'available - archiving keeps the document and frees a place.'}
            onClick={onWrite}
          >
            Write this CV
          </button>
        </span>
      </div>

      <div className="rank">
        <div>
          <div className="rank-row">
            <span className="nm">
              {rank === 0 ? 'The biggest gap' : 'And then'} — applications it would unblock
            </span>
            <span className="vl">{gap.postings}</span>
          </div>
          <div className="measure">
            <i style={{ width: `${top > 0 ? Math.round((gap.postings / top) * 100) : 0}%` }} />
          </div>
        </div>
      </div>

      <p className="note">
        Of the {gap.postings} posting{gap.postings === 1 ? '' : 's'} this CV is aimed at,{' '}
        {gap.concepts.map((concept, index) => (
          <span key={concept.key}>
            {index > 0 && (index === gap.concepts.length - 1 ? ' and ' : ', ')}
            <b>{concept.postings}</b> ask for {concept.label}
          </span>
        ))}
        . None of your CVs in use covers {gap.concepts.length === 1 ? 'it' : 'them'}.
      </p>
    </div>
  );
}

/**
 * What a write did, stated as the consequence rather than as "saved".
 *
 * The half worth saying is invisible: a CV entering or leaving selection changes what an
 * unattended run may send, and the render that makes that true happens after the save returns.
 * "Saved" is the one thing about this write nobody needed telling.
 */
function ReceiptNote({ receipt, onDismiss }: { receipt: Receipt; onDismiss: () => void }) {
  const { kind, variant } = receipt;

  return (
    <div className="undobar">
      <span>
        {kind === 'created' && <><b>{variant.label}</b> is in your library.</>}
        {kind === 'saved' && <><b>{variant.label}</b> is stored in your words, dated now.</>}
        {kind === 'renamed' && (
          <>Now called <b>{variant.label}</b>. Nothing else moved — it is dated as it was, and
            its rendered files still describe it.</>
        )}
        {kind === 'archived' && (
          <><b>{variant.label}</b> is out of selection and its place in the library is free. Its
            words, its files and their hash are kept, so the applications that sent it stay
            explicable.</>
        )}
        {kind === 'restored' && (
          <><b>{variant.label}</b> is back in use, as old as it was — putting a CV back is not
            authoring it.</>
        )}

        {(kind === 'created' || kind === 'saved') && (
          variant.isSendable
            ? <> It is laid out and read for selection, so a pass may choose it. Nothing has been
                sent.</>
            : <> The words are stored and safe, but laying them out did not succeed — so it stays
                out of selection until a save renders it. Nothing has been sent.</>
        )}
      </span>
      <button className="btn" onClick={onDismiss}>Dismiss</button>
    </div>
  );
}

/**
 * A refusal told apart from a fault.
 *
 * A 409 is the state of the library rather than a mistake in what was typed: the label was free
 * yesterday and the cap was not spent last week. Reporting it as an error would send somebody
 * looking at their own document for the problem. The server's own sentence carries the numbers
 * and what to do about them, so it is shown rather than replaced - a second copy of "the cap is
 * six" written here is a second copy free to go stale.
 */
function WriteError({ error }: { error: unknown }) {
  if (error instanceof ApiError && (error.status === 409 || error.status === 400)) {
    return (
      <div className="err">
        <strong>
          {error.status === 409
            ? 'The library would not take that, and not because of what you wrote.'
            : 'That cannot be stored as it stands.'}
        </strong>
        {error.detail && <div className="muted" style={{ marginTop: 4 }}>{error.detail}</div>}
        {!error.detail && (
          <div className="muted" style={{ marginTop: 4 }}>
            Nothing was written and nothing was lost — what is in the boxes is still there.
          </div>
        )}
      </div>
    );
  }

  return <ErrorNote error={error} />;
}

/**
 * The gap a new CV is being written for, found by the seed concept the link carried.
 *
 * The seed rather than a position in the list: a brief is recomputed every time it is read, so
 * "the second gap" names a different document tomorrow, and a link somebody kept would open the
 * editor against the wrong one. A key that has since been covered simply finds nothing, and the
 * form opens blank - which is the honest outcome, because the gap it was for is gone.
 */
function findGap(brief: CvGapBriefResponse | undefined, seed: string | undefined): CvGap | undefined {
  if (!brief || !seed) return undefined;

  return brief.gaps.find((gap) => gap.concepts[0]?.key === seed);
}

/**
 * A first name for a CV written against a gap.
 *
 * A filing name and not a word of the document: it is what the picker shows and what a run says
 * when it explains which CV it sent, and every application uploads the same filename whichever
 * one was chosen. Two concepts at most, because a label is read at a glance and a third name
 * makes it a sentence.
 */
function suggestedLabel(gap: CvGap | undefined): string {
  if (!gap) return '';

  return gap.concepts
    .slice(0, 2)
    .map((concept) => concept.label)
    .join(' and ')
    .slice(0, MAX_LABEL);
}

/** A list as somebody would say it: "Kubernetes, Terraform and platform engineering". */
function inWords(labels: string[]): string {
  if (labels.length === 0) return 'Concepts this system cannot name';
  if (labels.length === 1) return labels[0] as string;

  return `${labels.slice(0, -1).join(', ')} and ${labels[labels.length - 1] as string}`;
}

/**
 * How long ago a CV was written, as an age rather than a timestamp.
 *
 * "Written 04:12" looks the same whether that was this morning or in March, and the age is the
 * whole reason the date is on the row: a library goes wrong by getting old, not by being
 * written at an unusual hour. The exact moment is on the badge's title for anybody who needs it.
 */
function authoredAgo(iso: string): string {
  const hours = (Date.now() - new Date(iso).getTime()) / 3_600_000;

  if (hours < 24) return 'today';
  if (hours < 48) return 'yesterday';

  const days = Math.round(hours / 24);

  if (days < 60) return `${days} days ago`;

  const months = Math.round(days / 30);

  return months < 24 ? `${months} months ago` : new Date(iso).toLocaleDateString();
}
