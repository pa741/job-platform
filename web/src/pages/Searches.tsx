import { useCallback, useEffect, useState } from 'react';
import type { JobPlatformApi } from '../api/client';
import type {
  ScraperSearchListResponse, ScraperSearchOptionsResponse,
  ScraperSearchRequest, ScraperSearchResponse,
} from '../api/types';
import { Card, ErrorNote, Field } from '../components/Primitives';

/**
 * One card on the page: what is stored, and what the person has typed since.
 *
 * `slug` is null for a search that has not been saved yet - it is the platform that assigns
 * one, so there is nothing to show until the first save. `saved` is the last version the
 * server confirmed, and comparing against it is what decides whether Save is worth offering.
 */
type Draft = {
  slug: string | null;
  form: ScraperSearchRequest;
  saved: ScraperSearchRequest | null;
};

const BLANK: ScraperSearchRequest = {
  name: '', enabled: true, searchTerm: '',
  sites: ['indeed', 'linkedin', 'freehire'],
  location: null, countryIndeed: null, isRemote: null,
  hoursOld: 24, resultsWanted: 500, jobType: null,
  freehireFilters: {},
};

/** Board names the API returns are wire spellings; these are what a person should read. */
const SITE_LABELS: Record<string, string> = {
  indeed: 'Indeed',
  linkedin: 'LinkedIn',
  freehire: 'freehire',
};

/**
 * The searches the scraper runs, owned by whoever configured them.
 *
 * **Per-principal, like the profile, and deliberately not part of the dashboard's bootstrap.**
 * Everything here reads Azure SQL, which pauses when idle; a page somebody opened can wait for
 * it, and the search-term picker every other page depends on must not.
 *
 * The corpus itself stays shared. Owning a search decides what gets scraped, not who may read
 * the postings it found - a job advert is public text, and one posting is legitimately turned
 * up by several people's searches.
 */
export function Searches({ api }: { api: JobPlatformApi }) {
  const [drafts, setDrafts] = useState<Draft[]>();
  const [meta, setMeta] = useState<{ published: boolean; publishedUtc: string | null }>();
  const [options, setOptions] = useState<ScraperSearchOptionsResponse>();
  const [error, setError] = useState<unknown>();
  const [busy, setBusy] = useState<string | null>(null);

  const load = useCallback(() => {
    setError(undefined);

    Promise.all([api.searches(), api.searchOptions()])
      .then(([list, vocabulary]) => {
        setDrafts(list.searches.map(toDraft));
        setMeta({ published: list.published, publishedUtc: list.publishedUtc });
        setOptions(vocabulary);
      })
      .catch(setError);
  }, [api]);

  useEffect(load, [load]);

  /**
   * Folds a mutation's response back in without discarding other cards' unsaved edits.
   *
   * Every mutation answers with the whole set, because a save republishes the configuration for
   * all of them. Replacing the local state wholesale would be simpler and would throw away
   * whatever somebody had typed into the card below - so only the row that was saved is taken
   * from the response, and the publish state, which describes the set.
   */
  const absorb = (response: ScraperSearchListResponse, savedSlug: string | null) => {
    setMeta({ published: response.published, publishedUtc: response.publishedUtc });

    setDrafts((current) => {
      if (!current) return response.searches.map(toDraft);

      const known = new Set(current.map((draft) => draft.slug).filter(Boolean));

      // A create has no slug to match on, so the new row is the one the client had not seen.
      const settled = savedSlug
        ? response.searches.find((search) => search.slug === savedSlug)
        : response.searches.find((search) => !known.has(search.slug));

      if (!settled) return current;

      const index = savedSlug
        ? current.findIndex((draft) => draft.slug === savedSlug)
        : current.findIndex((draft) => draft.slug === null);

      if (index < 0) return current;

      return current.map((draft, i) => (i === index ? toDraft(settled) : draft));
    });
  };

  const save = (index: number) => {
    const draft = drafts?.[index];
    if (!draft) return;

    setBusy(draft.slug ?? '(new)');
    setError(undefined);

    const request = draft.slug
      ? api.updateSearch(draft.slug, draft.form)
      : api.createSearch(draft.form);

    request
      .then((response) => absorb(response, draft.slug))
      .catch(setError)
      .finally(() => setBusy(null));
  };

  const remove = (index: number) => {
    const draft = drafts?.[index];
    if (!draft) return;

    // An unsaved card has nothing on the server to delete.
    if (!draft.slug) {
      setDrafts((current) => current?.filter((_, i) => i !== index));
      return;
    }

    setBusy(draft.slug);
    setError(undefined);

    api.deleteSearch(draft.slug)
      .then((response) => {
        setMeta({ published: response.published, publishedUtc: response.publishedUtc });
        setDrafts((current) => current?.filter((_, i) => i !== index));
      })
      .catch(setError)
      .finally(() => setBusy(null));
  };

  const republish = () => {
    setBusy('(publish)');
    setError(undefined);

    api.publishSearches()
      .then((result) => setMeta({ published: result.published, publishedUtc: result.publishedUtc }))
      .catch(setError)
      .finally(() => setBusy(null));
  };

  const update = (index: number, form: ScraperSearchRequest) =>
    setDrafts((current) => current?.map((draft, i) => (i === index ? { ...draft, form } : draft)));

  if (error && !drafts) return <ErrorNote error={error} onRetry={load} />;
  if (!drafts || !options) return <div className="empty">Loading…</div>;

  const enabled = drafts.filter((d) => d.form.enabled).length;

  return (
    <div className="stack">
      <p className="lede">
        <b>{drafts.length}</b> search{drafts.length === 1 ? '' : 'es'}
        {enabled !== drafts.length && <>, <b>{enabled}</b> enabled</>}. The scraper rebuilds its
        configuration from these rather than the other way round.
      </p>

      <p className="lede-note">
        This is the page to open when nothing has been scraped yet: it waits on nothing, because
        it is about your configuration rather than about the corpus. A failed publish does not
        fail the save — the configuration is stored and only the write to the scraper did not
        happen, so the line below is the record and Republish is the retry.
      </p>

      {error ? <ErrorNote error={error} /> : null}

      <Card
        title="Your searches"
        subtitle={publishSummary(meta)}
        actions={
          <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
            <button
              className="btn"
              onClick={republish}
              disabled={busy !== null}
              title="Rewrites the scraper's configuration from what is stored."
            >
              {busy === '(publish)' ? 'Publishing…' : 'Republish'}
            </button>
            <button
              className="btn"
              disabled={busy !== null || drafts.some((draft) => draft.slug === null)}
              onClick={() => setDrafts((current) => [
                ...(current ?? []), { slug: null, form: { ...BLANK }, saved: null },
              ])}
            >
              Add a search
            </button>
          </div>
        }
      >
        <p className="muted" style={{ fontSize: 13, margin: 0 }}>
          These are the searches the scraper runs on its next scheduled pass. Each one is scraped
          and uploaded separately, so a run costs the sum of them — and searches you share with
          somebody else are scraped once each, not once between you.
        </p>
        <p className="muted" style={{ fontSize: 13, marginBottom: 0 }}>
          Postings are not private to a search. The corpus is shared, and one advert is
          legitimately found by several searches; what you own here is what gets looked for.
        </p>
      </Card>

      {drafts.length === 0 && (
        <div className="empty">
          No searches yet. Until you add one — or while none are enabled — the scraper falls back
          to the searches configured on the machine it runs on.
        </div>
      )}

      {drafts.map((draft, index) => (
        <SearchCard
          key={draft.slug ?? '(new)'}
          draft={draft}
          options={options}
          busy={busy !== null}
          saving={busy === (draft.slug ?? '(new)')}
          onChange={(form) => update(index, form)}
          onSave={() => save(index)}
          onRemove={() => remove(index)}
        />
      ))}
    </div>
  );
}

function SearchCard({ draft, options, busy, saving, onChange, onSave, onRemove }: {
  draft: Draft;
  options: ScraperSearchOptionsResponse;
  busy: boolean;
  saving: boolean;
  onChange: (form: ScraperSearchRequest) => void;
  onSave: () => void;
  onRemove: () => void;
}) {
  const { form } = draft;
  const dirty = JSON.stringify(form) !== JSON.stringify(draft.saved);

  const set = <K extends keyof ScraperSearchRequest>(key: K, value: ScraperSearchRequest[K]) =>
    onChange({ ...form, [key]: value });

  const toggleSite = (site: string) => set(
    'sites',
    form.sites.includes(site) ? form.sites.filter((s) => s !== site) : [...form.sites, site],
  );

  return (
    <Card
      title={form.name || 'New search'}
      subtitle={
        draft.slug
          // The slug is the name every other page knows this search by, so it is shown rather
          // than hidden: somebody comparing this page to the search-term picker needs the link.
          ? `Known everywhere else as “${draft.slug}”`
          : 'Not saved yet. The platform assigns its identifier on the first save.'
      }
      actions={
        <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
          {!form.enabled && <span className="pill">Paused</span>}
          {dirty && <span className="pill warning">Unsaved</span>}
          <button className="btn" onClick={onSave} disabled={busy || !dirty}>
            {saving ? 'Saving…' : 'Save'}
          </button>
          <button className="btn" onClick={onRemove} disabled={busy}>
            Delete
          </button>
        </div>
      }
    >
      <div className="form-grid">
        <Field label="Name" hint="Yours to choose. Used to derive the identifier, once.">
          <input value={form.name} onChange={(e) => set('name', e.target.value)} />
        </Field>
        <Field label="Search term" hint="What is typed into the board's own search box.">
          <input value={form.searchTerm} onChange={(e) => set('searchTerm', e.target.value)} />
        </Field>
        <Field label="Location">
          <input
            value={form.location ?? ''}
            placeholder="London, UK"
            onChange={(e) => set('location', e.target.value || null)}
          />
        </Field>
        <Field label="Indeed country" hint="Indeed wants the country named separately.">
          <input
            value={form.countryIndeed ?? ''}
            placeholder="UK"
            onChange={(e) => set('countryIndeed', e.target.value || null)}
          />
        </Field>
      </div>

      <Field label="Job boards" hint="Each board is scraped for this search.">
        <div className="chips">
          {options.sites.map((site) => (
            <label key={site} className="check" style={{ marginRight: 12 }}>
              <input
                type="checkbox"
                checked={form.sites.includes(site)}
                onChange={() => toggleSite(site)}
              />
              {SITE_LABELS[site] ?? site}
            </label>
          ))}
        </div>
      </Field>

      <div className="form-grid">
        <Field
          label="Working arrangement"
          hint="No preference asks the boards for everything, which is not the same as asking for on-site."
        >
          <select
            value={form.isRemote === null ? '' : String(form.isRemote)}
            onChange={(e) => set('isRemote', e.target.value === '' ? null : e.target.value === 'true')}
          >
            <option value="">No preference</option>
            <option value="true">Remote only</option>
            <option value="false">Not remote</option>
          </select>
        </Field>

        <Field label="Job type">
          <select
            value={form.jobType ?? ''}
            onChange={(e) => set('jobType', e.target.value || null)}
          >
            <option value="">Any</option>
            {options.jobTypes.map((type) => (
              <option key={type} value={type}>{type}</option>
            ))}
          </select>
        </Field>

        <Field
          label="Look back (hours)"
          hint="How fresh a posting has to be. The daily run usually wants 24."
        >
          <input
            type="number" min={1} max={options.maxHoursOld}
            value={form.hoursOld ?? ''}
            onChange={(e) => set('hoursOld', numberOrNull(e.target.value))}
          />
        </Field>

        <Field
          label="Results wanted"
          hint={`Per board, per run. At most ${options.maxResultsWanted} — searches run one after another, so this adds up across all of them.`}
        >
          <input
            type="number" min={1} max={options.maxResultsWanted}
            value={form.resultsWanted ?? ''}
            onChange={(e) => set('resultsWanted', numberOrNull(e.target.value))}
          />
        </Field>
      </div>

      <FreehireFilters
        filters={form.freehireFilters}
        keys={options.freehireFilterKeys}
        onChange={(filters) => set('freehireFilters', filters)}
      />

      <label className="check">
        <input
          type="checkbox"
          checked={form.enabled}
          onChange={(e) => set('enabled', e.target.checked)}
        />
        Run this search. Unticking keeps it here and stops scraping it.
      </label>
    </Card>
  );
}

/**
 * Extra freehire facets, over the options above.
 *
 * The keys come from the API rather than from a list in here: they are bounded server-side —
 * these strings end up inside a parameter the scraper forwards — and a dropdown offering one
 * the API refuses would fail a save for a reason nobody on this page can see.
 */
function FreehireFilters({ filters, keys, onChange }: {
  filters: Record<string, string>;
  keys: string[];
  onChange: (filters: Record<string, string>) => void;
}) {
  const entries = Object.entries(filters);
  const unused = keys.filter((key) => !(key in filters));

  return (
    <Field
      label="freehire filters"
      hint="Optional, and only affects freehire. An unknown value matches nothing rather than failing, so check the facet list before relying on one."
    >
      <div>
        {entries.map(([key, value]) => (
          <div key={key} style={{ display: 'flex', gap: 8, alignItems: 'center', marginBottom: 6 }}>
            <span className="pill">{key}</span>
            <input
              value={value}
              style={{ flex: 1 }}
              onChange={(e) => onChange({ ...filters, [key]: e.target.value })}
            />
            <button
              className="btn"
              aria-label={`Remove ${key}`}
              onClick={() => {
                const next = { ...filters };
                delete next[key];
                onChange(next);
              }}
            >
              Remove
            </button>
          </div>
        ))}

        {unused.length > 0 && (
          <select
            className="btn"
            value=""
            aria-label="Add a freehire filter"
            onChange={(e) => e.target.value && onChange({ ...filters, [e.target.value]: '' })}
          >
            <option value="">Add a filter…</option>
            {unused.map((key) => (
              <option key={key} value={key}>{key}</option>
            ))}
          </select>
        )}
      </div>
    </Field>
  );
}

/**
 * What the header says about the scraper having been told.
 *
 * Three states rather than two, because they need different answers. Never published is the
 * normal state of a fresh deployment; a failed publish is something to press Republish about;
 * and a timestamp is the only thing that answers "why is my new search not running yet".
 */
function publishSummary(meta?: { published: boolean; publishedUtc: string | null }): string {
  if (!meta) return '';

  // `published` is read before `publishedUtc`, and the order is the point. A failed publish
  // does not fail the save - the configuration is stored and the write to blob storage is what
  // did not happen - so a timestamp from the last write that DID succeed can sit beside a
  // failure. Reporting that timestamp first would say "last written <date>" about a scraper
  // still running the previous configuration, which is the one thing this line exists to
  // prevent somebody believing.
  if (!meta.published) {
    return meta.publishedUtc
      ? `The last write failed. The scraper is still running the configuration written ${new Date(meta.publishedUtc).toLocaleString()} — press Republish.`
      : 'The scraper has not been told about these yet. Press Republish, or check that this deployment has scraper configuration storage.';
  }

  return meta.publishedUtc
    ? `The scraper's configuration was last written ${new Date(meta.publishedUtc).toLocaleString()}.`
    : 'The scraper has been told about these searches.';
}

function toDraft(search: ScraperSearchResponse): Draft {
  const { slug, createdUtc, updatedUtc, ...form } = search;
  void createdUtc;
  void updatedUtc;

  return { slug, form, saved: form };
}

function numberOrNull(value: string): number | null {
  if (value.trim() === '') return null;
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : null;
}
