import { useCallback, useEffect, useState } from 'react';
import type { JobPlatformApi } from '../api/client';
import { ApiError } from '../api/client';
import type { ApplicationDetail, MatchDetail, MatchSummary } from '../api/types';
import { Card, ErrorNote } from '../components/Primitives';

const PAGE_SIZE = 25;

const ARRANGEMENT: Record<string, string> = {
  Remote: 'Remote',
  Hybrid: 'Hybrid',
  OnSite: 'On-site',
};

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
  Exact: 'exact',
  Specialisation: 'you have something more specific',
  Generalisation: 'you have something broader',
  Implied: 'implied by what you have',
  Related: 'you have something comparable',
  Superseded: 'you have the predecessor',
};

/**
 * The candidate's scored matches.
 *
 * Read-only, and deliberately cheap to open: the arithmetic runs nightly over every posting and
 * the model judges the shortlist behind it, so by the time this page loads the work is done.
 * A shortlist that costs model calls to browse is one nobody can afford to look at.
 *
 * Both numbers are shown and neither is presented as the answer. The score says how much of the
 * posting the profile covers; the verdict says whether the rest matters. A 58 the model called
 * strong is the most interesting row on the page, and showing only one of them deletes it.
 */
export function Matches({ api }: { api: JobPlatformApi }) {
  const [items, setItems] = useState<MatchSummary[]>();
  const [minScore, setMinScore] = useState(40);
  const [assessedOnly, setAssessedOnly] = useState(false);
  const [offset, setOffset] = useState(0);
  const [selected, setSelected] = useState<number>();
  const [error, setError] = useState<unknown>();
  const [noProfile, setNoProfile] = useState(false);

  const load = useCallback(() => {
    setError(undefined);
    setNoProfile(false);

    api.matches({ minScore, assessedOnly, limit: PAGE_SIZE, offset })
      .then((result) => setItems(result.items))
      .catch((cause) => {
        // A 404 here means "you have no profile", not a failure. Sending the person to the
        // form is the useful response; an error box is not.
        if (cause instanceof ApiError && cause.status === 404) {
          setNoProfile(true);
          setItems([]);
          return;
        }
        setError(cause);
      });
  }, [api, minScore, assessedOnly, offset]);

  useEffect(load, [load]);

  if (noProfile) {
    return (
      <div className="empty">
        You have no profile yet. Fill in the profile form and matches appear after the next
        nightly sweep.
      </div>
    );
  }

  return (
    <div className="grid">
      {error ? <ErrorNote error={error} onRetry={load} /> : null}

      <div className="filters">
        <label>
          Minimum score
          <input
            type="range" min={0} max={90} step={5}
            value={minScore}
            onChange={(e) => { setOffset(0); setMinScore(Number(e.target.value)); }}
          />
          <span className="pill">{minScore}</span>
        </label>

        <label className="check">
          <input
            type="checkbox"
            checked={assessedOnly}
            onChange={(e) => { setOffset(0); setAssessedOnly(e.target.checked); }}
          />
          Only roles the model has read
        </label>
      </div>

      {!items && <div className="empty">Loading…</div>}

      {items?.length === 0 && !noProfile && (
        <div className="empty">
          Nothing above {minScore}. Matches are computed nightly, so a profile saved today is
          scored tomorrow morning.
        </div>
      )}

      {items && items.length > 0 && (
        <Card title="Matches" subtitle="Ranked by how much of each posting your profile covers.">
          <div className="scroll-x">
            <table>
              <thead>
                <tr>
                  <th className="num">Score</th>
                  <th>Role</th>
                  <th>Company</th>
                  <th>Working</th>
                  <th className="num">Salary</th>
                  <th>Verdict</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {items.map((match) => (
                  <tr key={match.postingId}>
                    <td className="num">
                      <ScorePill score={match.score} />
                      {match.coverage < 0.25 && match.score >= 70 && (
                        <span
                          className="pill warning"
                          style={{ marginLeft: 6 }}
                          title={`Scored on ${Math.round(match.coverage * 100)}% of a full assessment - this advert states very little`}
                        >
                          thin
                        </span>
                      )}
                    </td>
                    <td>
                      {match.title}
                      {match.requiredGapCount > 0 && (
                        <span
                          className="pill warning"
                          style={{ marginLeft: 8 }}
                          title="Requirements the advert marked essential that your profile does not show"
                        >
                          {match.requiredGapCount} unmet
                        </span>
                      )}
                    </td>
                    <td>{match.company ?? '—'}</td>
                    <td>{ARRANGEMENT[match.workArrangement] ?? '—'}</td>
                    <td className="num">{salary(match)}</td>
                    <td><Verdict match={match} /></td>
                    <td>
                      <button className="btn" onClick={() => setSelected(match.postingId)}>
                        Open
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          <div style={{ display: 'flex', gap: 8, marginTop: 12 }}>
            <button className="btn" disabled={offset === 0} onClick={() => setOffset(Math.max(0, offset - PAGE_SIZE))}>
              Previous
            </button>
            <button
              className="btn"
              disabled={items.length < PAGE_SIZE}
              onClick={() => setOffset(offset + PAGE_SIZE)}
            >
              Next
            </button>
          </div>
        </Card>
      )}

      {selected !== undefined && (
        <MatchPanel api={api} postingId={selected} onClose={() => setSelected(undefined)} />
      )}
    </div>
  );
}

/**
 * One match in full: the breakdown, and the button that costs money.
 *
 * The breakdown is why this exists rather than the score being a column. A number with nothing
 * behind it is a number nobody acts on; "you meet nine of their eleven requirements, and the two
 * you do not are Terraform and a security clearance" is something a person can do something with.
 */
function MatchPanel({ api, postingId, onClose }: {
  api: JobPlatformApi; postingId: number; onClose: () => void;
}) {
  const [match, setMatch] = useState<MatchDetail>();
  const [draft, setDraft] = useState<ApplicationDetail>();
  const [instructions, setInstructions] = useState('');
  const [generating, setGenerating] = useState(false);
  const [error, setError] = useState<unknown>();

  const load = useCallback(() => {
    setError(undefined);
    api.match(postingId).then(setMatch).catch(setError);
  }, [api, postingId]);

  useEffect(load, [load]);

  const generate = () => {
    setGenerating(true);
    setError(undefined);

    api.generateApplication(postingId, instructions.trim() || undefined)
      .then(setDraft)
      .catch(setError)
      .finally(() => setGenerating(false));
  };

  return (
    <Card
      title={match?.title ?? 'Match'}
      subtitle={match?.company ?? undefined}
      actions={<button className="btn" onClick={onClose}>Close</button>}
    >
      {error ? <ErrorNote error={error} /> : null}
      {!match && !error && <div className="empty">Loading…</div>}

      {match && (
        <>
          <div className="grid cols">
            <div>
              <h3 style={{ fontSize: 13 }}>How the score was reached</h3>
              {match.components
                // Zero weight means the posting said nothing on that axis, so it was dropped
                // rather than failed. Rendering it would show a penalty that was never applied.
                .filter((component) => component.weight > 0)
                .map((component) => (
                  <div key={component.name} className="axis">
                    <span className="axis-label">{AXIS[component.name] ?? component.name}</span>
                    <span className="axis-track">
                      <span className="axis-fill" style={{ width: `${Math.round(component.score * 100)}%` }} />
                    </span>
                    <span className="axis-value">{Math.round(component.score * 100)}%</span>
                  </div>
                ))}
              <p className="muted" style={{ fontSize: 12, marginTop: 8 }}>
                Axes the advert said nothing about are left out entirely rather than scored zero.
              </p>
            </div>

            <div>
              <h3 style={{ fontSize: 13 }}>What the model said</h3>
              {match.assessedAtUtc ? (
                <>
                  <p style={{ fontSize: 13 }}>{match.rationale}</p>
                  {match.strengths.length > 0 && (
                    <>
                      <h4 style={{ fontSize: 12 }}>Lands well</h4>
                      <ul className="tight">{match.strengths.map((s) => <li key={s}>{s}</li>)}</ul>
                    </>
                  )}
                  {match.assessmentGaps.length > 0 && (
                    <>
                      <h4 style={{ fontSize: 12 }}>Worth knowing</h4>
                      <ul className="tight">{match.assessmentGaps.map((g) => <li key={g}>{g}</li>)}</ul>
                    </>
                  )}
                </>
              ) : (
                <p className="muted" style={{ fontSize: 13 }}>
                  The model has not read this one yet. It reads the highest-scoring unassessed
                  roles each night.
                </p>
              )}
            </div>
          </div>

          <div className="grid cols">
            <div>
              <h3 style={{ fontSize: 13 }}>Requirements you meet</h3>
              {match.matched.length === 0 ? (
                <p className="muted" style={{ fontSize: 13 }}>None.</p>
              ) : (
                <ul className="tight">
                  {match.matched.map((item) => (
                    <li key={`${item.required}-${item.held}`}>
                      {item.requiredLabel}
                      {item.relation !== 'Exact' && (
                        <span className="muted"> — {RELATION[item.relation]} ({item.heldLabel})</span>
                      )}
                    </li>
                  ))}
                </ul>
              )}
            </div>

            <div>
              <h3 style={{ fontSize: 13 }}>Requirements you do not</h3>
              {match.gaps.length === 0 ? (
                <p className="muted" style={{ fontSize: 13 }}>None.</p>
              ) : (
                <ul className="tight">
                  {match.gaps.map((gap) => (
                    <li key={gap.concept}>
                      {gap.label}
                      {gap.demand === 'Required' && <span className="pill warning" style={{ marginLeft: 6 }}>essential</span>}
                      {gap.yearsMin != null && <span className="muted"> — {gap.yearsMin}+ years</span>}
                    </li>
                  ))}
                </ul>
              )}
            </div>
          </div>

          <h3 style={{ fontSize: 13, marginTop: 18 }}>Tailored application</h3>
          <p className="muted" style={{ fontSize: 12 }}>
            Written from your profile alone. Nothing in the gap list above can be claimed —
            tailoring means choosing what to lead with, never adding what is not there.
          </p>

          <div style={{ display: 'flex', gap: 8, alignItems: 'flex-end', flexWrap: 'wrap' }}>
            <label className="field" style={{ flex: 1, minWidth: 260 }}>
              <span className="field-label">Anything to steer? (optional)</span>
              <input
                value={instructions}
                onChange={(e) => setInstructions(e.target.value)}
                placeholder="Lead with the platform work rather than the backend work"
              />
            </label>
            <button className="btn" onClick={generate} disabled={generating}>
              {generating ? 'Writing…' : draft ? 'Write another draft' : 'Write CV and cover letter'}
            </button>
          </div>

          {draft && <DraftView api={api} draft={draft} />}
        </>
      )}
    </Card>
  );
}

function DraftView({ api, draft }: { api: JobPlatformApi; draft: ApplicationDetail }) {
  const [downloading, setDownloading] = useState<string>();
  const [error, setError] = useState<unknown>();

  /**
   * Fetches the PDF with the bearer token, then hands it to a synthetic link.
   *
   * A plain anchor cannot carry an Authorization header, and these endpoints require one -
   * they return somebody's CV. The object URL is revoked immediately: it is only needed long
   * enough for the click to be dispatched.
   */
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

      <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', alignItems: 'center' }}>
        <span className="pill">Draft {draft.revision}</span>
        <button
          className="btn"
          disabled={downloading === 'cv'}
          onClick={() => download('cv', `CV-${draft.postingTitle}.pdf`)}
        >
          {downloading === 'cv' ? 'Preparing…' : 'Download CV (PDF)'}
        </button>
        <button
          className="btn"
          disabled={downloading === 'cover-letter'}
          onClick={() => download('cover-letter', `Cover-letter-${draft.postingTitle}.pdf`)}
        >
          {downloading === 'cover-letter' ? 'Preparing…' : 'Download cover letter (PDF)'}
        </button>
      </div>

      {draft.emphasised.length > 0 && (
        <>
          <h4 style={{ fontSize: 12 }}>This draft leads with</h4>
          <ul className="tight">{draft.emphasised.map((item) => <li key={item}>{item}</li>)}</ul>
        </>
      )}

      {/* Rendered as preformatted text, not as markdown or HTML. The PDF is the artefact; this
          is a preview, and model output has no business being interpreted as markup here. */}
      <h4 style={{ fontSize: 12 }}>CV</h4>
      <pre className="markdown">{draft.curriculumVitaeMarkdown}</pre>

      <h4 style={{ fontSize: 12 }}>Cover letter</h4>
      <pre className="markdown">{draft.coverLetterMarkdown}</pre>
    </div>
  );
}

/**
 * The score, banded.
 *
 * Paired in the table with a "thin" marker where a high score rests on very little: the
 * scorer drops axes a posting says nothing about, so a terse advert can score well on the
 * one thing it did state. The number is honest; the caveat is what stops it being read as
 * more than it is.
 *
 * Bands rather than a gradient: three states a person can hold in their head beat a continuous
 * hue nobody can read a number off. The number is always written out, so the colour reinforces
 * rather than encodes.
 */
function ScorePill({ score }: { score: number }) {
  const band = score >= 70 ? '' : score >= 45 ? 'warning' : 'critical';
  return <span className={`pill ${band}`}>{score}</span>;
}

function Verdict({ match }: { match: MatchSummary }) {
  if (!match.verdict || match.verdict === 'Unknown') {
    return <span className="muted">not read yet</span>;
  }

  const band = match.verdict === 'Strong' ? '' : match.verdict === 'Possible' ? 'warning' : 'critical';

  return (
    <span className={`pill ${band}`} title={match.rationale ?? undefined}>
      {match.verdict.toLowerCase()}
      {match.assessmentScore != null && ` · ${match.assessmentScore}`}
    </span>
  );
}

function salary(match: MatchSummary): string {
  if (match.annualSalaryMin == null && match.annualSalaryMax == null) return '—';

  const currency = match.annualSalaryCurrency ?? '';
  const format = (value: number) => `${currency}${Math.round(value / 1000)}k`;

  if (match.annualSalaryMin != null && match.annualSalaryMax != null) {
    return `${format(match.annualSalaryMin)}–${format(match.annualSalaryMax)}`;
  }

  return format((match.annualSalaryMax ?? match.annualSalaryMin)!);
}
