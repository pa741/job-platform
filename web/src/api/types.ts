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

  /**
   * How many people the board says have applied. The competition signal.
   *
   * On the list rather than only on the detail: it is a number you sort a shortlist by, and
   * a field only the detail endpoint carries can never be one. Sparse — LinkedIn is the only
   * board that publishes it — so null means "not stated", never zero.
   */
  applicantCount: number | null;

  firstSeenUtc: string;
  lastSeenUtc: string;
  seenCount: number;
  /** Every search that turned this posting up - it can match more than one. */
  searchTerms: string[];
}

export interface PostingDetail {
  /**
   * Verbatim applicant caption, e.g. "Over 200 applicants". LinkedIn only.
   *
   * The parsed figure is `summary.applicantCount`. "Over 200" and "200" are not the same
   * statement, and only the caption says which one the board actually made.
   */
  applicants: string | null;

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

/**
 * A column empty in every row of the last run, and when it was last populated.
 *
 * Two faults present the same symptom and need opposite responses. `lastFilledUtc` set means
 * the column was arriving and stopped — a board changed its markup. Null means no run within
 * the history window had it populated, which is a column the scraper does not emit yet, and
 * is not something that broke.
 */
export interface EmptyColumn {
  field: string;
  lastFilledUtc: string | null;
  lastFillRate: number | null;
}

export interface ScraperHealth {
  searchTerm: string;
  lastScrapedAtUtc: string | null;
  status: 'healthy' | 'degraded' | 'unknown';
  emptyColumns: EmptyColumn[];
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

  /**
   * Cosine of your profile against this advert, or null where either side has no vector yet.
   *
   * Not a percentage, and not on a 0-1 scale in practice: for one profile the whole corpus sits
   * in a band roughly 0.15 wide, so the absolute value says very little and the position within
   * the band says everything. Show it as a comparison between rows or not at all.
   */
  similarity: number | null;

  /**
   * What the list is ordered by, 0-100. **An ordering key, not a score — do not display it.**
   *
   * A convex combination of `score` and `similarity`, normalised over this candidate's whole
   * pool, so it is not comparable between candidates or between nights. It is here so a client
   * can re-sort without a second request, not so it can be put on screen beside the score where
   * the two would read as the same kind of number.
   */
  rankScore: number;

  scoredAtUtc: string;
  assessedAtUtc: string | null;

  /**
   * When the candidate said they were not interested. Null means they have not.
   *
   * Present on every row although the default list returns only undismissed ones: the
   * dismissed pile is the same shape read with `dismissed=true`, and a client showing it
   * needs to say when each was set aside.
   */
  dismissedAtUtc: string | null;
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

/**
 * One application the candidate sent, with its status folded from the event log.
 *
 * There is no stored status behind this: the server folds the events on every read, which is
 * why `isStale` can be trusted and why `phase` is null rather than a `Created` name where
 * nothing has happened yet. "Not started" and "started and we cannot say" are different facts.
 */
export interface Submission {
  id: number;
  postingId: number;
  postingTitle: string;
  company: string | null;

  /** `Ats` or `Board` — the employer's own system, or the job board's. */
  channel: string;

  /** Where the application went, as it stood when it was recorded. */
  applyUrl: string | null;

  createdAtUtc: string;

  /** The furthest phase reached. Null until the first event. */
  phase: string | null;

  /** The label inside the phase — "Tech round 2". Free text. */
  stage: string | null;

  lastActivityUtc: string;

  /** Nothing for a fortnight. Derived on read, never stored. */
  isStale: boolean;

  /** Rejected or withdrawn. A closed application is never stale. */
  isClosed: boolean;

  eventCount: number;
}

/** One thing that happened, as the log returns it. */
export interface SubmissionEvent {
  atUtc: string;
  type: string;
  stage: string | null;

  /** `Candidate`, `Client` or `Email` — who asserted it. */
  source: string;

  note: string | null;
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

// ---------------------------------------------------------------------------
// The concept vocabulary, and where the corpus's knowledge comes from.
// ---------------------------------------------------------------------------

export interface ConceptListItem {
  concept: string;
  label: string;
  /** `Domain`, `Skill` or `Qualification`. Domains are never asserted directly. */
  kind: string;
}

/** `Broader`, `Narrower`, `Implies`, `ImpliedBy`, `Related`, `SucceededBy`, `Succeeds`, `VariantOf`. */
export type ConceptRelation =
  | 'Broader' | 'Narrower' | 'Implies' | 'ImpliedBy'
  | 'Related' | 'SucceededBy' | 'Succeeds' | 'VariantOf';

export interface ConceptEdge {
  concept: string;
  label: string;
  kind: string;
  relation: ConceptRelation;
  /** Distinct postings asserting the concept at the other end. Zero is a real answer. */
  demand: number | null;
}

export interface ConceptLabel {
  label: string;
  /** `Preferred`, `Alternate`, or `Ambiguous` — names the concept but cannot be trusted to mean it. */
  kind: string;
}

export interface ConceptAncestor {
  concept: string;
  label: string;
  depth: number;
}

export interface ConceptDetail {
  concept: string;
  label: string;
  kind: string;
  labels: ConceptLabel[];
  demand: number;
  edges: ConceptEdge[];
  /** The closure. What makes a domain rollup possible, and what the match scorer walks. */
  ancestors: ConceptAncestor[];
}

export interface PolarityCount {
  polarity: string;
  assertions: number;
}

export interface SourceBreakdown {
  /** `Board`, `Taxonomy` or `Model`, in descending order of trust. */
  source: string;
  assertions: number;
  postings: number;
  polarities: PolarityCount[];
}

export interface SourceComposition {
  searchTerm: string | null;
  sources: SourceBreakdown[];
  totalAssertions: number;
  /**
   * Share of assertions carrying a strength rather than `Unspecified`, 0-1.
   *
   * The headline of the view. Near zero means the model pass has not run, and every match is
   * therefore weighing "mentioned once in passing" the same as "must have".
   */
  gradedShare: number;
}

/**
 * One model call, as the ledger records it.
 *
 * There is deliberately no prompt or response field. The prompts carry the candidate's
 * employment history, and the API has no field for them - this type reflects that rather
 * than trimming it client-side.
 */
export interface AiCallResponse {
  occurredAtUtc: string;
  operation: string;
  deployment: string | null;
  /** `Succeeded`, `PartiallyDiscarded` or `Failed`. */
  outcome: string;
  requested: number;
  returned: number;
  /** Paid for and thrown away. The number that used to be invisible. */
  discarded: number;
  durationMs: number;
  inputTokens: number;
  outputTokens: number;
  /** Of the output, how many the model spent thinking. Zero on a non-reasoning model. */
  reasoningTokens: number;
  /** Zero means the provider reported nothing, which is not the same as free. */
  totalTokens: number;
  reason: string | null;
  affectedIds: number[];
}

export interface AiCallTotalsResponse {
  operation: string;
  calls: number;
  failedCalls: number;
  requested: number;
  returned: number;
  discarded: number;
  totalTokens: number;
  reasoningTokens: number;
}

// --- scraper searches -------------------------------------------------------

/**
 * One configured search, as the form submits it.
 *
 * Every field is named, and there is deliberately no free-form parameter map: the scraper
 * ends up calling `scrape_jobs(**params)`, and a client that could name a keyword argument
 * could reach the ones carrying proxies and API keys. The API builds those names from these
 * typed fields and nowhere else.
 *
 * `slug` is absent for the same reason: it is an identity the platform assigns.
 */
export interface ScraperSearchRequest {
  name: string;
  enabled: boolean;
  searchTerm: string;
  /** Wire names from `GET /searches/options`, e.g. `['indeed', 'linkedin']`. */
  sites: string[];
  location: string | null;
  countryIndeed: string | null;
  /** `null` is "no preference", which is not the same as `false`. */
  isRemote: boolean | null;
  hoursOld: number | null;
  resultsWanted: number | null;
  jobType: string | null;
  freehireFilters: Record<string, string>;
}

export interface ScraperSearchResponse extends ScraperSearchRequest {
  /**
   * The identity. It is what the scraper writes into the blob name, so it is also what the
   * search-term picker and every metric partition call this search - shown rather than hidden,
   * or the two views cannot be reconciled by the person looking at them.
   */
  slug: string;
  createdUtc: string;
  updatedUtc: string;
}

export interface ScraperSearchListResponse {
  searches: ScraperSearchResponse[];
  /** Whether the scraper's configuration was successfully written. */
  published: boolean;
  /** When it was last written. Null means never, or the last attempt failed. */
  publishedUtc: string | null;
}

/** The vocabulary the form offers, served rather than duplicated here. */
export interface ScraperSearchOptionsResponse {
  sites: string[];
  jobTypes: string[];
  freehireFilterKeys: string[];
  maxHoursOld: number;
  maxResultsWanted: number;
}

/**
 * The join, run backwards: what the candidate's matched band asks for that they do not hold.
 *
 * The only figure on the market view that is about the reader rather than about the corpus,
 * and the only one that changes what they would do next. It exists because postings and
 * profiles are extracted into the same vocabulary, which makes this a set difference.
 */
export interface SkillGapResponse {
  /** The score floor the band was taken at, so the numbers are readable. */
  minScore: number;
  searchTerm: string | null;
  items: SkillGapItem[];
}

export interface SkillGapItem {
  concept: string;
  label: string;
  /** `Skill` or `Qualification`. Never `Domain` - nothing is tagged with one directly. */
  kind: string;

  /**
   * Postings among this candidate's matches that name it. The number to rank by.
   *
   * Read instead of `corpusPostings`, not beside it: the corpus figure says what the market
   * wants, this says what the market wants of them, and the concept at the top of the corpus
   * list is invariably one they already hold.
   */
  matchPostings: number;

  /** Postings across the corpus that name it. Context, and always the larger. */
  corpusPostings: number;

  /** The nearest concept the profile does hold, or null where there is none. */
  held: string | null;
  heldLabel: string | null;

  /**
   * How `held` relates to `concept`: `Specialisation`, `Generalisation`, `Implied`, `Related`
   * or `Superseded` - the same decision the match breakdown reports, so the two pages cannot
   * disagree about the same pair. Null means nothing in the profile touches it at all, which
   * is the gap with no partial credit behind it.
   */
  relation: string | null;

  /** What that relation is worth before the candidate's own strength, 0-1. */
  credit: number;
}

// ---------------------------------------------------------------------------
// The question queue: what an unattended run could not answer.
//
// This is the declared half of the system, and it is the only half. `FormFieldCatalog`
// answers a fixed allowlist of questions out of the profile — eleven fields, none of them
// sensitive, and two tests fail the build if a sensitive one is added quietly. Nothing else
// is derivable, so a value in these shapes exists because a person typed it and for no
// other reason. That is a stronger guarantee than a `sensitive: true` flag, and unlike a
// flag it does not depend on having been set correctly.
// ---------------------------------------------------------------------------

/**
 * One question waiting on the candidate, with the advert that raised it.
 *
 * One wording is one row however many adverts asked it — the queue folds typography, so the
 * same question with a curly apostrophe is not asked twice. `postingId` is therefore context
 * rather than identity: it names the advert that hit the wording first, and the other adverts
 * that hit it record their waiting on their own parked applications.
 */
export interface OpenQuestion {
  questionId: number;

  /** The advert that raised it. Null for a question that came from nowhere in particular. */
  postingId: number | null;
  postingTitle: string | null;
  company: string | null;

  /**
   * The employer's row, where the advert names one.
   *
   * Needed to offer the company scope at all: an answer is filed against a company id rather
   * than against the name printed on the advert, because the company table already folds
   * "Contoso" and "Contoso Ltd" into one employer and keying on the string would file the
   * same answer twice. Null means that folding is unavailable here, and the choice is between
   * this advert and everywhere.
   */
  companyId: number | null;

  /** The unattended pass that raised it, so an abandoned run's questions stay attributable. */
  runId: number | null;

  /** The question as the form asked it, verbatim. What a person reads before answering. */
  questionText: string;

  /**
   * The choices the form offered, in the form's own words.
   *
   * Empty covers both a free-text box and a set nobody recorded, deliberately: the form did
   * not answer that question either, and a caller telling them apart would be acting on a
   * distinction that was never established.
   */
  options: string[];

  /**
   * Whether this is one only the candidate may state.
   *
   * Read off the question's own wording as well as off whatever raised it, so a right-to-work
   * or salary question is marked whether or not anything ticked a box. It drives a
   * confirmation here and redaction in the disclosure log — never permission to infer.
   */
  sensitive: boolean;

  askedAtUtc: string;

  /** The application this question is holding back, where one is parked on it. */
  parked: ParkedApplication | null;
}

/**
 * An application put down without being made, waiting on an answer.
 *
 * Parking is an attribute on the submission rather than an event, because the event log folds
 * to the furthest phase reached and "no attempt was made" is not a point on that ladder. A
 * parked row is not a sent one and must never be counted as one.
 */
export interface ParkedApplication {
  submissionId: number;
  postingId: number;
  postingTitle: string;
  company: string | null;
  parkedAtUtc: string;
}

/**
 * How widely a stored answer applies.
 *
 * The narrow scopes are the safety property rather than a filing convenience: a posting-scoped
 * answer is only ever offered back for that posting, so the cost of writing something specific
 * is bounded to the place it was written for. Widening is a deliberate act by the person, never
 * something resolution decides for them.
 */
export type AnswerScope = 'Global' | 'Company' | 'Posting';

/**
 * What the candidate answers, and how far it should carry.
 *
 * <b>No company or posting id travels in this.</b> The scope is a choice; the ids behind it are
 * read server-side from the question's own advert. A body that named its own ids would let a
 * mistyped number file somebody's salary expectation against an employer they never applied to,
 * and there is nothing the server could check it against.
 */
export interface AnswerQuestionRequest {
  /** In the words that would be typed into the form. Stored verbatim, never shortened. */
  value: string;
  scope: AnswerScope;
  /**
   * A canonical key where the question has one, e.g. `notice_period`.
   *
   * The queue does not ask a person to invent one — it is here because the route takes it, the
   * same way the tool surface does, and because the key is the escape from phrasing: the hash
   * folds typography and nothing more, so two employers asking the same thing in genuinely
   * different words are two hashes and one name.
   */
  name?: string | null;
}

/**
 * What answering did, including the half that is otherwise invisible.
 *
 * `returnedToQueue` is the causal link the queue exists for: closing a question takes it out of
 * the unanswered set, which is the same set the applyable predicate reads, so the advert parked
 * on it stops being held. Nothing here sends anything — the next unattended run is what picks
 * the advert up.
 */
export interface AnswerQuestionResponse {
  answerId: number;

  /** False where that exact answer was already stored: nothing written, nothing superseded. */
  created: boolean;

  scope: AnswerScope;
  sensitive: boolean;
  answeredAtUtc: string;

  /** The question this closed. Null where the answer was volunteered rather than asked for. */
  closedQuestionId: number | null;

  /** The applications no longer held back by it. Empty is ordinary, not a failure. */
  returnedToQueue: ParkedApplication[];

  /** An explanatory sentence where something is simply absent. Null where there is nothing to say. */
  note: string | null;
}
