import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { AuthenticatedTemplate, UnauthenticatedTemplate, useMsal } from '@azure/msal-react';
import { useApi } from './auth/useApi';
import { apiRequest } from './auth/msalConfig';
import type { JobPlatformApi } from './api/client';
import type { SearchTermResponse } from './api/types';
import { Overview } from './pages/Overview';
import { Postings } from './pages/Postings';
import { Profile } from './pages/Profile';
import { Searches } from './pages/Searches';
import { Shortlist } from './pages/Shortlist';
import { Applications } from './pages/Applications';
import { Vocabulary } from './pages/Vocabulary';
import { AiCalls } from './pages/AiCalls';
import { ErrorNote } from './components/Primitives';
import { useTheme, type Theme } from './theme/useTheme';
import { SECTIONS, labelOf, sectionOf, type PageId } from './routing/route';
import { useRoute } from './routing/useRoute';
import { placeIndicator } from './motion/useGsap';
import './theme/app.css';

/**
 * The pages that are about a slice of the corpus, and therefore wait on the search-term
 * bootstrap.
 *
 * Named as a set rather than repeated as `page !== 'profile' && ...` in three places: this
 * list has grown twice, and the third addition is where one of the three copies gets missed
 * and a per-person page starts waiting on a call it has no use for.
 */
const CORPUS_PAGES: PageId[] = ['briefing', 'postings', 'vocabulary'];

export function App() {
  const [theme, setTheme] = useTheme();

  return (
    <div className="app">
      <AuthenticatedTemplate>
        <Dashboard theme={theme} setTheme={setTheme} />
      </AuthenticatedTemplate>
      <UnauthenticatedTemplate>
        <SignIn theme={theme} setTheme={setTheme} />
      </UnauthenticatedTemplate>
    </div>
  );
}

function ThemeToggle({ theme, setTheme }: { theme: Theme; setTheme: (t: Theme) => void }) {
  return (
    <select
      className="btn"
      value={theme}
      aria-label="Colour theme"
      onChange={(e) => setTheme(e.target.value as Theme)}
    >
      <option value="system">System</option>
      <option value="light">Light</option>
      <option value="dark">Dark</option>
    </select>
  );
}

function SignIn({ theme, setTheme }: { theme: Theme; setTheme: (t: Theme) => void }) {
  const { instance } = useMsal();

  return (
    <>
      <header className="chrome">
        <div className="wrap">
          <div className="sectionbar">
            <h1 className="mark">job-platform <span>briefing</span></h1>
            <div className="right"><ThemeToggle theme={theme} setTheme={setTheme} /></div>
          </div>
        </div>
      </header>
      <main>
        <div className="card" style={{ maxWidth: 460, margin: '60px auto', textAlign: 'center' }}>
          <h2 style={{ fontSize: 16 }}>Sign in</h2>
          <p className="muted" style={{ fontSize: 13 }}>
            The dashboard reads job-market data from the platform API, which requires a
            Microsoft Entra account.
          </p>
          <button className="btn" onClick={() => void instance.loginRedirect(apiRequest)}>
            Sign in with Microsoft
          </button>
        </div>
      </main>
    </>
  );
}

/**
 * How old the corpus is, as an age rather than a time.
 *
 * "Ingested 04:12" looks identical whether the NAS ran this morning or has been switched off
 * for a day, and the failure this dashboard cannot otherwise see is the scraper that did not
 * run at all - degraded is handled carefully everywhere and absent was handled nowhere.
 *
 * Reads `updatedAtUtc` rather than `lastScrapeDate`, which is a date: a date parses to
 * midnight, so an age computed from it measures how long ago today started rather than how
 * long ago anything happened.
 */
function freshness(updatedAtUtc: string | null | undefined): { text: string; stale: boolean } {
  if (!updatedAtUtc) return { text: 'never', stale: true };

  const hours = (Date.now() - new Date(updatedAtUtc).getTime()) / 3_600_000;

  if (hours < 1) return { text: 'less than an hour ago', stale: false };
  if (hours < 24) {
    const whole = Math.round(hours);
    return { text: `${whole} hour${whole === 1 ? '' : 's'} ago`, stale: false };
  }

  const days = Math.round(hours / 24);
  // A daily scrape that has not run for more than a day is the thing worth colouring.
  return { text: `${days} day${days === 1 ? '' : 's'} ago`, stale: true };
}

function Dashboard({ theme, setTheme }: { theme: Theme; setTheme: (t: Theme) => void }) {
  const api = useApi();
  const { instance, accounts } = useMsal();
  const { route, go } = useRoute();

  const [terms, setTerms] = useState<SearchTermResponse[]>();
  const [searchTerm, setSearchTerm] = useState<string>();
  const [error, setError] = useState<unknown>();
  const [menuOpen, setMenuOpen] = useState(false);

  const page = route.page;
  const section = sectionOf(page);

  // The search term is the axis everything else partitions on - postings and metrics are
  // both scoped by it - so it is resolved once here and passed down, rather than each page
  // discovering it independently.
  //
  // This call gates the corpus pages, which is why the API serves it from Cosmos rather than
  // SQL: when it read SQL, opening the dashboard while the serverless database was paused
  // left every page - including the Cosmos-only overview - waiting on a wake-up.
  const loadTerms = useCallback(() => {
    setError(undefined);
    api.searchTerms()
      .then((result) => {
        setTerms(result);
        setSearchTerm((current) => current ?? result[0]?.searchTerm);
      })
      .catch(setError);
  }, [api]);

  useEffect(loadTerms, [loadTerms]);

  // Closing the sheet on navigation rather than in every handler that navigates: there are
  // three of them, and the fourth is where one gets forgotten and the menu stays open over
  // the page it just opened.
  useEffect(() => setMenuOpen(false), [page]);

  const ingested = freshness(terms?.find((t) => t.searchTerm === searchTerm)?.updatedAtUtc);

  return (
    <>
      <header className="chrome">
        <div className="wrap">
          <div className="sectionbar">
            <h1 className="mark">job-platform <span>briefing</span></h1>
            <span className="now">{labelOf(page)}</span>

            <SectionPill section={section} onPick={go} />

            <div className="right">
              <div className="dateline">
                {searchTerm ?? 'no search yet'}<br />
                ingested <b className={ingested.stale ? 'stale' : undefined}>{ingested.text}</b>
              </div>

              {terms && terms.length > 1 && (
                <select
                  className="btn"
                  aria-label="Search term"
                  value={searchTerm ?? ''}
                  onChange={(e) => setSearchTerm(e.target.value)}
                >
                  {terms.map((term) => (
                    <option key={term.searchTerm} value={term.searchTerm}>
                      {term.searchTerm} ({term.postingCount})
                    </option>
                  ))}
                </select>
              )}

              <ThemeToggle theme={theme} setTheme={setTheme} />

              <button
                className="btn"
                onClick={() => void instance.logoutRedirect()}
                title={accounts[0]?.username}
              >
                Sign out
              </button>
            </div>

            <button
              className="burger"
              aria-label="Open menu"
              aria-expanded={menuOpen}
              onClick={() => setMenuOpen(true)}
            >
              <i /><i /><i />
            </button>
          </div>

          <PageTabs section={section} page={page} onPick={go} />
        </div>
      </header>

      {menuOpen && (
        <Sheet
          page={page}
          onPick={go}
          onClose={() => setMenuOpen(false)}
          theme={theme}
          setTheme={setTheme}
        />
      )}

      <main>
        {/* The bootstrap gates the corpus pages only. Everything under You is about the
            signed-in person, so neither an error nor a slow load here should stand between
            somebody and their own record. */}
        {error && CORPUS_PAGES.includes(page)
          ? <ErrorNote error={error} onRetry={loadTerms} />
          : null}
        {!error && !terms && CORPUS_PAGES.includes(page) && (
          <div className="empty">Loading…</div>
        )}
        {terms?.length === 0 && CORPUS_PAGES.includes(page) && (
          <div className="empty">
            The platform has no ingested data yet. Add a search under Searches and run the
            scraper, or replay a blob through the ingest function.
          </div>
        )}

        <Page page={page} api={api} searchTerm={searchTerm} go={go} />
      </main>
    </>
  );
}

/**
 * The eight pages, still the ones that exist today.
 *
 * The shell, the routing and the request states land first and the page bodies are replaced a
 * section at a time behind them. Swapping all nine at once would mean nothing rendered until
 * the last one was finished, and every problem found on the way would be found in a dashboard
 * that could not be opened.
 */
function Page({ page, api, searchTerm, go }: {
  page: PageId; api: JobPlatformApi; searchTerm: string | undefined; go: (page: PageId) => void;
}) {
  switch (page) {
    case 'shortlist': return <Shortlist api={api} go={go} />;
    case 'applications': return <Applications api={api} go={go} />;
    case 'profile': return <Profile api={api} />;
    case 'briefing': return searchTerm ? <Overview api={api} searchTerm={searchTerm} /> : null;
    case 'postings': return searchTerm ? <Postings api={api} searchTerm={searchTerm} /> : null;
    case 'vocabulary': return <Vocabulary api={api} searchTerm={searchTerm} />;
    case 'searches': return <Searches api={api} />;
    case 'calls': return <AiCalls api={api} />;
  }
}

/**
 * The section control.
 *
 * Picking a section goes to the page you last had open in it. Sending you to its first page
 * instead throws away where you were, which turns "check something on the Briefing and come
 * back" into losing your place.
 */
function SectionPill({ section, onPick }: {
  section: string; onPick: (page: PageId) => void;
}) {
  const bar = useRef<HTMLDivElement | null>(null);
  const thumb = useRef<HTMLSpanElement | null>(null);
  const lastPage = useRef<Record<string, PageId>>({
    you: 'shortlist', market: 'briefing', system: 'searches',
  });
  const mounted = useRef(false);

  // Layout, not effect: the indicator is positioned from measured text, and doing it after
  // paint shows it in the wrong place for a frame.
  useLayoutEffect(() => {
    const target = bar.current?.querySelector<HTMLElement>('[aria-current="true"]');
    placeIndicator(thumb.current, target ?? null, bar.current, mounted.current, 3);
    mounted.current = true;
  }, [section]);

  // Text metrics change when the webfont lands, and a pill measured against the fallback sits
  // under the wrong word until something else re-renders.
  useEffect(() => {
    if (!document.fonts) return;
    void document.fonts.ready.then(() => {
      const target = bar.current?.querySelector<HTMLElement>('[aria-current="true"]');
      placeIndicator(thumb.current, target ?? null, bar.current, false, 3);
    });
  }, []);

  return (
    <div className="sections" ref={bar}>
      <span className="thumb" ref={thumb} />
      {SECTIONS.map((entry) => (
        <button
          key={entry.id}
          aria-current={entry.id === section}
          onClick={() => onPick(lastPage.current[entry.id] ?? entry.pages[0]!.id)}
        >
          {entry.label}
        </button>
      ))}
    </div>
  );
}

function PageTabs({ section, page, onPick }: {
  section: string; page: PageId; onPick: (page: PageId) => void;
}) {
  const nav = useRef<HTMLElement | null>(null);
  const marker = useRef<HTMLSpanElement | null>(null);
  const mounted = useRef(false);

  useLayoutEffect(() => {
    const target = nav.current?.querySelector<HTMLElement>('[aria-current="page"]');
    placeIndicator(marker.current, target ?? null, nav.current, mounted.current);
    mounted.current = true;
  }, [section, page]);

  useEffect(() => {
    if (!document.fonts) return;
    void document.fonts.ready.then(() => {
      const target = nav.current?.querySelector<HTMLElement>('[aria-current="page"]');
      placeIndicator(marker.current, target ?? null, nav.current, false);
    });
  }, []);

  const pages = SECTIONS.find((s) => s.id === section)?.pages ?? [];

  return (
    <nav className="pages" aria-label="Page" ref={nav}>
      <span className="marker" ref={marker} />
      {pages.map((entry) => (
        <button
          key={entry.id}
          aria-current={entry.id === page ? 'page' : undefined}
          onClick={() => onPick(entry.id)}
        >
          {entry.label}
        </button>
      ))}
    </nav>
  );
}

/**
 * The whole navigation on a narrow screen.
 *
 * Three headed groups rather than eight tabs in a row that scrolls sideways - which is what
 * the old topbar did, and why the header did not fit.
 */
function Sheet({ page, onPick, onClose, theme, setTheme }: {
  page: PageId;
  onPick: (page: PageId) => void;
  onClose: () => void;
  theme: Theme;
  setTheme: (t: Theme) => void;
}) {
  const close = useRef<HTMLButtonElement | null>(null);

  useEffect(() => {
    close.current?.focus();

    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  return (
    <div className="sheet" role="dialog" aria-modal="true" aria-label="Menu">
      <div className="head">
        <h1 className="mark">job-platform <span>briefing</span></h1>
        <button className="close" aria-label="Close menu" onClick={onClose} ref={close}>
          &times;
        </button>
      </div>

      {SECTIONS.map((entry) => (
        <section key={entry.id}>
          <h2>{entry.label}</h2>
          {entry.pages.map((item) => (
            <button
              key={item.id}
              className="page"
              aria-current={item.id === page ? 'page' : undefined}
              onClick={() => onPick(item.id)}
            >
              {item.label}
              {item.id === page && <span>you are here</span>}
            </button>
          ))}
        </section>
      ))}

      <div className="controls">
        <ThemeToggle theme={theme} setTheme={setTheme} />
      </div>
    </div>
  );
}
