/**
 * Mirrors the API's response contracts (`src/JobPlatform.Api/Features/**`).
 *
 * Hand-written rather than generated from the OpenAPI document, deliberately: generation
 * would be worth it once the shape settles, but while the API is iterating a hand-written
 * file is one place to look and diff. The API's contract tests are what stop it drifting.
 */

export interface PageResponse<T> {
  items: T[];
  hasMore: boolean;
  total: number | null;
  limit: number;
  offset: number;
}

export interface PostingSummary {
  id: number;
  sourceKey: string;
  site: string;
  title: string;
  company: string | null;
  location: string | null;
  city: string | null;
  country: string | null;
  /** Null where the board said nothing, which is most of the corpus - not false. */
  isRemote: boolean | null;
  jobType: string | null;
  datePosted: string | null;
  /** What the scraper delivered. Populated for fewer postings than the annualised pair. */
  minAmount: number | null;
  maxAmount: number | null;
  currency: string | null;
  salaryInterval: string | null;

  /**
   * Salary on one scale, from the board's columns where it filled them and from the
   * description where it did not. This is the one to display: it covers more postings, and
   * a day rate lands on the same scale as a salary so the two can be compared.
   */
  annualSalaryMin: number | null;
  annualSalaryMax: number | null;
  annualSalaryCurrency: string | null;
  /** True where the figure came from prose. Weaker evidence, and worth marking as such. */
  salaryFromText: boolean;
  /**
   * What the source said before annualisation. A GBP 600/day contract annualised to 156,000
   * is not a 156,000 salary, and this is the only field that distinguishes them.
   */
  salaryStatedInterval: string | null;

  seniority: string;
  roleFamily: string;
  /** The three-way answer isRemote cannot express: OnSite, Hybrid, Remote, Unknown. */
  workArrangement: string;
  hybridDaysInOffice: number | null;
  yearsExperienceMin: number | null;
  yearsExperienceMax: number | null;
  requiresSecurityClearance: boolean;
  /** 'inside', 'outside', or null. UK contract market only. */
  ir35: string | null;

  jobUrl: string | null;
  /** Length only - the description itself is on the detail endpoint. */
  descriptionLength: number;
  /**
   * How much of the posting's own freshness claim to believe. freehire only, so null
   * on every scraped board - and `fakeFreshness: null` means nobody checked, which is
   * not the same as `false`.
   */
  freshnessClass: string | null;
  postingAgeDays: number | null;
  repostCount: number | null;
  fakeFreshness: boolean | null;
  firstSeenUtc: string;
  lastSeenUtc: string;
  seenCount: number;
  /** Every search that turned this posting up - it can match more than one. */
  searchTerms: string[];
}

export interface PostingDetail {
  summary: PostingSummary;
  description: string | null;
  jobUrlDirect: string | null;
  companyUrl: string | null;
  jobLevel: string | null;
  jobFunction: string | null;
  companyIndustry: string | null;
  salarySource: string | null;
  /** freehire's synopsis. Named synopsis because `summary` here is the list contract. */
  synopsis: string | null;
  experienceRange: string | null;
  companyNumEmployees: string | null;
  contentHash: string;
  firstSeenRunId: number;
  lastSeenRunId: number;
}

export interface NamedCount {
  name: string;
  count: number;
}

export interface FacetsResponse {
  searchTerm: string | null;
  total: number;
  remoteCount: number;
  withSalaryCount: number;
  earliestDatePosted: string | null;
  latestDatePosted: string | null;
  lastSeenUtc: string | null;
  sites: NamedCount[];
  jobTypes: NamedCount[];
  countries: NamedCount[];
  cities: NamedCount[];
  companies: NamedCount[];
  /** Concepts and domains present. The key prefix says which: area.* or skill.*. */
  concepts: ConceptCount[];
}

export interface ConceptCount {
  key: string;
  label: string;
  count: number;
}

/** Served from Cosmos, not SQL - see the API endpoint for why that matters. */
export interface SearchTermResponse {
  searchTerm: string;
  postingCount: number;
  lastScrapeDate: string | null;
  updatedAtUtc: string | null;
}

export interface MetricsSummary {
  searchTerm: string;
  lastScrapedAtUtc: string | null;
  lastIngestedAtUtc: string | null;
  lastScrapeDate: string | null;
  postingsInLastRun: number;
  newInLastRun: number;
  updatedInLastRun: number;
  invalidInLastRun: number;
  cumulativePostings: number;
  newPostingsDelta: number | null;
  remoteShare: number;
  salaryCoverage: number;
  medianAgeDays: number | null;
  bySite: Record<string, number>;
  topCompanies: NamedCount[];
  titleKeywords: NamedCount[];
  /** What the postings actually ask for, as opposed to what the scraper delivered. */
  enrichment: EnrichmentBreakdown;
  daysOfHistory: number;
}

export interface EnrichmentBreakdown {
  bySeniority: Record<string, number>;
  byWorkArrangement: Record<string, number>;
  byRoleFamily: Record<string, number>;
  topConcepts: NamedCount[];
  /** The same demand rolled up through the closure - the shape under the scatter. */
  topDomains: NamedCount[];
  /** Share with a salary once descriptions have been read. */
  salaryCoverage: number;
  /** Of those, the share that came from prose rather than a salary field. */
  salaryFromTextShare: number;
  medianAnnualSalary: number | null;
  /** Surface forms seen and not resolved - the size of the vocabulary's blind spot. */
  unresolvedMentions: number;
  /** And what is actually in it, most frequent first - the only actionable part. */
  topUnresolved: UnresolvedCount[];
}

export interface UnresolvedCount {
  form: string;
  /** 'Ambiguous' needs context; 'UnknownBoardSkill' / 'UnknownModelSkill' need vocabulary. */
  reason: string;
  count: number;
}

export interface DailyRollup {
  id: string;
  type: string;
  searchTerm: string;
  date: string;
  updatedAtUtc: string;
  runsIngested: number;
  postingsSeen: number;
  newPostings: number;
  cumulativePostings: number;
  bySite: Record<string, number>;
  remoteShare: number;
  salaryCoverage: number;
  topCompanies: NamedCount[];
}

export interface FieldFill {
  field: string;
  fillRate: number;
}

export interface ScraperHealth {
  searchTerm: string;
  lastScrapedAtUtc: string | null;
  status: 'healthy' | 'degraded' | 'unknown';
  emptyColumns: string[];
  sparseColumns: FieldFill[];
  fieldFillRates: Record<string, number>;
  rowsInLastRun: number;
  invalidInLastRun: number;
  bySite: Record<string, number>;
}

export interface RunResponse {
  id: number;
  blobPath: string;
  blobSizeBytes: number;
  searchTerm: string;
  scrapedAtUtc: string;
  ingestedAtUtc: string;
  scrapeDate: string;
  rowCount: number;
  parsedCount: number;
  invalidCount: number;
  newCount: number;
  updatedCount: number;
  unchangedCount: number;
}

export interface MeResponse {
  name: string | null;
  isAuthenticated: boolean;
  objectId: string | null;
  tenantId: string | null;
  scopes: string[];
}
