import type {
  DailyRollup, FacetsResponse, MeResponse, MetricsSummary,
  PageResponse, PostingDetail, PostingSummary, RunResponse, ScraperHealth, SearchTermResponse,
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
  sort?: string;
  order?: string;
  limit?: number;
  offset?: number;
  includeTotal?: boolean;
}
