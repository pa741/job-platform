import { useCallback, useEffect, useState } from 'react';
import type { JobPlatformApi, PostingQuery } from '../api/client';
import type { FacetsResponse, PageResponse, PostingSummary } from '../api/types';
import { Card, ErrorNote } from '../components/Primitives';

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

export function Postings({ api, searchTerm }: { api: JobPlatformApi; searchTerm: string | undefined }) {
  const [facets, setFacets] = useState<FacetsResponse>();
  const [page, setPage] = useState<PageResponse<PostingSummary>>();
  const [error, setError] = useState<unknown>();
  const [loading, setLoading] = useState(false);
  const [offset, setOffset] = useState(0);

  const [q, setQ] = useState('');
  const [site, setSite] = useState('');
  const [remote, setRemote] = useState('');
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
      remote: remote === '' ? undefined : remote === 'true',
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
  }, [api, searchTerm, q, site, remote, hasSalary, sort, offset]);

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
          <label htmlFor="remote">Remote</label>
          <select id="remote" value={remote} onChange={(e) => onFilterChange(setRemote)(e.target.value)}>
            <option value="">Any</option>
            <option value="true">Remote</option>
            <option value="false">On-site</option>
          </select>
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
              <th>Title</th><th>Company</th><th>Location</th>
              <th>Board</th><th className="num">Salary</th><th className="num">Seen</th>
            </tr>
          </thead>
          <tbody>
            {page?.items.map((posting) => (
              <tr key={posting.id}>
                <td>
                  {posting.jobUrl
                    ? <a href={posting.jobUrl} target="_blank" rel="noreferrer noopener">{posting.title}</a>
                    : posting.title}
                  {posting.isRemote && <span className="pill" style={{ marginLeft: 8 }}>remote</span>}
                  <FreshnessPills posting={posting} />
                </td>
                <td>{posting.company ?? '—'}</td>
                <td>{posting.city ?? posting.location ?? '—'}</td>
                <td>{posting.site}</td>
                <td className="num">
                  {posting.minAmount || posting.maxAmount
                    ? `${posting.currency ?? ''} ${posting.minAmount ?? ''}${posting.maxAmount ? `–${posting.maxAmount}` : ''}`.trim()
                    : '—'}
                </td>
                <td className="num">{posting.seenCount}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {page?.items.length === 0 && !loading && (
        <div className="empty">No postings match these filters.</div>
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
