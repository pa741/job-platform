import { useCallback, useEffect, useState } from 'react';
import type { JobPlatformApi } from '../api/client';
import type { Assertion, AssertionSource, DemandPolarity, PostingInsight } from '../api/types';
import { Card, ErrorNote, StatTile } from '../components/Primitives';

/**
 * Everything the pipeline concluded about one posting, and how it concluded it.
 *
 * **Provenance is the whole point of this panel.** The rest of the dashboard shows what the
 * corpus looks like in aggregate; this is the only place a person can check a single
 * conclusion against the sentence it came from. "This advert wants Kubernetes" is a claim, and
 * "the employer tagged it", "the description contains the string" and "the model read *must
 * have Kubernetes*" are three different qualities of evidence for it.
 *
 * Deliberately almost chart-free. A posting has one of most things — one applicant count, one
 * salary band, one age — and a single value is a stat tile, not a plot. The only magnitude
 * comparison here is the domain rollup, which gets a bar because it genuinely compares counts.
 */
export function PostingInsightPanel({ api, postingId, onClose }: {
  api: JobPlatformApi; postingId: number; onClose: () => void;
}) {
  const [insight, setInsight] = useState<PostingInsight>();
  const [error, setError] = useState<unknown>();
  const [showDescription, setShowDescription] = useState(false);

  const load = useCallback(() => {
    setError(undefined);
    setInsight(undefined);
    api.postingInsight(postingId).then(setInsight).catch(setError);
  }, [api, postingId]);

  useEffect(load, [load]);

  if (error) return <ErrorNote error={error} onRetry={load} />;
  if (!insight) return <div className="empty">Loading…</div>;

  const { detail, provenance } = insight;
  const summary = detail.summary;

  return (
    <div className="grid">
      <Card
        title={summary.title}
        subtitle={[summary.company, summary.location].filter(Boolean).join(' · ') || undefined}
        actions={<button className="btn" onClick={onClose}>Close</button>}
      >
        <div className="grid kpi">
          {/* A single number is a stat tile, never a one-bar chart. */}
          <StatTile
            label="Applicants"
            value={summary.applicantCount ?? '—'}
            hint={detail.applicants ?? 'LinkedIn only; most boards never say'}
          />
          <StatTile
            label="Salary"
            value={salary(summary)}
            hint={
              summary.annualSalaryMin == null && summary.annualSalaryMax == null
                ? 'Not stated anywhere'
                : summary.salaryFromText
                  ? 'Read from the description, not a salary field'
                  : `Published by the board${summary.salaryStatedInterval ? ` as ${summary.salaryStatedInterval}` : ''}`
            }
          />
          <StatTile
            label="Age"
            value={summary.postingAgeDays != null ? `${summary.postingAgeDays}d` : '—'}
            hint={summary.freshnessClass ?? 'No freshness signal from this board'}
          />
          <StatTile
            label="Times seen"
            value={provenance.seenCount}
            hint={`First ${date(provenance.firstSeenUtc)}, last ${date(provenance.lastSeenUtc)}`}
          />
        </div>

        <div className="chips">
          {summary.workArrangement !== 'Unknown' && (
            <span className="pill" title="Concluded by the enricher">
              {summary.workArrangement}
              {summary.hybridDaysInOffice != null && ` · ${summary.hybridDaysInOffice}d in office`}
            </span>
          )}
          {/* Shown next to the derived value on purpose: this is what the employer stated and
              that is what we concluded. Where they disagree, the disagreement is the story. */}
          {detail.workFromHomeType && (
            <span className="pill" title="Stated by the board">board says: {detail.workFromHomeType}</span>
          )}
          {summary.seniority !== 'Unknown' && <span className="pill">{summary.seniority}</span>}
          {summary.roleFamily !== 'Unknown' && <span className="pill">{summary.roleFamily}</span>}
          {insight.jobTypes.map((t) => <span key={t} className="pill">{t}</span>)}
          {detail.ir35 && <span className="pill warning">IR35 {detail.ir35}</span>}
          {detail.visaSponsorship === true && <span className="pill">visa sponsorship</span>}
          {summary.requiresSecurityClearance && <span className="pill warning">clearance required</span>}
          {detail.vacancyCount != null && detail.vacancyCount > 1 && (
            <span className="pill">{detail.vacancyCount} openings</span>
          )}
          {summary.fakeFreshness === true && (
            <span className="pill critical" title="The stated date looks refreshed rather than real">
              date refreshed
            </span>
          )}
        </div>

        {summary.jobUrl && (
          <p style={{ marginTop: 12, fontSize: 13 }}>
            <a href={summary.jobUrl} target="_blank" rel="noreferrer">Open the advert</a>
            {detail.jobUrlDirect && detail.jobUrlDirect !== summary.jobUrl && (
              <> · <a href={detail.jobUrlDirect} target="_blank" rel="noreferrer">direct link</a></>
            )}
          </p>
        )}
      </Card>

      <Requirements concepts={insight.concepts} />

      {insight.domains.length > 0 && <Domains domains={insight.domains} />}

      {insight.mentions.length > 0 && <Mentions mentions={insight.mentions} />}

      <Card
        title="Where this came from"
        subtitle="Which searches surfaced it, and which passes have read it."
      >
        <div className="form-grid">
          <Fact label="Found by">
            {insight.foundBy.length === 0 ? '—' : insight.foundBy.map((f) => (
              <div key={f.searchTerm}>
                <strong>{f.searchTerm}</strong>
                <span className="muted"> · last {date(f.lastSeenUtc)}</span>
              </div>
            ))}
          </Fact>
          <Fact label="Board">
            {summary.site}
            {detail.salarySource && <span className="muted"> · salary from {detail.salarySource}</span>}
          </Fact>
          <Fact label="Enrichment">
            v{provenance.enrichmentVersion}
            <span className="muted"> · the deterministic classifiers</span>
          </Fact>
          <Fact label="Model pass">
            {provenance.extractorVersion == null ? (
              <span className="muted">never run for this posting</span>
            ) : (
              <>
                v{provenance.extractorVersion}
                <span className="muted"> · {provenance.model} · {date(provenance.extractedAtUtc)}</span>
              </>
            )}
          </Fact>
          {insight.company && (
            <Fact label="Company">
              {insight.company.displayName}
              {insight.company.industry && <span className="muted"> · {insight.company.industry}</span>}
              {insight.company.employeesBand && <span className="muted"> · {insight.company.employeesBand}</span>}
            </Fact>
          )}
          <Fact label="Description">
            {summary.descriptionLength.toLocaleString()} characters
          </Fact>
        </div>

        {detail.synopsis && (
          <>
            <h4 style={{ fontSize: 12, marginTop: 14 }}>Board synopsis</h4>
            <p style={{ fontSize: 13 }}>{detail.synopsis}</p>
          </>
        )}

        {detail.description && (
          <>
            <button className="btn" style={{ marginTop: 12 }} onClick={() => setShowDescription((v) => !v)}>
              {showDescription ? 'Hide' : 'Show'} the full advert
            </button>
            {/* Preformatted, never rendered as markup: this is scraped third-party text. */}
            {showDescription && <pre className="markdown">{detail.description}</pre>}
          </>
        )}
      </Card>
    </div>
  );
}

/**
 * What the posting asks for, grouped by how hard it asks.
 *
 * Grouped by polarity rather than sorted by it, because the groups mean different things to a
 * reader: "essential" is a filter, "nice to have" is a tiebreak. `Unspecified` is the biggest
 * group in practice and is labelled honestly — it means no pass has been able to tell, not
 * that the advert was neutral.
 */
function Requirements({ concepts }: { concepts: Assertion[] }) {
  const order: DemandPolarity[] = ['Required', 'Preferred', 'Mentioned', 'Unspecified'];

  const heading: Record<DemandPolarity, string> = {
    Required: 'Essential',
    Preferred: 'Nice to have',
    Mentioned: 'Mentioned',
    Unspecified: 'Named, strength unknown',
  };

  const note: Record<DemandPolarity, string> = {
    Required: 'The advert marks these essential.',
    Preferred: 'Desirable, bonus, nice to have.',
    Mentioned: 'Named with no indication either way.',
    Unspecified: 'No pass could tell how hard the advert asks — usually because the model has not read it.',
  };

  return (
    <Card
      title="What it asks for"
      subtitle="Every requirement carries the evidence it was read from. Hover a source badge to see what it means."
    >
      {concepts.length === 0 && (
        <p className="muted" style={{ fontSize: 13 }}>
          Nothing resolved. Either the advert names no technology the vocabulary knows, or no
          pass has read it yet — the footer below says which.
        </p>
      )}

      {order.map((polarity) => {
        const group = concepts.filter((c) => c.polarity === polarity);
        if (group.length === 0) return null;

        return (
          <section key={polarity} style={{ marginBottom: 14 }}>
            <h4 style={{ fontSize: 12, marginBottom: 2 }}>
              {heading[polarity]} <span className="muted">({group.length})</span>
            </h4>
            <p className="muted" style={{ fontSize: 11, margin: '0 0 6px' }}>{note[polarity]}</p>

            <div className="assertions">
              {group.map((c) => (
                <div key={`${c.concept}-${c.source}`} className="assertion">
                  <span className="assertion-label">
                    {c.label}
                    {c.yearsMin != null && <span className="muted"> · {c.yearsMin}+ yrs</span>}
                  </span>
                  <SourceBadge source={c.source} />
                  {c.evidence && <span className="assertion-evidence">“{c.evidence}”</span>}
                </div>
              ))}
            </div>
          </section>
        );
      })}
    </Card>
  );
}

/**
 * Where an assertion came from.
 *
 * Rendered as a labelled badge rather than a colour, because the three are not a scale and a
 * reader should not have to learn a legend to know whether a claim is the employer's own or a
 * model's inference. The title carries the full explanation.
 */
function SourceBadge({ source }: { source: AssertionSource }) {
  const explain: Record<AssertionSource, string> = {
    Board: 'The employer published this as structured data. The strongest evidence there is.',
    Taxonomy: 'A string match against the advert text. Finds things mentioned in passing as readily as things required.',
    Model: 'A language model read this out of the prose. The only pass that can tell essential from desirable.',
  };

  const short: Record<AssertionSource, string> = {
    Board: 'employer',
    Taxonomy: 'text match',
    Model: 'model',
  };

  return <span className="assertion-source" title={explain[source]}>{short[source]}</span>;
}

/**
 * The domains this posting rolls up to.
 *
 * The one genuine magnitude comparison on the page, so the one thing that gets a bar. Computed
 * server-side by walking the concept DAG upward — this is the only view in the dashboard where
 * the closure is visible as something other than a filter, and it is what turns "wants C#,
 * ASP.NET Core and SQL Server" into "is a backend role".
 */
function Domains({ domains }: { domains: { concept: string; label: string; count: number }[] }) {
  const max = Math.max(...domains.map((d) => d.count), 1);

  return (
    <Card
      title="What kind of role this is"
      subtitle="Rolled up through the concept graph. The advert never uses these words — they are reached from what it does say."
    >
      {domains.map((d) => (
        <div key={d.concept} className="axis">
          <span className="axis-label" title={d.concept}>{d.label}</span>
          <span className="axis-track">
            <span className="axis-fill" style={{ width: `${(d.count / max) * 100}%` }} />
          </span>
          <span className="axis-value">{d.count}</span>
        </div>
      ))}
    </Card>
  );
}

/**
 * What the vocabulary could not place.
 *
 * Shown rather than hidden, for the reason the mention log exists at all: "nobody asked for
 * this" and "we could not tell" are different answers, and this is the only place a reader can
 * see which one they are looking at.
 */
function Mentions({ mentions }: { mentions: { surfaceForm: string; reason: string; occurrences: number }[] }) {
  const explain: Record<string, string> = {
    Ambiguous: 'Names a concept the vocabulary knows but cannot be trusted to mean it — "Go", "R", "C".',
    UnknownBoardSkill: 'The employer tagged this and the vocabulary has no concept for it.',
    UnknownModelSkill: 'The model flagged this as a technology and the vocabulary has no concept for it.',
  };

  return (
    <Card
      title="Seen but not placed"
      subtitle="Recorded rather than discarded. This is where the next batch of vocabulary comes from."
    >
      <div className="chips">
        {mentions.map((m) => (
          <span key={m.surfaceForm} className="pill" title={explain[m.reason] ?? m.reason}>
            {m.surfaceForm}
            {m.occurrences > 1 && <span className="muted"> ×{m.occurrences}</span>}
          </span>
        ))}
      </div>
    </Card>
  );
}

function Fact({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="field">
      <span className="field-label">{label}</span>
      <span style={{ fontSize: 13 }}>{children}</span>
    </div>
  );
}

function salary(s: { annualSalaryMin: number | null; annualSalaryMax: number | null; annualSalaryCurrency: string | null }): string {
  if (s.annualSalaryMin == null && s.annualSalaryMax == null) return '—';

  const c = s.annualSalaryCurrency ?? '';
  const k = (v: number) => `${c}${Math.round(v / 1000)}k`;

  return s.annualSalaryMin != null && s.annualSalaryMax != null
    ? `${k(s.annualSalaryMin)}–${k(s.annualSalaryMax)}`
    : k((s.annualSalaryMax ?? s.annualSalaryMin)!);
}

function date(value: string | null): string {
  return value ? new Date(value).toLocaleDateString() : '—';
}
