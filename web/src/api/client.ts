import type {
  ApplicationDetail, ApplicationSummary, ConceptDetail, ConceptListItem,
  DailyRollup, FacetsResponse, MatchDetail, MatchSummary,
  MeResponse, MetricsSummary, PageResponse, PostingDetail, PostingInsight, PostingSummary, ProfileRequest,
  ProfileResponse, RunResponse, ScraperHealth, SearchTermResponse, SourceComposition,
  AiCallResponse, AiCallTotalsResponse,
  ScraperSearchRequest, ScraperSearchListResponse, ScraperSearchOptionsResponse,
  SkillGapResponse, Submission, SubmissionEvent,
  AnswerQuestionRequest, AnswerQuestionResponse, OpenQuestion,
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
   * The AI call ledger.
   *
   * Failures by default, because the losses are the part nobody could see: a sweep once
   * discarded five batches of ten while reporting success. Served from Cosmos like every
   * other dashboard read, so leaving this page open costs the SQL grant nothing.
   */
  aiCalls = (params: { days?: number; failuresOnly?: boolean; limit?: number } = {}) =>
    this.request<{ items: AiCallResponse[] }>(
      `/api/v1/ai-calls${JobPlatformApi.query({ ...params })}`);

  aiCallSummary = (days = 7) =>
    this.request<{ days: number; items: AiCallTotalsResponse[] }>(
      `/api/v1/ai-calls/summary${JobPlatformApi.query({ days })}`);

  /**
   * Mints this client's access to the live feed.
   *
   * A POST because it has an effect - it issues a short-lived token against a service with a
   * connection budget - and because SignalR's own negotiate is a POST, so a reader comparing the
   * two is not left wondering whether this is a different kind of handshake.
   *
   * Rejects with a 503 where the deployment has no realtime service, which is a normal state
   * rather than an error: the feed is optional, and the caller falls back to what it already has.
   */
  negotiateRealtime = () =>
    this.request<{ url: string; accessToken: string }>(
      '/api/v1/realtime/negotiate', { method: 'POST' });

  /**
   * Everything concluded about one posting, with the evidence behind each conclusion.
   *
   * A second call rather than more fields on `posting`: it pulls six collections and a
   * company row server-side, and a list view has no use for any of it.
   */
  postingInsight = (id: number) => this.request<PostingInsight>(`/api/v1/postings/${id}/insight`);

  /**
   * The whole vocabulary.
   *
   * Served from the graph shipped in the API's build and touches no database, which is why it
   * is safe to load on page open where nothing else that reads SQL is.
   */
  concepts = () =>
    this.request<{ version: number; items: ConceptListItem[] }>('/api/v1/concepts');

  concept = (key: string, searchTerm?: string) =>
    this.request<ConceptDetail>(`/api/v1/concepts/${encodeURIComponent(key)}${JobPlatformApi.query({ searchTerm })}`);

  sourceComposition = (searchTerm?: string) =>
    this.request<SourceComposition>(`/api/v1/concepts/source-composition${JobPlatformApi.query({ searchTerm })}`);

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

  // --- scraper searches -----------------------------------------------------
  //
  // Per-principal like the profile, and scoped the same way: the slug in a path names one of
  // the caller's own searches, and the API answers 404 for anybody else's.
  //
  // Every mutation answers with the caller's whole set rather than the one row it touched. A
  // save rewrites the scraper's configuration for every search at once, so the publish state
  // that comes back describes the set - and one round trip beats a save followed by a refetch
  // against a database that pauses when idle.

  searches = () => this.request<ScraperSearchListResponse>('/api/v1/searches');

  /** The boards, job types and bounds a form may offer. Served, never hard-coded here. */
  searchOptions = () => this.request<ScraperSearchOptionsResponse>('/api/v1/searches/options');

  createSearch = (search: ScraperSearchRequest) =>
    this.request<ScraperSearchListResponse>('/api/v1/searches', {
      method: 'POST',
      body: JSON.stringify(search),
    });

  updateSearch = (slug: string, search: ScraperSearchRequest) =>
    this.request<ScraperSearchListResponse>(`/api/v1/searches/${encodeURIComponent(slug)}`, {
      method: 'PUT',
      body: JSON.stringify(search),
    });

  deleteSearch = (slug: string) =>
    this.request<ScraperSearchListResponse>(`/api/v1/searches/${encodeURIComponent(slug)}`, {
      method: 'DELETE',
    });

  /** Rewrites the scraper's configuration from what is stored. The repair path. */
  publishSearches = () =>
    this.request<{ published: boolean; publishedUtc: string }>('/api/v1/searches/publish', {
      method: 'POST',
    });

  /**
   * The candidate's applications, most recently active first.
   *
   * Reads SQL, like the profile and the searches, and for the same reason it is allowed to:
   * opened by a person, not polled. It must never join the bootstrap sequence.
   */
  submissions = () => this.request<{ items: Submission[] }>('/api/v1/submissions');

  submissionEvents = (id: number) =>
    this.request<{ items: SubmissionEvent[] }>(`/api/v1/submissions/${id}/events`);

  /**
   * Records that an application was sent.
   *
   * Idempotent by construction - one submission per posting - so a double-click converges on
   * the row that already exists rather than making a second.
   */
  createSubmission = (postingId: number) =>
    this.request<Submission>('/api/v1/submissions', {
      method: 'POST',
      body: JSON.stringify({ postingId }),
    });

  /**
   * Appends one event to an application's log.
   *
   * The idempotency key is minted here rather than by the server, because only the caller knows
   * whether two requests are one event or two - a retry after a timeout is one, and a person
   * recording a second interview round is two.
   */
  recordSubmissionEvent = (
    id: number,
    event: { type: string; atUtc?: string; stage?: string; note?: string; source?: string },
  ) =>
    this.request<{ recorded: boolean }>(`/api/v1/submissions/${id}/events`, {
      method: 'POST',
      body: JSON.stringify({ ...event, idempotencyKey: crypto.randomUUID() }),
    });

  /**
   * The questions this system refused to answer and has put to the candidate, oldest first.
   *
   * Oldest first inverts every other list in this client, and that is the point: those are
   * histories, where the last thing that happened is the interesting one, and this is a queue
   * to be drained. The question that has held an application back for three days is the one to
   * put in front of somebody.
   *
   * Per-principal and SQL-backed like the submissions, so it is read when the page opens and
   * never on a bootstrap or a poll.
   */
  openQuestions = () => this.request<{ items: OpenQuestion[] }>('/api/v1/questions');

  /**
   * Records what the candidate answered, and closes the question.
   *
   * <b>The one write in this client that says a person typed it.</b> Everything arriving over
   * the tool surface is stored as a client's assertion; only this route may stamp an answer as
   * the candidate's own, and it is why there is no `source` in the body — the API reads it from
   * the token rather than from anything a caller can fill in.
   *
   * No idempotency key, unlike the event log. Recording the same answer twice converges on the
   * stored row rather than writing a second one, because the answer store is keyed on the
   * question and supersedes rather than appends; and the question it closes stays closed on the
   * first close, so a retry after a timeout is safe without one.
   */
  answerQuestion = (questionId: number, answer: AnswerQuestionRequest) =>
    this.request<AnswerQuestionResponse>(`/api/v1/questions/${questionId}/answer`, {
      method: 'POST',
      body: JSON.stringify(answer),
    });

  matches = (params: MatchQuery = {}) =>
    this.request<{ items: MatchSummary[]; offset: number }>(
      `/api/v1/matches${JobPlatformApi.query({ ...params })}`);

  match = (postingId: number) => this.request<MatchDetail>(`/api/v1/matches/${postingId}`);

  /**
   * Sets, or clears, "not interested" on one match.
   *
   * A PUT because it sets a state rather than appending to a log, so a client retrying one it
   * is unsure landed gets the same answer the second time. The undo is the same call with
   * `false` - a dismissal that cannot be taken back is one nobody will risk using.
   */
  /**
   * What the candidate's own matched band asks for that their profile does not hold.
   *
   * Per-principal and SQL-backed, so it carries no output cache and must never sit on a
   * bootstrap or polling path - load it when the market page renders, not before.
   */
  skillGap = (searchTerm?: string, minScore?: number) =>
    this.request<SkillGapResponse>(
      `/api/v1/matches/skill-gap${JobPlatformApi.query({ searchTerm, minScore })}`);

  setMatchDismissed = (postingId: number, dismissed: boolean) =>
    this.request<{ postingId: number; dismissedAtUtc: string | null }>(
      `/api/v1/matches/${postingId}/dismissed`,
      { method: 'PUT', body: JSON.stringify({ dismissed }) });

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

    // The same deadline every other call carries. This one used a bare fetch with no
    // AbortController, so the single download in the product was the single request that
    // could hang forever - against endpoints that render the PDF per request out of a
    // database which pauses when idle, which is exactly where a request hangs.
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), DEFAULT_TIMEOUT_MS);

    let response: Response;
    try {
      response = await fetch(this.applicationPdfUrl(id, kind), {
        headers, signal: controller.signal,
      });
    } catch (error) {
      if (error instanceof DOMException && error.name === 'AbortError') {
        throw new ApiTimeoutError(DEFAULT_TIMEOUT_MS);
      }
      throw error;
    } finally {
      clearTimeout(timer);
    }

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
  /** The dismissed pile instead of the shortlist. Never both - see the repository. */
  dismissed?: boolean;
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
