import { useState } from 'react';
import type { JobPlatformApi } from '../api/client';
import type { MatchResponse, PostingSummary } from '../api/types';
import { Card, ErrorNote } from '../components/Primitives';

/**
 * CV matching.
 *
 * The one page that can cost money per request, which is why the API keeps it behind its
 * own small rate-limit bucket and requires a real principal even when reads are open. The
 * UI reflects that: matching is an explicit button press, never a keystroke-triggered
 * search.
 */
export function Match({ api, searchTerm }: { api: JobPlatformApi; searchTerm: string | undefined }) {
  const [cvText, setCvText] = useState('');
  const [topN, setTopN] = useState(10);
  const [result, setResult] = useState<MatchResponse>();
  const [postings, setPostings] = useState<Record<number, PostingSummary>>({});
  const [error, setError] = useState<unknown>();
  const [busy, setBusy] = useState(false);

  async function run() {
    if (cvText.trim().length < 40) {
      setError(new Error('Paste a bit more of the CV — there is not enough text to match on.'));
      return;
    }

    setBusy(true);
    setError(undefined);

    try {
      const response = await api.match({ cvText, searchTerm, topN });
      setResult(response);

      // Matches carry ids, not titles. Resolve them so the result is readable - and do it
      // in parallel, since these are independent cached reads.
      const details = await Promise.all(
        response.matches.map((match) => api.posting(match.postingId).catch(() => undefined)),
      );

      const resolved: Record<number, PostingSummary> = {};
      for (const detail of details) {
        if (detail) resolved[detail.summary.id] = detail.summary;
      }
      setPostings(resolved);
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="grid halves">
      <Card title="Your CV" subtitle="Plain text. It is sent to the API for matching and never stored.">
        <label htmlFor="cv">CV text</label>
        <textarea
          id="cv"
          value={cvText}
          onChange={(e) => setCvText(e.target.value)}
          placeholder="Paste your CV here…"
        />
        <div className="filters" style={{ marginTop: 12, marginBottom: 0 }}>
          <div>
            <label htmlFor="topN">Matches</label>
            <select id="topN" value={topN} onChange={(e) => setTopN(Number(e.target.value))}>
              {[5, 10, 20].map((n) => <option key={n} value={n}>{n}</option>)}
            </select>
          </div>
          <button className="btn" onClick={() => void run()} disabled={busy || !searchTerm}>
            {busy ? 'Matching…' : 'Find matches'}
          </button>
        </div>
        {error ? <div style={{ marginTop: 12 }}><ErrorNote error={error} /></div> : null}
      </Card>

      <Card
        title="Matches"
        subtitle={
          result
            ? `${result.provider} · ${result.candidatesConsidered} postings considered`
            : 'Paste a CV and press Find matches.'
        }
      >
        {/* Stated outright rather than hidden: a ranking produced by the keyword fallback
            is a different thing from one produced by the model, and a reader deserves to
            know which they are looking at. */}
        {result?.degradedToFallback && (
          <div className="err" style={{ marginBottom: 12 }}>
            <strong>Ranked by keyword overlap.</strong>
            <div className="muted" style={{ marginTop: 4 }}>
              The configured ranker was unavailable{result.degradationReason ? ` (${result.degradationReason})` : ''},
              so these are the retrieval scores rather than a model ranking.
            </div>
          </div>
        )}

        {result && (
          <div className="muted" style={{ fontSize: 12, marginBottom: 12 }}>
            Detected: {result.profile.skills.slice(0, 12).join(', ') || 'no known skills'}
            {result.profile.yearsExperience != null && ` · ${result.profile.yearsExperience} years`}
          </div>
        )}

        {result?.matches.map((match) => {
          const posting = postings[match.postingId];

          return (
            <div key={match.postingId} style={{ padding: '10px 0', borderBottom: '1px solid var(--gridline)' }}>
              <div style={{ display: 'flex', gap: 12, alignItems: 'baseline' }}>
                <strong style={{ flex: 1 }}>
                  {posting?.jobUrl
                    ? <a href={posting.jobUrl} target="_blank" rel="noreferrer noopener">{posting.title}</a>
                    : (posting?.title ?? `Posting ${match.postingId}`)}
                </strong>
                {/* The score is written, not only encoded as a bar: it is comparable within
                    this result set and nowhere else, so a number the reader can see beats a
                    length they might compare across runs. */}
                <span style={{ fontVariantNumeric: 'tabular-nums', fontWeight: 600 }}>
                  {Math.round(match.score)}
                </span>
              </div>
              <div className="muted" style={{ fontSize: 12 }}>
                {posting?.company ?? '—'}{posting?.city ? ` · ${posting.city}` : ''}
                {posting?.isRemote ? ' · remote' : ''}
              </div>
              {match.rationale && <div style={{ fontSize: 13, marginTop: 4 }}>{match.rationale}</div>}
              {match.matchedSkills.length > 0 && (
                <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap', marginTop: 6 }}>
                  {match.matchedSkills.map((skill) => (
                    <span key={skill} className="pill good">✓ {skill}</span>
                  ))}
                  {match.missingSkills.slice(0, 5).map((skill) => (
                    <span key={skill} className="pill">− {skill}</span>
                  ))}
                </div>
              )}
            </div>
          );
        })}

        {result?.matches.length === 0 && <div className="empty">No postings matched.</div>}
      </Card>
    </div>
  );
}
