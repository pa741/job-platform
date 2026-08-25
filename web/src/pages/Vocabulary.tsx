import { useCallback, useEffect, useMemo, useState } from 'react';
import type { JobPlatformApi } from '../api/client';
import type {
  ConceptDetail, ConceptEdge, ConceptListItem, ConceptRelation, SourceComposition,
} from '../api/types';
import { Card, ErrorNote, Meter, StatTile } from '../components/Primitives';

/**
 * The concept graph, and where the corpus's knowledge comes from.
 *
 * **These are the two things this architecture is actually about, and neither had a view.** The
 * vocabulary decides what a posting is understood to ask for, what a profile is understood to
 * hold, and therefore every match — and the only way to look at it was to read
 * `concepts.json`. The source composition is the only honest measure of what each pass
 * contributes, and it existed solely as a column nothing read.
 */
export function Vocabulary({ api, searchTerm }: { api: JobPlatformApi; searchTerm: string | undefined }) {
  return (
    <div className="grid">
      <Provenance api={api} searchTerm={searchTerm} />
      <Explorer api={api} searchTerm={searchTerm} />
    </div>
  );
}

// ---------------------------------------------------------------------------
// Where knowledge comes from
// ---------------------------------------------------------------------------

/** Fixed order, never cycled. Strongest demand first, so the stack reads left to right. */
const POLARITY_ORDER = ['Required', 'Preferred', 'Mentioned', 'Unspecified'] as const;

/**
 * The categorical slots, taken in fixed order.
 *
 * Validated rather than assumed — the four together clear the lightness band, chroma floor and
 * CVD separation in both modes (worst adjacent pair ΔE 9.1 protan in light, 8.4 in dark, both
 * above the 8 threshold). Taking the next slot is the only correct way to add a series;
 * substituting a hex by eye is not something the eye can check. Four polarities, four slots —
 * a fifth would have to fold into "Other".
 *
 * <b>Light mode warns on contrast against the surface</b> for the green and amber slots (2.7:1
 * and 2.1:1, under 3:1). That warning is not dismissable: it obligates a non-colour reading of
 * the same figures, which is what the table view below is for, alongside the legend and the
 * per-segment titles. Remove the table and the palette stops being legal here.
 */
const POLARITY_SLOT: Record<string, string> = {
  Required: 'var(--series-1)',
  Preferred: 'var(--series-2)',
  Mentioned: 'var(--series-3)',
  Unspecified: 'var(--series-4)',
};

const SOURCE_NOTE: Record<string, string> = {
  Board: 'The employer published these as structured data. The strongest evidence there is, and the rarest.',
  Taxonomy: 'String matches against the advert text. Finds a technology mentioned in passing as readily as one the role requires — which is why they are all Unspecified.',
  Model: 'A language model read these out of prose. The only pass that can tell essential from desirable.',
};

function Provenance({ api, searchTerm }: { api: JobPlatformApi; searchTerm: string | undefined }) {
  const [data, setData] = useState<SourceComposition>();
  const [error, setError] = useState<unknown>();

  const load = useCallback(() => {
    setError(undefined);
    api.sourceComposition(searchTerm).then(setData).catch(setError);
  }, [api, searchTerm]);

  useEffect(load, [load]);

  if (error) return <ErrorNote error={error} onRetry={load} />;
  if (!data) return <div className="empty">Loading…</div>;

  const max = Math.max(...data.sources.map((s) => s.assertions), 1);

  return (
    <Card
      title="Where what we know comes from"
      subtitle="Three passes write assertions that look identical once stored. This is what separates them."
    >
      <div className="grid kpi">
        <StatTile label="Assertions" value={data.totalAssertions.toLocaleString()} hint="Concept claims across the corpus" />
        {/* One ratio against its limit is a meter, not a two-slice pie: the reader is comparing
            one value to 100%, and a track shows that directly. */}
        <Meter
          label="Graded"
          ratio={data.gradedShare}
          caption="Assertions carrying a strength rather than Unspecified"
        />
      </div>

      <p className="muted" style={{ fontSize: 12, marginTop: 4 }}>
        <strong>Graded is the number that matters.</strong> Only the model pass can say whether a
        requirement is essential or merely desirable, so this is the share of the corpus that is
        genuinely understood rather than merely inventoried. Near zero means every match is
        weighing “mentioned once in passing” the same as “must have”.
      </p>

      {/* Legend is always present for two or more series, so identity is never colour alone. */}
      <div className="legend" style={{ marginTop: 14 }}>
        {POLARITY_ORDER.map((p) => (
          <span key={p}>
            <i style={{ background: POLARITY_SLOT[p] }} aria-hidden="true" />
            {p}
          </span>
        ))}
      </div>

      {data.sources.length === 0 && (
        <p className="muted" style={{ fontSize: 13 }}>
          No assertions yet for this search term.
        </p>
      )}

      {data.sources.map((source) => {
        const counts = new Map(source.polarities.map((p) => [p.polarity, p.assertions]));

        return (
          <section key={source.source} className="source-row">
            <div className="source-head">
              <strong>{source.source}</strong>
              <span className="muted">{source.assertions.toLocaleString()} assertions</span>
            </div>

            {/* Width encodes each source's share of the largest, so the three are comparable;
                the segments within encode strength. One axis, no second scale. */}
            <div className="stack" style={{ width: `${(source.assertions / max) * 100}%` }}>
              {POLARITY_ORDER.map((p) => {
                const n = counts.get(p) ?? 0;
                if (n === 0) return null;

                return (
                  <span
                    key={p}
                    className="stack-seg"
                    style={{
                      flexGrow: n,
                      background: POLARITY_SLOT[p],
                    }}
                    title={`${source.source} · ${p}: ${n.toLocaleString()}`}
                  />
                );
              })}
            </div>

            <p className="muted source-note">{SOURCE_NOTE[source.source] ?? ''}</p>
          </section>
        );
      })}

      {/* A table view, so the figures are readable without relying on colour at all. */}
      <details style={{ marginTop: 12 }}>
        <summary className="muted" style={{ fontSize: 12, cursor: 'pointer' }}>Show as a table</summary>
        <div className="scroll-x">
          <table>
            <thead>
              <tr>
                <th>Source</th>
                {POLARITY_ORDER.map((p) => <th key={p} className="num">{p}</th>)}
                <th className="num">Total</th>
              </tr>
            </thead>
            <tbody>
              {data.sources.map((s) => {
                const counts = new Map(s.polarities.map((p) => [p.polarity, p.assertions]));
                return (
                  <tr key={s.source}>
                    <td>{s.source}</td>
                    {POLARITY_ORDER.map((p) => (
                      <td key={p} className="num">{(counts.get(p) ?? 0).toLocaleString()}</td>
                    ))}
                    <td className="num">{s.assertions.toLocaleString()}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </details>
    </Card>
  );
}

// ---------------------------------------------------------------------------
// The concept explorer
// ---------------------------------------------------------------------------

/** Relation groups, in the order a reader wants them. Structure first, then similarity. */
const RELATION_ORDER: ConceptRelation[] = [
  'Broader', 'Narrower', 'Implies', 'ImpliedBy', 'SucceededBy', 'Succeeds', 'Related', 'VariantOf',
];

const RELATION_HEADING: Record<ConceptRelation, string> = {
  Broader: 'Sits under',
  Narrower: 'Contains',
  Implies: 'Implies',
  ImpliedBy: 'Implied by',
  SucceededBy: 'Replaced by',
  Succeeds: 'Replaces',
  Related: 'Comparable to',
  VariantOf: 'Same thing, different spelling',
};

const RELATION_NOTE: Record<ConceptRelation, string> = {
  Broader: 'Walking up here is what turns “wants C# and ASP.NET Core” into “is a backend role”.',
  Narrower: 'Holding one of these satisfies a requirement for this concept outright — the specific case entails the general one.',
  Implies: 'Curated, not inferred. Naming this almost certainly means wanting these too.',
  ImpliedBy: 'Holding any of these earns full credit against this requirement.',
  SucceededBy: 'A different technology, not a different spelling. Holding this is weak evidence for its successor.',
  Succeeds: 'This replaced them. Holding this says nothing about the predecessor — the direction is the point.',
  Related: 'Competing or commonly substituted. Earns partial credit in a match, and is reported as an argument rather than a match.',
  VariantOf: 'A spelling or packaging of the same underlying thing.',
};

function Explorer({ api, searchTerm }: { api: JobPlatformApi; searchTerm: string | undefined }) {
  const [all, setAll] = useState<ConceptListItem[]>();
  const [version, setVersion] = useState<number>();
  const [selected, setSelected] = useState<string>();
  const [detail, setDetail] = useState<ConceptDetail>();
  const [filter, setFilter] = useState('');
  const [error, setError] = useState<unknown>();

  useEffect(() => {
    api.concepts()
      .then((r) => {
        setAll(r.items);
        setVersion(r.version);
        setSelected((current) => current ?? r.items.find((c) => c.kind === 'Skill')?.concept);
      })
      .catch(setError);
  }, [api]);

  useEffect(() => {
    if (!selected) return;
    setDetail(undefined);
    api.concept(selected, searchTerm).then(setDetail).catch(setError);
  }, [api, selected, searchTerm]);

  const matches = useMemo(() => {
    if (!all) return [];
    const needle = filter.trim().toLowerCase();
    const found = needle
      ? all.filter((c) => c.label.toLowerCase().includes(needle) || c.concept.includes(needle))
      : all;
    return found.slice(0, 300);
  }, [all, filter]);

  if (error) return <ErrorNote error={error} />;
  if (!all) return <div className="empty">Loading the vocabulary…</div>;

  return (
    <Card
      title="The concept graph"
      subtitle={`${all.length} concepts, version ${version ?? '?'}. A DAG, not a tree — Python is a language and is used in backend, data and ML.`}
    >
      <div className="explorer">
        <div className="explorer-list">
          <input
            className="explorer-filter"
            placeholder="Filter concepts…"
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
            aria-label="Filter concepts"
          />
          <ul>
            {matches.map((c) => (
              <li key={c.concept}>
                <button
                  className={c.concept === selected ? 'selected' : undefined}
                  onClick={() => setSelected(c.concept)}
                  title={c.concept}
                >
                  {c.label}
                  <span className="muted"> · {c.kind.toLowerCase()}</span>
                </button>
              </li>
            ))}
            {matches.length === 0 && <li className="muted" style={{ padding: 8 }}>Nothing matches.</li>}
          </ul>
        </div>

        <div className="explorer-detail">
          {!detail && <div className="empty">Loading…</div>}

          {detail && (
            <>
              <h3 style={{ fontSize: 15, marginBottom: 2 }}>{detail.label}</h3>
              <p className="muted" style={{ fontSize: 12, margin: 0 }}>
                <code>{detail.concept}</code> · {detail.kind.toLowerCase()}
                {' · '}
                <strong>{detail.demand.toLocaleString()}</strong> posting{detail.demand === 1 ? '' : 's'} ask for it
                {searchTerm && <span> in “{searchTerm}”</span>}
              </p>

              {detail.kind === 'Domain' && (
                <p className="muted" style={{ fontSize: 12 }}>
                  A grouping. It is never matched against advert text — adverts do not describe
                  themselves as “{detail.label.toLowerCase()}” — so it is normally reached by
                  walking up from something concrete rather than asserted directly.
                  {detail.demand > 0
                    ? ' The count above is real all the same: a board that publishes its own structured tags can name a domain outright, and some do.'
                    : ' A count of zero here is expected, and does not mean nobody wants this — see what sits under it below.'}
                </p>
              )}

              {detail.labels.length > 0 && (
                <section style={{ marginTop: 12 }}>
                  <h4 style={{ fontSize: 12 }}>Recognised as</h4>
                  <div className="chips">
                    {detail.labels.map((l) => (
                      <span
                        key={l.label}
                        className={l.kind === 'Ambiguous' ? 'pill warning' : 'pill'}
                        title={
                          l.kind === 'Ambiguous'
                            ? 'Names this concept but cannot be trusted to mean it. A match on this is recorded as an unresolved mention, never as an assertion.'
                            : l.kind
                        }
                      >
                        {l.label}
                        {l.kind === 'Ambiguous' && <span className="muted"> ambiguous</span>}
                      </span>
                    ))}
                  </div>
                </section>
              )}

              {detail.ancestors.length > 0 && (
                <section style={{ marginTop: 12 }}>
                  <h4 style={{ fontSize: 12 }}>Rolls up to</h4>
                  <p className="muted" style={{ fontSize: 11, margin: '0 0 4px' }}>
                    The closure. This is what a domain rollup reads, and what the match scorer walks
                    to decide one concept satisfies a requirement for another.
                  </p>
                  <div className="chips">
                    {detail.ancestors.map((a) => (
                      <button
                        key={a.concept}
                        className="pill"
                        onClick={() => setSelected(a.concept)}
                        title={`${a.concept} · ${a.depth} step${a.depth === 1 ? '' : 's'} up`}
                      >
                        {a.label}
                        <span className="muted"> +{a.depth}</span>
                      </button>
                    ))}
                  </div>
                </section>
              )}

              {RELATION_ORDER.map((relation) => {
                const group = detail.edges.filter((e) => e.relation === relation);
                if (group.length === 0) return null;

                return (
                  <section key={relation} style={{ marginTop: 12 }}>
                    <h4 style={{ fontSize: 12 }}>
                      {RELATION_HEADING[relation]} <span className="muted">({group.length})</span>
                    </h4>
                    <p className="muted" style={{ fontSize: 11, margin: '0 0 4px' }}>
                      {RELATION_NOTE[relation]}
                    </p>
                    <div className="chips">
                      {group.map((e) => <EdgeChip key={`${relation}-${e.concept}`} edge={e} onOpen={setSelected} />)}
                    </div>
                  </section>
                );
              })}

              {detail.edges.length === 0 && detail.ancestors.length === 0 && (
                <p className="muted" style={{ fontSize: 13, marginTop: 12 }}>
                  An isolated node — no parents, no children, no relations. Worth knowing: it can
                  only ever match itself exactly.
                </p>
              )}
            </>
          )}
        </div>
      </div>
    </Card>
  );
}

/** One neighbour, with how much of the corpus wants it. Clicking walks the graph. */
function EdgeChip({ edge, onOpen }: { edge: ConceptEdge; onOpen: (key: string) => void }) {
  return (
    <button
      className="pill"
      onClick={() => onOpen(edge.concept)}
      title={`${edge.concept} · ${edge.demand ?? 0} posting(s)`}
    >
      {edge.label}
      {/* Demand is written out rather than encoded, because zero is a real and interesting
          answer here: a concept in the vocabulary that nothing in the corpus asks for. */}
      <span className="muted"> {edge.demand ?? 0}</span>
    </button>
  );
}
