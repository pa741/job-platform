import type {
  ApplicationDetail, ApplicationSummary, DailyRollup, FacetsResponse, MatchDetail, MatchSummary,
  MeResponse, MetricsSummary, PageResponse, PostingDetail, PostingInsight, PostingSummary, ProfileRequest,
  ProfileResponse, RunResponse, ScraperHealth, SearchTermResponse,
} from './types';

/** Thrown for any non-2xx response, carrying the RFC 9457 detail the API returns. */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
    readonly detail?: string,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

/**
 * Thrown when a request exceeds the deadline.
 *
 * Distinct from ApiError so the UI can say something useful. The postings endpoints read a
 * serverless database that pauses when idle and takes 30-60s to wake, so the first request
 * after a quiet period is legitimately slow - but "slow" must still end, because a promise
 * that never settles leaves a spinner on screen forever with nothing to click.
 */
export class ApiTimeoutError extends Error {
  constructor(readonly timeoutMs: number) {
    super('The request timed out.');
    this.name = 'ApiTimeoutError';
  }
}

/** Generous, because waking a paused database genuinely takes this long. */
const DEFAULT_TIMEOUT_MS = 90_000;

/** Supplies a bearer token. Async because MSAL may need to refresh silently. */
export type TokenProvider = () => Promise<string | null>;

/**
 * Typed wrapper over the API.
 *
 * Every method funnels through one `request`, so the bearer token, error shape and
 * cancellation are handled once. Endpoints are methods rather than free functions so a
 * component takes the whole client and tests can substitute a fake.
 */
export class JobPlatformApi {
  constructor(
    private readonly baseUrl: string,
    private readonly getToken: TokenProvider,
  ) {}

  private async request<T>(path: string, init?: RequestInit): Promise<T> {
    const token = await this.getToken();

    const headers = new Headers(init?.headers);
    headers.set('Accept', 'application/json');
    if (token) headers.set('Authorization', `Bearer ${token}`);
    if (init?.body) headers.set('Content-Type', 'application/json');

    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), DEFAULT_TIMEOUT_MS);

    let response: Response;
    try {
      response = await fetch(`${this.baseUrl}${path}`, { ...init, headers, signal: controller.signal });
    } catch (error) {
      if (error instanceof DOMException && error.name === 'AbortError') {
        throw new ApiTimeoutError(DEFAULT_TIMEOUT_MS);
      }
      throw error;
    } finally {
      clearTimeout(timer);
    }

    if (!response.ok) {
      // The API answers with RFC 9457 problem details; surface `detail` when it is there,
      // because it carries the actionable half of the message.
      let detail: string | undefined;
      try {
        const problem = await response.json();
        detail = problem?.detail ?? problem?.title;
      } catch {
        // A non-JSON error body is not worth failing over.
      }

      throw new ApiError(
        response.status,
        response.status === 401
          ? 'Not signed in, or the token has expired.'
          : `Request failed (${response.status}).`,
        detail,
      );
    }

    if (response.status === 204) return undefined as T;
    return (await response.json()) as T;
  }

  private static query(params: Record<string, string | number | boolean | undefined | null>): string {
    const search = new URLSearchParams();
    for (const [key, value] of Object.entries(params)) {
      if (value !== undefined && value !== null && value !== '') search.set(key, String(value));
    }
    const qs = search.toString();
    return qs ? `?${qs}` : '';
  }

  me = () => this.request<MeResponse>('/api/v1/me');

  searchTerms = () => this.request<SearchTermResponse[]>('/api/v1/search-terms');

  postings = (params: PostingQuery) =>
    this.request<PageResponse<PostingSummary>>(`/api/v1/postings${JobPlatformApi.query({ ...params })}`);

  posting = (id: number) => this.request<PostingDetail>(`/api/v1/postings/${id}`);

  /**
   * Everything concluded about one posting, with the evidence behind each conclusion.
   *
   * A second call rather than more fields on `posting`: it pulls six collections and a
   * company row server-side, and a list view has no use for any of it.
   */
  postingInsight = (id: number) => this.request<PostingInsight>(`/api/v1/postings/${id}/insight`);

  facets = (searchTerm?: string) =>
    this.request<FacetsResponse>(`/api/v1/postings/facets${JobPlatformApi.query({ searchTerm })}`);

  runs = (searchTerm?: string, limit = 20) =>
    this.request<PageResponse<RunResponse>>(`/api/v1/runs${JobPlatformApi.query({ searchTerm, limit })}`);

  summary = (searchTerm: string) =>
    this.request<MetricsSummary>(`/api/v1/metrics/summary${JobPlatformApi.query({ searchTerm })}`);

  rollups = (searchTerm: string, from?: string, to?: string) =>
    this.request<DailyRollup[]>(`/api/v1/metrics/rollups${JobPlatformApi.query({ searchTerm, from, to })}`);

  scraperHealth = (searchTerm: string) =>
    this.request<ScraperHealth>(`/api/v1/metrics/scraper-health${JobPlatformApi.query({ searchTerm })}`);

  // --- profile, matches and applications ------------------------------------
  //
  // None of these takes an identifier for whose data it wants: the API resolves that from the
  // token's `oid` claim. There is deliberately no way to ask for somebody else's.

  /** Null where the caller has not created a profile yet, which is a real and common state. */
  profile = async (): Promise<ProfileResponse | null> => {
    try {
      return await this.request<ProfileResponse>('/api/v1/profile');
    } catch (error) {
      // A 404 here means "you have not filled in the form", not "something went wrong". The
      // caller needs to open an empty form, and making it distinguish that from a failure by
      // inspecting a status code is how one of them ends up showing an error instead.
      if (error instanceof ApiError && error.status === 404) return null;
      throw error;
    }
  };

  saveProfile = (profile: ProfileRequest) =>
    this.request<ProfileResponse>('/api/v1/profile', {
      method: 'PUT',
      body: JSON.stringify(profile),
    });

  deleteProfile = () => this.request<void>('/api/v1/profile', { method: 'DELETE' });

  matches = (params: MatchQuery = {}) =>
    this.request<{ items: MatchSummary[]; offset: number }>(
      `/api/v1/matches${JobPlatformApi.query({ ...params })}`);

  match = (postingId: number) => this.request<MatchDetail>(`/api/v1/matches/${postingId}`);

  applications = (limit = 25) =>
    this.request<{ items: ApplicationSummary[] }>(
      `/api/v1/applications${JobPlatformApi.query({ limit })}`);

  application = (id: number) => this.request<ApplicationDetail>(`/api/v1/applications/${id}`);

  /**
   * Writes a tailored CV and cover letter.
   *
   * The one call in this client that costs real money and takes tens of seconds, so it is
   * always driven by an explicit user action - never by a page opening.
   */
  generateApplication = (postingId: number, instructions?: string) =>
    this.request<ApplicationDetail>(`/api/v1/applications/${postingId}`, {
      method: 'POST',
      body: JSON.stringify({ instructions: instructions ?? null }),
    });

  /**
   * The URL a download link points at.
   *
   * Returned as a string rather than fetched, because the response is a PDF and the browser
   * should handle it. That does mean the bearer token cannot travel in a header, so this is
   * used with a fetch-then-object-URL flow rather than a bare anchor - see downloadPdf.
   */
  applicationPdfUrl = (id: number, kind: 'cv' | 'cover-letter') =>
    `${this.baseUrl}/api/v1/applications/${id}/${kind === 'cv' ? 'cv.pdf' : 'cover-letter.pdf'}`;

  /**
   * Fetches a generated PDF as a blob.
   *
   * A plain anchor cannot carry an Authorization header, and these endpoints require one -
   * they return somebody's CV. So the file is fetched with the token, turned into an object
   * URL and handed to a synthetic link; the caller revokes the URL afterwards.
   */
  applicationPdf = async (id: number, kind: 'cv' | 'cover-letter'): Promise<Blob> => {
    const token = await this.getToken();
    const headers = new Headers({ Accept: 'application/pdf' });
    if (token) headers.set('Authorization', `Bearer ${token}`);

    const response = await fetch(this.applicationPdfUrl(id, kind), { headers });

    if (!response.ok) {
      throw new ApiError(response.status, `Could not download the PDF (${response.status}).`);
    }

    return response.blob();
  };
}

export interface MatchQuery {
  /** Hide everything scoring below this. The page's main control. */
  minScore?: number;
  /** Only rows the model has judged. Useful the morning after a profile change. */
  assessedOnly?: boolean;
  limit?: number;
  offset?: number;
}

export interface PostingQuery {
  searchTerm?: string;
  q?: string;
  site?: string;
  company?: string;
  jobType?: string;
  country?: string;
  city?: string;
  remote?: boolean;
  hasSalary?: boolean;
  minSalary?: number;

  /** A concept key. Matched through the closure, so area.* includes everything beneath it. */
  concept?: string;
  minSeniority?: string;
  maxSeniority?: string;
  roleFamily?: string;
  workArrangement?: string;
  /** Filters the annualised figure, not the board's raw column. */
  minAnnualSalary?: number;
  /** Set false to see only salaries an employer typed into a salary field. */
  includeTextSalary?: boolean;
  securityClearance?: boolean;
  ir35?: string;

  sort?: string;
  order?: string;
  limit?: number;
  offset?: number;
  includeTotal?: boolean;
}
