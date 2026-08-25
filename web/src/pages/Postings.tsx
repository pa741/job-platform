import { useCallback, useEffect, useState } from 'react';
import type { JobPlatformApi, PostingQuery } from '../api/client';
import type { FacetsResponse, PageResponse, PostingSummary } from '../api/types';
import { Card, ErrorNote } from '../components/Primitives';
import { PostingInsightPanel } from './PostingInsightPanel';

const PAGE_SIZE = 25;

/**
 * Browse and filter stored postings.
 *
 * The only page that reads Azure SQL. Filters are applied server-side rather than by
 * fetching everything and filtering in the browser - the table has hundreds of rows now but
 * grows daily, and the database is billed by the second it spends awake.
 */
/**
 * Only freehire supplies these, and only the unflattering ones earn a badge: a "fresh"
 * pill on most of the table would be noise, while a recycled posting is exactly what a
 * job hunter wants to spot before spending an afternoon on it.
 *
 * fakeFreshness is compared against true rather than tested for truthiness - null means
 * the posting came from a board that never checked, not that it passed.
 */
function FreshnessPills({ posting }: { posting: PostingSummary }) {
  const stale = posting.freshnessClass === 'stale' || posting.freshnessClass === 'likely-evergreen';
  const reposted = (posting.repostCount ?? 0) > 1;

  return (
    <>
      {stale && (
        <span
          className="pill warning"
          style={{ marginLeft: 8 }}
          title={posting.postingAgeDays !== null ? `${posting.postingAgeDays} days old` : undefined}
        >
          {posting.freshnessClass === 'stale' ? 'stale' : 'evergreen'}
        </span>
      )}
      {reposted && (
        <span className="pill warning" style={{ marginLeft: 8 }}>
          reposted &times;{posting.repostCount}
        </span>
      )}
      {posting.fakeFreshness === true && (
        <span
          className="pill critical"
          style={{ marginLeft: 8 }}
          title="The stated posting date looks refreshed rather than real"
        >
          date refreshed
        </span>
      )}
    </>
  );
}

const ARRANGEMENT: Record<string, string> = {
  Remote: 'Remote',
  Hybrid: 'Hybrid',
  OnSite: 'On-site',
};

/**
 * The salary, on one scale, with the caveat attached.
 *
 * Shows the annualised figure rather than the board's raw columns, which are populated for
 * materially fewer postings. A "Salary" column reading "—" almost everywhere is worse than
 * useless: it suggests the market does not disclose, when partly we were not reading.
 *
 * The interval matters more than it looks. A contract at GBP 600 a day annualises to 156,000,
 * which is a real number for comparison and a misleading one to read as a salary. Marking it
 * is the difference between a comparable figure and a wrong one.
 */
function Salary({ posting }: { posting: PostingSummary }) {
  const { annualSalaryMin: min, annualSalaryMax: max } = posting;
  if (min == null && max == null) return <>—</>;

  const currency = posting.annualSalaryCurrency ?? '';
  const fmt = (v: number) => Math.round(v / 1000) + 'k';
  const range = min != null && max != null && min !== max
    ? `${fmt(min)}–${fmt(max)}`
    : fmt((min ?? max)!);

  const derived = posting.salaryFromText;
  const interval = posting.salaryStatedInterval;
  const rate = interval && interval !== 'yearly' ? interval : null;

  return (
    <>
      {`${currency} ${range}`.trim()}
      {rate && (
        <span className="pill warning" style={{ marginLeft: 6 }}
          title={`Stated as a ${rate} rate and annualised for comparison`}>
          {rate}
        </span>
      )}
      {derived && !rate && (
        <span className="muted" style={{ marginLeft: 6 }}
          title="Read from the description rather than a salary field">~</span>
      )}
    </>
  );
}

/**
 * The things that decide whether someone can take the job at all.
 *
 * Only the ones that exclude people get a pill. A "hybrid" badge on half the table would be
 * noise - that is what the Working column is for - but a clearance requirement or an inside
 * IR35 contract is a hard filter, and worth seeing without opening the posting.
 */
function RequirementPills({ posting }: { posting: PostingSummary }) {
  return (
    <>
      {posting.requiresSecurityClearance && (
        <span className="pill warning" style={{ marginLeft: 8 }}
          title="The posting names a security clearance">clearance</span>
      )}
      {posting.ir35 && (
        <span className={`pill ${posting.ir35 === 'inside' ? 'warning' : ''}`}
          style={{ marginLeft: 8 }} title={`${posting.ir35} IR35`}>
          {posting.ir35} IR35
        </span>
      )}
    </>
  );
}

export function Postings({ api, searchTerm }: { api: JobPlatformApi; searchTerm: string | undefined }) {
  const [facets, setFacets] = useState<FacetsResponse>();
  const [page, setPage] = useState<PageResponse<PostingSummary>>();
  const [error, setError] = useState<unknown>();
  const [loading, setLoading] = useState(false);
  const [offset, setOffset] = useState(0);

  // Which posting's insight panel is open. Held here rather than in the row so that opening a
  // second one closes the first: two expanded panels in a table is a scrolling puzzle.
  const [inspecting, setInspecting] = useState<number>();

  const [q, setQ] = useState('');
  const [site, setSite] = useState('');
  const [concept, setConcept] = useState('');
  const [minSeniority, setMinSeniority] = useState('');
  const [workArrangement, setWorkArrangement] = useState('');
  const [minAnnualSalary, setMinAnnualSalary] = useState('');
  const [hasSalary, setHasSalary] = useState('');
  const [sort, setSort] = useState('lastSeen');

  useEffect(() => {
    if (!searchTerm) return;
    api.facets(searchTerm).then(setFacets).catch(setError);
  }, [api, searchTerm]);

  const load = useCallback(async () => {
    if (!searchTerm) return;
    setLoading(true);

    const query: PostingQuery = {
      searchTerm,
      q: q || undefined,
      site: site || undefined,
      concept: concept || undefined,
      minSeniority: minSeniority || undefined,
      workArrangement: workArrangement || undefined,
      // Deliberately the annualised column rather than the board's raw one: it covers
      // more postings, and it puts day rates and salaries on one scale so a threshold
      // means the same thing for both.
      minAnnualSalary: minAnnualSalary ? Number(minAnnualSalary) : undefined,
      hasSalary: hasSalary === '' ? undefined : hasSalary === 'true',
      sort,
      limit: PAGE_SIZE,
      offset,
      // Asked for explicitly: the API skips the COUNT unless a caller wants it, because it
      // is a second aggregate against a database that may be asleep.
      includeTotal: true,
    };

    try {
      setPage(await api.postings(query));
      setError(undefined);
    } catch (err) {
      setError(err);
    } finally {
      setLoading(false);
    }
  }, [api, searchTerm, q, site, concept, minSeniority, workArrangement,
      minAnnualSalary, hasSalary, sort, offset]);

  useEffect(() => { void load(); }, [load]);

  // Any filter change invalidates the current offset - staying on page 4 of a result set
  // that no longer has four pages shows an empty table for no visible reason.
  const onFilterChange = <T,>(setter: (value: T) => void) => (value: T) => {
    setOffset(0);
    setter(value);
  };

  return (
    <Card
      title="Postings"
      subtitle={page?.total != null ? `${page.total.toLocaleString()} matching` : undefined}
    >
      <div className="filters">
        <div>
          <label htmlFor="q">Title or company</label>
          <input id="q" value={q} placeholder="e.g. backend"
            onChange={(e) => onFilterChange(setQ)(e.target.value)} />
        </div>
        <div>
          <label htmlFor="site">Board</label>
          <select id="site" value={site} onChange={(e) => onFilterChange(setSite)(e.target.value)}>
            <option value="">All</option>
            {facets?.sites.map((s) => (
              <option key={s.name} value={s.name}>{s.name} ({s.count})</option>
            ))}
          </select>
        </div>
        <div>
          <label htmlFor="concept">Skill or area</label>
          <select id="concept" value={concept}
            onChange={(e) => onFilterChange(setConcept)(e.target.value)}>
            <option value="">Any</option>
            {/* Areas first and grouped: picking one matches every skill underneath it,
                which is the question most people actually have. The keys are opaque, so
                the label is what is shown. */}
            <optgroup label="Areas">
              {facets?.concepts.filter((c) => c.key.startsWith('area.')).map((c) => (
                <option key={c.key} value={c.key}>{c.label} ({c.count})</option>
              ))}
            </optgroup>
            <optgroup label="Skills">
              {facets?.concepts.filter((c) => !c.key.startsWith('area.')).map((c) => (
                <option key={c.key} value={c.key}>{c.label} ({c.count})</option>
              ))}
            </optgroup>
          </select>
        </div>
        <div>
          <label htmlFor="seniority">Seniority</label>
          <select id="seniority" value={minSeniority}
            onChange={(e) => onFilterChange(setMinSeniority)(e.target.value)}>
            <option value="">Any</option>
            {/* A floor rather than an exact level, because the scale is ordinal and
                "senior or above" is the question. Unknown is never included. */}
            {['Junior', 'Mid', 'Senior', 'Lead', 'Principal'].map((level) => (
              <option key={level} value={level}>{level} or above</option>
            ))}
          </select>
        </div>
        <div>
          <label htmlFor="arrangement">Working</label>
          <select id="arrangement" value={workArrangement}
            onChange={(e) => onFilterChange(setWorkArrangement)(e.target.value)}>
            <option value="">Any</option>
            <option value="Remote">Remote</option>
            <option value="Hybrid">Hybrid</option>
            <option value="OnSite">On-site</option>
          </select>
        </div>
        <div>
          <label htmlFor="minSalary">Min salary</label>
          <input id="minSalary" type="number" inputMode="numeric" step={5000}
            value={minAnnualSalary} placeholder="e.g. 80000"
            onChange={(e) => onFilterChange(setMinAnnualSalary)(e.target.value)} />
        </div>
        <div>
          <label htmlFor="salary">Salary</label>
          <select id="salary" value={hasSalary} onChange={(e) => onFilterChange(setHasSalary)(e.target.value)}>
            <option value="">Any</option>
            <option value="true">Disclosed</option>
            <option value="false">Not disclosed</option>
          </select>
        </div>
        <div>
          <label htmlFor="sort">Sort</label>
          <select id="sort" value={sort} onChange={(e) => onFilterChange(setSort)(e.target.value)}>
            <option value="lastSeen">Last seen</option>
            <option value="firstSeen">First seen</option>
            <option value="datePosted">Date posted</option>
            <option value="salary">Salary</option>
            <option value="title">Title</option>
          </select>
        </div>
      </div>

      {error ? <ErrorNote error={error} onRetry={() => void load()} /> : null}

      <div className="scroll-x">
        <table>
          <thead>
            <tr>
              <th>Title</th><th>Company</th><th>Level</th><th>Working</th>
              <th>Location</th><th className="num">Salary</th><th className="num">Seen</th><th />
            </tr>
          </thead>
          <tbody>
            {page?.items.map((posting) => (
              <tr key={posting.id}>
                <td>
                  {posting.jobUrl
                    ? <a href={posting.jobUrl} target="_blank" rel="noreferrer noopener">{posting.title}</a>
                    : posting.title}
                  <RequirementPills posting={posting} />
                  <FreshnessPills posting={posting} />
                </td>
                <td>{posting.company ?? '—'}</td>
                <td>{posting.seniority === 'Unknown' ? '—' : posting.seniority}</td>
                <td>
                  {posting.workArrangement === 'Unknown' ? '—' : ARRANGEMENT[posting.workArrangement]}
                  {posting.hybridDaysInOffice != null && (
                    <span className="muted"> · {posting.hybridDaysInOffice}d</span>
                  )}
                </td>
                <td>{posting.city ?? posting.location ?? '—'}</td>
                <td className="num"><Salary posting={posting} /></td>
                <td className="num">{posting.seenCount}</td>
                <td>
                  <button
                    className="btn"
                    onClick={() => setInspecting((current) => (current === posting.id ? undefined : posting.id))}
                    aria-expanded={inspecting === posting.id}
                  >
                    {inspecting === posting.id ? 'Hide' : 'Inspect'}
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {page?.items.length === 0 && !loading && (
        <div className="empty">No postings match these filters.</div>
      )}

      {/* Below the table rather than inside a row: the panel is tall, and expanding a row
          in place pushes everything the reader was comparing off the screen. */}
      {inspecting !== undefined && (
        <div style={{ marginTop: 16 }}>
          <PostingInsightPanel
            api={api}
            postingId={inspecting}
            onClose={() => setInspecting(undefined)}
          />
        </div>
      )}

      <div style={{ display: 'flex', gap: 8, alignItems: 'center', marginTop: 12 }}>
        <button className="btn" disabled={offset === 0 || loading}
          onClick={() => setOffset(Math.max(0, offset - PAGE_SIZE))}>Previous</button>
        <button className="btn" disabled={!page?.hasMore || loading}
          onClick={() => setOffset(offset + PAGE_SIZE)}>Next</button>
        <span className="muted">
          {loading ? 'Loading…' : `${offset + 1}–${offset + (page?.items.length ?? 0)}`}
        </span>
      </div>
    </Card>
  );
}
