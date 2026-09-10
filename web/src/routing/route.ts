/**
 * The eleven pages, and what each one is called in a URL.
 *
 * A page id is unique across sections, so a path needs only the page and not the section it
 * sits in - `/shortlist`, not `/you/shortlist`. The section is derived, which also means
 * moving a page between sections does not break a link somebody saved.
 */
export type PageId =
  | 'shortlist' | 'applications' | 'questions' | 'profile' | 'cvs'
  | 'briefing' | 'postings' | 'vocabulary'
  | 'searches' | 'pipeline' | 'calls';

export type SectionId = 'you' | 'market' | 'system';

export interface Section {
  id: SectionId;
  label: string;
  pages: { id: PageId; label: string }[];
}

/**
 * Sections, in the order they appear.
 *
 * `you` is first because the product is about the person using it and the market is context.
 * The grouping is not decoration: it is the same split that decides which pages wait on the
 * search-term bootstrap and which render against an empty database.
 */
export const SECTIONS: readonly Section[] = [
  {
    id: 'you',
    label: 'You',
    pages: [
      { id: 'shortlist', label: 'Shortlist' },
      { id: 'applications', label: 'Applications' },
      { id: 'questions', label: 'Questions' },
      { id: 'profile', label: 'Profile' },
      // Immediately after Profile, because that adjacency is the whole of the staleness
      // rule: a CV is measured against the profile's last change, so the page that makes
      // six documents go out of date is the one next to the page that says they have.
      { id: 'cvs', label: 'CVs' },
    ],
  },
  {
    id: 'market',
    label: 'Market',
    pages: [
      { id: 'briefing', label: 'Briefing' },
      { id: 'postings', label: 'Postings' },
      { id: 'vocabulary', label: 'Vocabulary' },
    ],
  },
  {
    id: 'system',
    label: 'System',
    pages: [
      { id: 'searches', label: 'Searches' },
      // Next to Searches because System is already the section for numbers a scheduled run
      // reads rather than the browser: what gets scraped, and then what the night is allowed
      // to spend judging and drafting it. The two levers whose effect is immediate are not
      // here - they are on Applications, beside the figures they move.
      { id: 'pipeline', label: 'Pipeline' },
      { id: 'calls', label: 'Model calls' },
    ],
  },
];

export const HOME: PageId = 'shortlist';

/** Where each page lives in a URL. The section prefix is cosmetic; the page id is the key. */
const PATHS: Record<PageId, string> = {
  shortlist: '/shortlist',
  applications: '/applications',
  questions: '/questions',
  profile: '/profile',
  // `/cvs` rather than `/cv-library`: the tail segment is a variant id or the word `new`, and
  // `/cvs/new` reads as an act where `/cv-library/new` reads as a section of a site.
  cvs: '/cvs',
  briefing: '/market',
  postings: '/market/postings',
  vocabulary: '/market/vocabulary',
  searches: '/system/searches',
  pipeline: '/system/pipeline',
  calls: '/system/calls',
};

/** Longest first, so `/market/postings` is not swallowed by `/market`. */
const BY_PATH = (Object.entries(PATHS) as [PageId, string][])
  .sort((a, b) => b[1].length - a[1].length);

export interface Route {
  page: PageId;
  /** The path segment after the page, where it takes one: a posting id, a concept key. */
  id: string | null;
  params: URLSearchParams;
}

export function sectionOf(page: PageId): SectionId {
  const found = SECTIONS.find((s) => s.pages.some((p) => p.id === page));
  return found ? found.id : 'you';
}

export function labelOf(page: PageId): string {
  for (const section of SECTIONS) {
    const found = section.pages.find((p) => p.id === page);
    if (found) return found.label;
  }
  return page;
}

/**
 * Reads a route out of a location.
 *
 * Falls back to the home page rather than rendering a not-found: there is nothing at an
 * unknown path worth explaining, and the shortlist is what somebody typing a URL by hand
 * almost always wanted.
 */
export function parseRoute(pathname: string, search: string): Route {
  const path = pathname.replace(/\/+$/, '') || '/';

  for (const [page, prefix] of BY_PATH) {
    if (path === prefix) {
      return { page, id: null, params: new URLSearchParams(search) };
    }

    if (path.startsWith(`${prefix}/`)) {
      const rest = path.slice(prefix.length + 1);

      // One segment only. A deeper path is not a route this app has, and treating the whole
      // tail as an id would put a slash into a posting id.
      if (rest.length > 0 && !rest.includes('/')) {
        return { page, id: decodeURIComponent(rest), params: new URLSearchParams(search) };
      }
    }
  }

  return { page: HOME, id: null, params: new URLSearchParams(search) };
}

export function buildRoute(page: PageId, id?: string | null, params?: URLSearchParams): string {
  let path = PATHS[page];
  if (id) path += `/${encodeURIComponent(id)}`;

  const query = params?.toString() ?? '';
  return query ? `${path}?${query}` : path;
}
