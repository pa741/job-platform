import { useCallback, useState } from 'react';
import { ApiError, type JobPlatformApi } from '../api/client';
import type { ApplicationDetail, MatchDetail, MatchSummary, Submission } from '../api/types';
import { ErrorNote } from '../components/Primitives';
import { useApiResource } from '../components/useApiResource';
import { WakingRegion, LoadingRegion } from '../components/WakingRegion';
import type { PageId } from '../routing/route';

const PAGE_SIZE = 25;

/** Wording for each scoring axis. The API returns stable identifiers; the labels live here. */
const AXIS: Record<string, string> = {
  requiredSkills: 'Essential skills',
  preferredSkills: 'Other skills',
  seniority: 'Seniority',
  experience: 'Experience',
  workArrangement: 'Working arrangement',
  salary: 'Salary',
  location: 'Location',
};

/** How a held concept satisfied a required one. Worth showing: they are not the same claim. */
const RELATION: Record<string, string> = {
  Exact: 'the same concept',
  Specialisation: 'you hold something more specific',
  Generalisation: 'you hold something broader',
  Implied: 'implied by what you hold',
  Related: 'comparable, not equivalent',
  Superseded: 'you hold the successor',
};

const ARRANGEMENT: Record<string, string> = {
  Remote: 'Remote', Hybrid: 'Hybrid', OnSite: 'On-site', Unknown: 'Not stated',
};

/** Recent enough to be "since you last looked" on a nightly sweep, without a clock to sync. */
const OVERNIGHT_HOURS = 20;

function isRecent(iso: string | null): boolean {
  return iso !== null && (Date.now() - new Date(iso).getTime()) / 3_600_000 < OVERNIGHT_HOURS;
}

/**
 * The shortlist, and the landing page.
 *
 * <b>Ordered by overall fit, not by the number on the left.</b> The score orders the corpus
 * well and inverts inside its own top band - measured against the model's judgement, the
 * 90-100 band carries a higher share of Weak verdicts than the two below it - so the API
 * orders by a fusion of the score and how closely each advert reads like the profile. Each
 * entry says where it ranks and where it would have sat on score alone, because a list sorted
 * by something other than the number on screen is otherwise indistinguishable from a broken
 * one. `rankScore` itself is never rendered: it is normalised over this candidate's pool, so
 * it is not comparable between candidates or between nights.
 *
 * The lede is the day's decisions rather than a summary of the corpus. That is the whole
 * argument for this page being the one that loads.
 */
export function Shortlist({ api, go }: { api: JobPlatformApi; go: (page: PageId) => void }) {
  const [minScore, setMinScore] = useState(40);
  const [assessedOnly, setAssessedOnly] = useState(false);
  const [showDismissed, setShowDismissed] = useState(false);
  const [offset, setOffset] = useState(0);

  const [open, setOpen] = useState<number>();
  const [undo, setUndo] = useState<{ postingId: number; title: string; company: string | null }>();
  const [noProfile, setNoProfile] = useState(false);
  const [actionError, setActionError] = useState<unknown>();

  const load = useCallback(async () => {
    try {
      const result = await api.matches({
        minScore, assessedOnly, limit: PAGE_SIZE, offset, dismissed: showDismissed,
      });
      setNoProfile(false);
      return result.items;
    } catch (cause) {
      // A 404 here means "you have no profile", not a failure. Sending the person to the form
      // is the useful response; an error box is not.
      if (cause instanceof ApiError && cause.status === 404) {
        setNoProfile(true);
        return [];
      }
      throw cause;
    }
  }, [api, minScore, assessedOnly, offset, showDismissed]);

  const matches = useApiResource(load);

  // The cross-page half of the lede. On this page rather than in the shell because the shell's
  // only call is the Cosmos-backed bootstrap, and putting a SQL read there would gate the
  // whole dashboard on a database that pauses.
  const loadSubmissions = useCallback(
    () => api.submissions().then((r) => r.items).catch(() => [] as Submission[]),
    [api],
  );
  const submissions = useApiResource(loadSubmissions);

  if (noProfile) {
    return (
      <div className="empty">
        You have no profile yet. Fill in the profile form and matches appear after the next
        nightly sweep.
      </div>
    );
  }

  if (matches.state.status === 'waking') {
    return <WakingRegion what="Your shortlist" onRetry={matches.reload} go={go} />;
  }

  if (matches.state.status === 'error') {
    return <ErrorNote error={matches.state.error} onRetry={matches.reload} />;
  }

  if (matches.state.status === 'loading') return <LoadingRegion what="your shortlist" />;

  const items = matches.state.data;

  // Where each row would sit on score alone. Computed over the page in hand and described as
  // such: the API returns no global position, and implying one it did not send would be worse
  // than saying nothing.
  const byScore = [...items].sort((a, b) => b.score - a.score).map((m) => m.postingId);

  const judged = items.filter((m) => isRecent(m.assessedAtUtc)).length;
  const sent = submissions.state.data ?? [];
  const quiet = sent.filter((s) => s.isStale);
  const unsent = sent.filter((s) => s.phase === null);

  const dismiss = (match: MatchSummary) => {
    setActionError(undefined);
    api.setMatchDismissed(match.postingId, true)
      .then(() => {
        setUndo({ postingId: match.postingId, title: match.title, company: match.company });
        matches.reload();
      })
      .catch(setActionError);
  };

  const restore = () => {
    if (!undo) return;
    api.setMatchDismissed(undo.postingId, false)
      .then(() => { setUndo(undefined); matches.reload(); })
      .catch(setActionError);
  };

  return (
    <div className="stack">
      <p className="lede">
        {items.length === 0 && !showDismissed
          ? <>Nothing clears {minScore} today.</>
          : <>
              <b>{items.length}{items.length === PAGE_SIZE ? '+' : ''}</b> roles clear your line
              {judged > 0 && <>, and the model judged <b>{judged}</b> of them overnight</>}.
            </>}
        {unsent.length > 0 && (
          <> <button className="linkish" onClick={() => go('applications')}>
            {unsent.length} draft{unsent.length === 1 ? '' : 's'}
          </button> {unsent.length === 1 ? 'is' : 'are'} written and unsent.</>
        )}
        {quiet[0] && (
          <> <button className="linkish" onClick={() => go('applications')}>
            {quiet[0].company ?? 'One employer'}
          </button> has said nothing for {quiet[0].eventCount === 0 ? 'a fortnight' : 'two weeks or more'}.</>
        )}
      </p>

      <p className="lede-note">
        Ordered by overall fit, not by the number on the left: the score orders the corpus well
        and inverts inside its own top band, so each entry says where it ranks and where it
        would sit on score alone within this page. Matches are computed nightly, so a profile
        saved today is scored tomorrow morning.
      </p>

      {actionError ? <ErrorNote error={actionError} /> : null}

      {undo && (
        <div className="undobar">
          <span>
            Set aside <b>{undo.title}</b>{undo.company ? ` at ${undo.company}` : ''}. Tonight&rsquo;s
            sweep will spend its judgement budget elsewhere, and it will not be back tomorrow.
          </span>
          <button className="btn" onClick={restore}>Undo</button>
        </div>
      )}

      <div className="filters">
        <label>
          Minimum score <span className="pill">{minScore}</span>
          <input
            type="range" min={0} max={90} step={5} value={minScore}
            onChange={(e) => { setOffset(0); setMinScore(Number(e.target.value)); }}
          />
        </label>

        <label className="check">
          <input
            type="checkbox" checked={assessedOnly}
            onChange={(e) => { setOffset(0); setAssessedOnly(e.target.checked); }}
          />
          Only roles the model has judged
        </label>

        <button
          className="btn"
          aria-pressed={showDismissed}
          onClick={() => { setOffset(0); setUndo(undefined); setShowDismissed(!showDismissed); }}
        >
          {showDismissed ? 'Back to the shortlist' : 'What I set aside'}
        </button>
      </div>

      {items.length === 0 && (
        <div className="empty">
          {showDismissed
            ? 'Nothing set aside yet.'
            : `Nothing above ${minScore}${assessedOnly ? ' that the model has judged' : ''}.`}
        </div>
      )}

      {items.map((match, index) => (
        <Entry
          key={match.postingId}
          api={api}
          match={match}
          rank={offset + index + 1}
          scoreRank={byScore.indexOf(match.postingId) + 1}
          expanded={open === match.postingId}
          onToggle={() => setOpen(open === match.postingId ? undefined : match.postingId)}
          onDismiss={showDismissed ? undefined : () => dismiss(match)}
          onRestore={showDismissed ? () => {
            api.setMatchDismissed(match.postingId, false).then(matches.reload).catch(setActionError);
          } : undefined}
        />
      ))}

      {(offset > 0 || items.length === PAGE_SIZE) && (
        <div className="pager">
          <button className="btn" disabled={offset === 0} onClick={() => setOffset(Math.max(0, offset - PAGE_SIZE))}>
            Previous
          </button>
          {/* The API returns no hasMore on this route, so a short page is the only signal
              there is. Said plainly rather than dressed up as a total. */}
          <button className="btn" disabled={items.length < PAGE_SIZE} onClick={() => setOffset(offset + PAGE_SIZE)}>
            Next
          </button>
        </div>
      )}
    </div>
  );
}

function Entry({ api, match, rank, scoreRank, expanded, onToggle, onDismiss, onRestore }: {
  api: JobPlatformApi;
  match: MatchSummary;
  rank: number;
  scoreRank: number;
  expanded: boolean;
  onToggle: () => void;
  onDismiss?: () => void;
  onRestore?: () => void;
}) {
  const moved = rank !== scoreRank;

  return (
    <article className="entry">
      <div className="figcol">
        <div className="score">{match.score}</div>
        <div className="of">
          ranked <b>{rank}</b>{moved && <>, scores <b>{scoreRank}</b></>}
        </div>
      </div>

      <div>
        <button className="entry-title" aria-expanded={expanded} onClick={onToggle}>
          {match.title}
        </button>

        <div className="entry-meta">
          <span>{match.company ?? 'Unnamed employer'}</span>
          <span className="sep">/</span>
          <span>{ARRANGEMENT[match.workArrangement] ?? match.workArrangement}</span>
          <span className="sep">/</span>
          <span>{salary(match)}</span>

          <Verdict match={match} />

          {match.requiredGapCount > 0 && (
            <span className="stamp warn">{match.requiredGapCount} unmet</span>
          )}

          {/* Coverage on every row, not only where it is alarming. A 100 over every axis and a
              100 over one are the same number and very different claims. */}
          <span className="stamp">{Math.round(match.coverage * 100)}% assessed</span>

          {isRecent(match.assessedAtUtc) && <span className="stamp on">judged overnight</span>}

          {onDismiss && <button className="dismiss" onClick={onDismiss}>Not for me</button>}
          {onRestore && <button className="dismiss" onClick={onRestore}>Put it back</button>}
        </div>

        {expanded && <Detail api={api} postingId={match.postingId} />}
      </div>
    </article>
  );
}

/**
 * One match in full: the breakdown, and the button that costs money.
 *
 * The breakdown is why this exists rather than the score being a column. A number with nothing
 * behind it is a number nobody acts on; "you meet nine of their eleven requirements, and the
 * two you do not are Terraform and a security clearance" is something a person can act on.
 */
function Detail({ api, postingId }: { api: JobPlatformApi; postingId: number }) {
  const load = useCallback(() => api.match(postingId), [api, postingId]);
  const detail = useApiResource<MatchDetail>(load);

  const [draft, setDraft] = useState<ApplicationDetail>();
  const [instructions, setInstructions] = useState('');
  const [generating, setGenerating] = useState(false);
  const [error, setError] = useState<unknown>();

  if (detail.state.status === 'waking') {
    return <WakingRegion what="This breakdown" onRetry={detail.reload} />;
  }
  if (detail.state.status === 'error') {
    return <ErrorNote error={detail.state.error} onRetry={detail.reload} />;
  }
  if (detail.state.status === 'loading') return <LoadingRegion what="the breakdown" />;

  const match = detail.state.data;

  // Zero weight means the posting said nothing on that axis, so it was dropped rather than
  // failed. The heaviest axis sets the scale: drawing two axes the same length says one
  // carrying a third of the score and one carrying a tenth are equally important.
  const scored = match.components.filter((c) => c.weight > 0);
  const maxWeight = Math.max(...scored.map((c) => c.weight), 0.01);

  const generate = () => {
    setGenerating(true);
    setError(undefined);

    api.generateApplication(postingId, instructions.trim() || undefined)
      .then(setDraft)
      .catch(setError)
      .finally(() => setGenerating(false));
  };

  return (
    <div className="expand">
      {error ? <GenerateError error={error} /> : null}

      <div className="two">
        <div>
          <h4 className="mini">How the score was reached</h4>
          <p className="note">Bar length is how much each axis weighs; the fill is how it scored.</p>

          <div className="axes">
            {scored.map((component) => (
              <div className="ax" key={component.name}>
                <span className="nm">{AXIS[component.name] ?? component.name}</span>
                <span className="tr" style={{ width: `${(component.weight / maxWeight) * 100}%` }}>
                  <i className="fl" style={{ width: `${Math.round(component.score * 100)}%` }} />
                </span>
                <span className="vl">
                  {Math.round(component.score * 100)}% × {component.weight.toFixed(2)}
                </span>
              </div>
            ))}
          </div>

          <p className="note">
            An axis the advert says nothing about is dropped and the remaining weights
            renormalised, never scored zero — a zero would show a penalty that was never applied.
          </p>

          {match.matched.length > 0 && (
            <>
              <h4 className="mini">How your concepts satisfied theirs</h4>
              <ul className="tight">
                {match.matched.slice(0, 8).map((m) => (
                  <li key={`${m.required}:${m.held}`}>
                    <b>{m.heldLabel}</b> → {m.requiredLabel} — {m.relation.toLowerCase()},{' '}
                    {RELATION[m.relation] ?? 'related'}
                  </li>
                ))}
              </ul>
            </>
          )}
        </div>

        <div>
          <h4 className="mini">What the model said</h4>

          {match.assessedAtUtc ? (
            <>
              <p className="quote">{match.rationale}</p>
              {match.strengths.length > 0 && (
                <>
                  <h4 className="mini">Lands well</h4>
                  <ul className="tight">{match.strengths.map((s) => <li key={s}>{s}</li>)}</ul>
                </>
              )}
              {match.assessmentGaps.length > 0 && (
                <>
                  <h4 className="mini">Worth knowing</h4>
                  <ul className="tight">{match.assessmentGaps.map((g) => <li key={g}>{g}</li>)}</ul>
                </>
              )}
            </>
          ) : (
            <p className="quote">
              Not judged yet. Each night the budget is split thirty from the top of the ranking
              and ten drawn evenly across the score bands, so a mid-scoring advert is reached on
              its own account rather than waiting behind every higher number.
            </p>
          )}

          <h4 className="mini">Tailored application</h4>
          <p className="note">
            Costs a call to the writing model, and the gaps above are handed to it as the
            claims it must not make.
          </p>

          <div className="row-actions">
            <input
              value={instructions}
              placeholder="Anything to lead with (optional)"
              onChange={(e) => setInstructions(e.target.value)}
            />
            <button className="btn primary" disabled={generating} onClick={generate}>
              {generating ? 'Writing…' : 'Write CV and cover letter'}
            </button>
          </div>
        </div>
      </div>

      {draft && <DraftView api={api} draft={draft} />}
    </div>
  );
}

/**
 * The three ways generating can fail, told apart.
 *
 * A 503 means this deployment has no model provider, which is a configuration rather than a
 * fault and retrying will never fix it. A 502 means the model answered with nothing usable,
 * which retrying might. Reporting both as "something went wrong" leaves somebody clicking a
 * button that cannot work.
 */
function GenerateError({ error }: { error: unknown }) {
  if (error instanceof ApiError && error.status === 503) {
    return (
      <div className="err">
        <strong>No model provider is configured for this deployment.</strong>
        <div className="muted" style={{ marginTop: 4 }}>
          Scoring and matching still run; only the writing pass needs a provider. Set
          <code> JP_AI_PROVIDER </code> and redeploy to turn it on.
        </div>
      </div>
    );
  }

  if (error instanceof ApiError && error.status === 502) {
    return (
      <div className="err">
        <strong>The model returned nothing usable.</strong>
        <div className="muted" style={{ marginTop: 4 }}>
          Nothing was saved and nothing was charged for a document. Trying again usually works.
        </div>
      </div>
    );
  }

  return <ErrorNote error={error} />;
}

function DraftView({ api, draft }: { api: JobPlatformApi; draft: ApplicationDetail }) {
  const [downloading, setDownloading] = useState<string>();
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

  return (
    <div className="draft">
      {error ? <ErrorNote error={error} /> : null}

      <div className="row-actions">
        <span className="stamp">Draft {draft.revision}</span>
        <button
          className="btn" disabled={downloading === 'cv'}
          onClick={() => download('cv', `CV-${draft.postingTitle}.pdf`)}
        >
          {downloading === 'cv' ? 'Preparing…' : 'Download CV (PDF)'}
        </button>
        <button
          className="btn" disabled={downloading === 'cover-letter'}
          onClick={() => download('cover-letter', `Cover-letter-${draft.postingTitle}.pdf`)}
        >
          {downloading === 'cover-letter' ? 'Preparing…' : 'Download cover letter (PDF)'}
        </button>
      </div>

      {draft.emphasised.length > 0 && (
        <>
          <h4 className="mini">This draft leads with</h4>
          <ul className="tight">{draft.emphasised.map((item) => <li key={item}>{item}</li>)}</ul>
        </>
      )}

      {/* Preformatted, never rendered as markup. The PDF is the artefact; this is a preview,
          and model output has no business being interpreted as HTML in the browser. */}
      <h4 className="mini">CV</h4>
      <pre className="markdown">{draft.curriculumVitaeMarkdown}</pre>

      <h4 className="mini">Cover letter</h4>
      <pre className="markdown">{draft.coverLetterMarkdown}</pre>
    </div>
  );
}

/**
 * The verdict, with `Unknown` kept apart from null.
 *
 * `CandidacyVerdict` is Weak | Possible | Strong | Unknown, and Unknown is a judgement that
 * came back inconclusive - the model read it and would not commit. Null is no judgement at
 * all. Collapsing the two loses the distinction the enum exists to carry.
 */
function Verdict({ match }: { match: MatchSummary }) {
  if (match.verdict === null) return <span className="stamp">not judged yet</span>;
  if (match.verdict === 'Unknown') return <span className="stamp">judged inconclusive</span>;

  const tone = match.verdict === 'Strong' ? 'good' : match.verdict === 'Weak' ? 'warn' : '';

  return (
    <span className={`stamp ${tone}`} title={match.rationale ?? undefined}>
      {match.verdict}
      {match.assessmentScore != null && ` · ${match.assessmentScore}`}
    </span>
  );
}

/**
 * The annualised pair, never the raw columns.
 *
 * `annualSalaryMin/Max` covers more postings and puts a day rate on the same scale as a
 * salary, which is the only way the two can sit in one column at all.
 */
function salary(match: MatchSummary): string {
  if (match.annualSalaryMin == null && match.annualSalaryMax == null) return 'not stated';

  const currency = match.annualSalaryCurrency ?? '';
  const format = (value: number) => `${currency}${Math.round(value / 1000)}k`;

  if (match.annualSalaryMin != null && match.annualSalaryMax != null) {
    return `${format(match.annualSalaryMin)}–${format(match.annualSalaryMax)}`;
  }

  return format((match.annualSalaryMax ?? match.annualSalaryMin)!);
}
