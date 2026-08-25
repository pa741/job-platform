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
  /** Verbatim applicant caption, e.g. "Over 200 applicants". LinkedIn only. */
  applicants: string | null;

  /**
   * The figure parsed out of `applicants`. The competition signal.
   *
   * Sparse — LinkedIn is the only board that publishes it — so null means "not stated",
   * never zero.
   */
  applicantCount: number | null;

  /** Openings this listing covers, where the board says. Naukri and freehire. */
  vacancyCount: number | null;

  /**
   * The board's own three-way work mode.
   *
   * Worth showing beside the derived `workArrangement`: this is what the employer stated and
   * that is what we concluded. Where they disagree, the disagreement is the story.
   */
  workFromHomeType: string | null;

  listingType: string | null;

  /** `inside`, `outside`, or null. UK contract postings only. */
  ir35: string | null;

  /** Null where the posting is silent, which is not the same as "no". */
  visaSponsorship: boolean | null;

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

// ---------------------------------------------------------------------------
// Profile, matching and generated applications.
//
// Every one of these is per-principal: the API resolves whose data to return from the token's
// `oid` claim, and none of these calls carries an identifier for whose profile it wants. There
// is deliberately no way for the client to ask for somebody else's.
// ---------------------------------------------------------------------------

/** `Unknown`, `OnSite`, `Hybrid` or `Remote`. Unknown means no preference, not on-site. */
export type WorkArrangementName = 'Unknown' | 'OnSite' | 'Hybrid' | 'Remote';

/** The supply half of the assertion polarity. The demand half is not valid here. */
export type SkillLevel = 'Familiar' | 'Proficient' | 'Expert';

export interface ProfileExperience {
  company: string;
  title: string;
  startDate: string | null;
  /** Null means current. */
  endDate: string | null;
  locationCity: string | null;
  locationCountry: string | null;
  description: string | null;
}

export interface ProfileEducation {
  institution: string;
  qualification: string;
  fieldOfStudy: string | null;
  startDate: string | null;
  endDate: string | null;
  grade: string | null;
  description: string | null;
}

export interface ProfileProject {
  name: string;
  description: string | null;
  url: string | null;
  completedOn: string | null;
}

export interface ProfileCertification {
  name: string;
  issuer: string | null;
  year: number | null;
}

export interface ProfileLanguage {
  name: string;
  level: string | null;
}

export interface ProfileLink {
  label: string;
  url: string;
}

/** A skill claimed outright, keyed against the shared concept vocabulary. */
export interface DeclaredSkill {
  conceptKey: string;
  level: SkillLevel | null;
  years: number | null;
}

/**
 * The profile form.
 *
 * No subject id. The API takes it from the token, which is what stops a request body naming
 * somebody else's directory object id from writing into their profile.
 */
export interface ProfileRequest {
  fullName: string | null;
  headline: string | null;
  email: string | null;
  phone: string | null;
  summary: string | null;
  locationCity: string | null;
  locationCountry: string | null;
  willingToRelocate: boolean;
  preferredArrangement: WorkArrangementName | null;
  maxDaysInOffice: number | null;
  minimumSalary: number | null;
  salaryCurrency: string | null;
  jobTypes: string[];
  yearsExperience: number | null;
  seniority: string | null;
  experiences: ProfileExperience[];
  education: ProfileEducation[];
  projects: ProfileProject[];
  certifications: ProfileCertification[];
  languages: ProfileLanguage[];
  links: ProfileLink[];
  declaredSkills: DeclaredSkill[];
}

/** A concept the model read out of the candidate's own prose. */
export interface ExtractedSkill {
  conceptKey: string;
  label: string;
  level: string;
  years: number | null;
  /** The phrase it was read from. What makes an inference checkable by the candidate. */
  evidence: string | null;
}

export interface ProfileResponse extends ProfileRequest {
  updatedUtc: string | null;
  /**
   * What was inferred, kept apart from what was declared. Merging the two would hide which is
   * which from the person they are about.
   */
  extractedSkills: ExtractedSkill[];
  extractedAtUtc: string | null;
}

/** `Weak`, `Possible` or `Strong`. Null until the nightly sweep has judged this pair. */
export type CandidacyVerdict = 'Weak' | 'Possible' | 'Strong' | 'Unknown';

export interface MatchSummary {
  postingId: number;
  title: string;
  company: string | null;
  location: string | null;
  annualSalaryMin: number | null;
  annualSalaryMax: number | null;
  annualSalaryCurrency: string | null;
  workArrangement: string;
  seniority: string;
  datePosted: string | null;

  /** 0-100 from the deterministic scorer. Always present. */
  score: number;

  /**
   * How much of a full assessment this posting supported, 0-1.
   *
   * Read next to `score`, never instead of it: a 100 over every axis and a 100 over one are
   * the same number and very different claims. Most real postings land between 0.2 and 0.5,
   * so a low value is normal — it is a low value *with* a high score that needs a caveat.
   */
  coverage: number;
  requiredGapCount: number;

  /** Null until the model has judged this pair. Not the same as a Weak verdict. */
  verdict: CandidacyVerdict | null;
  assessmentScore: number | null;
  rationale: string | null;

  scoredAtUtc: string;
  assessedAtUtc: string | null;
}

export interface MatchComponent {
  /** `requiredSkills`, `seniority`, `salary`… */
  name: string;
  /** 0-1 within this axis. */
  score: number;
  /**
   * Share of the total this axis carried. **Zero means the posting said nothing** and the axis
   * was dropped rather than failed - rendering it as a zero score shows a penalty never applied.
   */
  weight: number;
}

export interface ConceptMatch {
  required: string;
  requiredLabel: string;
  held: string;
  heldLabel: string;
  /** `Exact`, `Specialisation`, `Generalisation`, `Implied`, `Related` or `Superseded`. */
  relation: string;
  credit: number;
  demand: string;
}

export interface ConceptGap {
  concept: string;
  label: string;
  demand: string;
  yearsMin: number | null;
}

export interface MatchDetail extends MatchSummary {
  components: MatchComponent[];
  matched: ConceptMatch[];
  gaps: ConceptGap[];
  strengths: string[];
  assessmentGaps: string[];
  emphasise: string[];
  hasApplication: boolean;
}

export interface ApplicationSummary {
  id: number;
  postingId: number;
  postingTitle: string;
  company: string | null;
  revision: number;
  instructions: string | null;
  model: string | null;
  createdAtUtc: string;
}

export interface ApplicationDetail extends ApplicationSummary {
  curriculumVitaeMarkdown: string;
  coverLetterMarkdown: string;
  emphasised: string[];
}

// ---------------------------------------------------------------------------
// Posting insight: everything the pipeline concluded about one posting, and how.
//
// The provenance is the point. A list of skills is the shallow half; which of them the
// employer tagged, which a string match found, which the model read out of prose — and the
// exact phrase — is what makes a conclusion checkable rather than merely presented.
// ---------------------------------------------------------------------------

/** `Board` (employer's own tagging), `Taxonomy` (string match), `Model` (a judgement). */
export type AssertionSource = 'Board' | 'Taxonomy' | 'Model';

/** Demand half. Only the model pass can produce anything but `Unspecified`. */
export type DemandPolarity = 'Required' | 'Preferred' | 'Mentioned' | 'Unspecified';

export interface Assertion {
  concept: string;
  label: string;
  kind: string;
  source: AssertionSource;
  polarity: DemandPolarity;
  yearsMin: number | null;
  yearsMax: number | null;
  /** The phrase it was read from, verbatim. Null for board tags, which have none. */
  evidence: string | null;
  confidence: number | null;
}

/** A domain reached by walking the closure up from the asserted concepts. */
export interface Rollup {
  concept: string;
  label: string;
  count: number;
}

export interface Mention {
  surfaceForm: string;
  /** `Ambiguous`, `UnknownBoardSkill` or `UnknownModelSkill`. */
  reason: string;
  occurrences: number;
}

export interface PostingTag {
  name: string;
  value: string | null;
}

export interface Attribution {
  searchTerm: string;
  firstSeenUtc: string;
  lastSeenUtc: string;
}

export interface CompanyInfo {
  displayName: string;
  industry: string | null;
  employeesBand: string | null;
  revenue: string | null;
  url: string | null;
}

/** Which passes have run, and at which version. The honest footer. */
export interface Provenance {
  enrichmentVersion: number;
  extractorVersion: number | null;
  model: string | null;
  extractedAtUtc: string | null;
  seenCount: number;
  firstSeenUtc: string;
  lastSeenUtc: string;
}

export interface PostingInsight {
  detail: PostingDetail;
  concepts: Assertion[];
  domains: Rollup[];
  mentions: Mention[];
  tags: PostingTag[];
  jobTypes: string[];
  foundBy: Attribution[];
  company: CompanyInfo | null;
  provenance: Provenance;
}
