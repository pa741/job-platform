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
  isRemote: boolean;
  jobType: string | null;
  datePosted: string | null;
  minAmount: number | null;
  maxAmount: number | null;
  currency: string | null;
  salaryInterval: string | null;
  jobUrl: string | null;
  /** Length only - the description itself is on the detail endpoint. */
  descriptionLength: number;
  firstSeenUtc: string;
  lastSeenUtc: string;
  seenCount: number;
  searchTerm: string;
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
  daysOfHistory: number;
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

export interface PostingMatch {
  postingId: number;
  score: number;
  rationale: string | null;
  matchedSkills: string[];
  missingSkills: string[];
}

export interface MatchResponse {
  matches: PostingMatch[];
  profile: {
    skills: string[];
    titles: string[];
    locations: string[];
    yearsExperience: number | null;
    prefersRemote: boolean | null;
    tokenCount: number;
  };
  provider: string;
  candidatesConsidered: number;
  degradedToFallback: boolean;
  degradationReason: string | null;
}

export interface MeResponse {
  name: string | null;
  isAuthenticated: boolean;
  objectId: string | null;
  tenantId: string | null;
  scopes: string[];
}
