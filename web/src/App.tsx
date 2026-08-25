import { useCallback, useEffect, useState } from 'react';
import { AuthenticatedTemplate, UnauthenticatedTemplate, useMsal } from '@azure/msal-react';
import { useApi } from './auth/useApi';
import { apiRequest } from './auth/msalConfig';
import type { SearchTermResponse } from './api/types';
import { Overview } from './pages/Overview';
import { Postings } from './pages/Postings';
import { Profile } from './pages/Profile';
import { Matches } from './pages/Matches';
import { ErrorNote } from './components/Primitives';
import { useTheme, type Theme } from './theme/useTheme';
import './theme/app.css';

type Page = 'overview' | 'postings' | 'matches' | 'profile';

const PAGES: { id: Page; label: string }[] = [
  { id: 'overview', label: 'Overview' },
  { id: 'postings', label: 'Postings' },
  { id: 'matches', label: 'Matches' },
  { id: 'profile', label: 'Profile' },
];

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
      <header className="topbar">
        <h1>job-platform</h1>
        <div className="spacer" />
        <ThemeToggle theme={theme} setTheme={setTheme} />
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

function Dashboard({ theme, setTheme }: { theme: Theme; setTheme: (t: Theme) => void }) {
  const api = useApi();
  const { instance, accounts } = useMsal();

  const [page, setPage] = useState<Page>('overview');
  const [terms, setTerms] = useState<SearchTermResponse[]>();
  const [searchTerm, setSearchTerm] = useState<string>();
  const [error, setError] = useState<unknown>();

  // The search term is the axis everything else partitions on - postings and metrics are
  // both scoped by it - so it is resolved once here and passed down, rather than each page
  // discovering it independently.
  //
  // This call gates the entire dashboard, which is why the API serves it from Cosmos rather
  // than SQL: when it read SQL, opening the dashboard while the serverless database was
  // paused left every page - including the Cosmos-only overview - waiting on a wake-up.
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

  return (
    <>
      <header className="topbar">
        <h1>job-platform</h1>

        <nav className="nav">
          {PAGES.map((entry) => (
            <button
              key={entry.id}
              onClick={() => setPage(entry.id)}
              aria-current={page === entry.id ? 'page' : undefined}
            >
              {entry.label}
            </button>
          ))}
        </nav>

        <div className="spacer" />

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
      </header>

      <main>
        {/* The search-term bootstrap gates the corpus pages only. Profile and Matches are
            about the signed-in person, so neither an error nor a slow load here should stand
            between somebody and their own record. */}
        {error && page !== 'profile' && page !== 'matches'
          ? <ErrorNote error={error} onRetry={loadTerms} />
          : null}
        {!error && !terms && page !== 'profile' && page !== 'matches' && (
          <div className="empty">Loading…</div>
        )}
        {terms?.length === 0 && page !== 'profile' && page !== 'matches' && (
          <div className="empty">
            The platform has no ingested data yet. Run the scraper, or replay a blob through
            the ingest function.
          </div>
        )}

        {searchTerm && page === 'overview' && <Overview api={api} searchTerm={searchTerm} />}
        {searchTerm && page === 'postings' && <Postings api={api} searchTerm={searchTerm} />}

        {/* Neither of these is scoped by search term, and neither waits on one. They are about
            the signed-in person rather than about a slice of the corpus, so they render even
            when the platform has ingested nothing at all - which is exactly the state somebody
            filling in their profile for the first time is in. */}
        {page === 'matches' && <Matches api={api} />}
        {page === 'profile' && <Profile api={api} />}
      </main>
    </>
  );
}
