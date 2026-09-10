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

  /**
   * Whose application system is at the end of this posting's apply link.
   *
   * `Aggregator` is the value worth acting on: the link leads to another job board rather than
   * to an employer, so following it lands on a second set of search results. The apply loop
   * already skips these, and `MatchQuery.excludeAggregators` hides them here.
   *
   * **Null is not `Unknown`.** `Unknown` is a verdict — there is no address to open — and null
   * means nobody has derived one, which is the state of any posting nothing has rescraped since
   * the column was added. Render the two differently or not at all.
   */
  applyVendor: string | null;

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
  /**
   * The CV, for a draft old enough to have one. Null for everything written since.
   *
   * The CV stopped being written per posting when the library replaced it: it is chosen from the
   * variants the candidate wrote themselves, and `GET /matches/{postingId}/cv` says which one.
   * The field survives so an application made under the old rule stays explicable — "what exactly
   * did we send them" is a question about a document somebody else received — and a page must
   * branch on it rather than render it, because rendering null is a blank panel that reads as a
   * failure.
   */
  curriculumVitaeMarkdown: string | null;
  coverLetterMarkdown: string;
  emphasised: string[];

  /**
   * The free text drafted for this posting — the paragraphs a form's own boxes get.
   *
   * Written to be sent under your name, and until now readable nowhere: the agent surface had
   * them and the page you approve an application on showed the cover letter and stopped.
   * `questionText` is the catalogue's wording rather than any form's, so a form asking "What
   * draws you to us?" is answered by the draft filed under "Why do you want to work at this
   * company?" — the matching is the server's job, not this page's.
   */
  draftedAnswers: DraftedAnswer[];
}

/** One paragraph drafted for one posting, and where it came from. */
export interface DraftedAnswer {
  questionText: string;
  answer: string;

  /**
   * `PostingSpecific` — prose about this employer, written per posting.
   * `StableFact` — the same answer whatever the posting, such as where you heard about the job.
   * `Novel` — a question nothing anticipated, answered by a person.
   */
  category: string;
}

/**
 * Which of the candidate's own CVs goes with one posting, and why.
 *
 * The arithmetic's answer. The pack an agent assembles runs the same selector and can do two
 * things this cannot — put a genuine tie to a model, and park a posting nothing fits — because
 * neither belongs behind a page load. So `Ambiguous` is shown as a tie rather than as a choice.
 */
export interface CvChoice {
  postingId: number;
  outcome: 'Chosen' | 'Ambiguous' | 'NoFit';

  /** The CV that would be sent. Null on every outcome but `Chosen`. */
  chosen: CvChoiceVariant | null;

  /** The variants nothing could separate. Only for `Ambiguous`. */
  tied: CvChoiceVariant[];

  /** What this posting asks for that no CV in the library answers. The brief for the next one. */
  missing: { concept: string; label: string }[];

  rationale: string;

  /**
   * `arithmetic` where the scores chose one, `candidate` where you did, otherwise null.
   *
   * Never `model` from this route: a page load must not spend a model call, so a tie stays a tie
   * here and the pack settles it when an application is assembled.
   */
  decidedBy: string | null;

  /**
   * The CV you picked for this posting yourself, where you picked one.
   *
   * Carried beside `outcome` rather than replacing it, because both matter: this is what will be
   * sent, and the outcome is why the choice was worth making and what the alternatives are.
   */
  chosenByCandidate: CandidateCvChoice | null;

  /** How many CVs were eligible at all. Zero is "write one", not "write a different one". */
  considered: number;
}

/** The CV a person settled on for one posting. */
export interface CandidateCvChoice {
  variantId: number;
  label: string;
  /** When it was chosen. Null on a build that stored no timestamp. */
  atUtc: string | null;

  /**
   * Whether it could still go out. **False is the case worth rendering**: a chosen CV that has
   * been archived, or re-authored without being re-rendered, has no file behind it any more —
   * and a page that quietly dropped the choice would leave somebody believing this was settled.
   */
  isSendable: boolean;

  /** What it scored against this advert, where it was scored at all. */
  score: number | null;
}

export interface CvChoiceVariant {
  variantId: number;
  label: string;
  score: number;
  answered: number;
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

// ---------------------------------------------------------------------------
// The CV library: markdown variants the candidate wrote, and the CVs they have not.
//
// **Nothing in these shapes is written by a model, and there is no field through which one
// could be.** The CV stopped being generated per posting because a writer, asked what else an
// employer should know, answered with the candidate's citizenship and added "I am an AI and
// they should have seen this" - stored, served through the pack, one form submission from a
// real employer. So `markdown` arrives from a textarea and from nowhere else, and there is no
// `source`, no `instructions` and no `regenerate` anywhere below. That absence is the feature.
//
// **A variant's concepts are not here either, and their absence is also load-bearing.** They
// are extracted for selection and stored against the variant; they must never reach
// `ProfileConcepts`, never move a match score, and never widen what the candidate is judged to
// have. A client that could read them is a client that could show them beside the profile's
// own, which is the first half of somebody deciding the two lists ought to be merged.
// ---------------------------------------------------------------------------

/**
 * One CV in the library, as a row on the page it is kept on.
 *
 * No markdown: it is `nvarchar(max)`, archived variants are kept forever, and a library with
 * thirty retired CVs would answer the list route with half a megabyte of documents nobody
 * asked to read. `CvVariantDetail` is what the editor opens.
 *
 * The three booleans below are false for three different reasons, and only the server decides
 * them - they are the stored variant's own reading, so the badge on a row and the sentence
 * above the rows are one rule read twice rather than two spellings of it.
 */
export interface CvVariantSummary {
  /** The row. What a submission records, and what a rename cannot change. */
  variantId: number;

  /** What the person calls it. Unique among the CVs in use, reusable once one is archived. */
  label: string;

  /** When the words last became what they are now. Untouched by a rename or an archive. */
  authoredAtUtc: string;

  /** When the stored files were produced, or null while this is still only text. */
  renderedAtUtc: string | null;

  /** Over the rendered PDF's bytes: the answer to "what exactly did we send them". */
  sha256: string | null;

  /** Retired from selection, and from nothing else. There is no delete. */
  isArchived: boolean;

  /** Whether the stored files are the current words rendered. False means "needs rendering". */
  isRenderCurrent: boolean;

  /**
   * Whether a pass may choose this for an application being made now.
   *
   * Archived or not currently rendered, and deliberately *not* staleness: a CV written before
   * this morning's profile edit is still a CV worth sending, and excluding it would leave
   * somebody who added a job at lunchtime with every posting parked while holding six good
   * documents.
   */
  isSendable: boolean;

  /**
   * Whether this row is one the staleness sentence counted.
   *
   * Never true for an archived variant, because the count skips those before it asks anything
   * else. A page flagging rows the sentence above them had not counted would leave a reader
   * deciding which of the two to believe, which is worse than saying nothing.
   */
  isStale: boolean;
}

/** One CV with the candidate's own words - the only thing this system will not rewrite. */
export interface CvVariantDetail extends CvVariantSummary {
  /**
   * Their CV, as stored rather than as submitted.
   *
   * The server trims the ends, so an editor redisplaying its own request body instead would
   * show a document differing from the one on disk by whitespace nobody can see.
   */
  markdown: string;
}

/**
 * How much of the library has fallen behind the profile it was written from.
 *
 * Counts and no verb. There is deliberately no suggested action: a summary carrying an
 * instruction is how "regenerate them for me" arrives six months later as a helpful
 * automation, and the correct response to a stale CV is a person reading it.
 */
export interface CvLibraryStaleness {
  /** How many were counted. Archived variants never reach it. */
  considered: number;

  /** How many predate the profile's last change. The number in the sentence. */
  stale: number;

  /** How many are still level with it. */
  current: number;

  /** Whether there is anything to say at all. */
  anyStale: boolean;

  /**
   * What they were compared against, or null where the profile has never recorded a change.
   *
   * Carried so that "nothing is out of date" can be told apart from "there is nothing to be
   * out of date against". Both count zero, for opposite reasons, and a page that cannot
   * separate them says nothing on the day the feature ships and says nothing on the day it
   * matters.
   */
  profileUpdatedUtc: string | null;
}

/**
 * How much room is left, so a refusal at the cap is not the first time anybody hears of it.
 *
 * The numbers, not a permission: the server enforces the cap and is the only thing that does,
 * so `hasRoomForAnother` was true when the page loaded and is not authority. What it is for is
 * a button that explains itself before it is pressed.
 */
export interface CvLibraryCapacity {
  /** How many variants are in use. Archived rows are not counted, because the cap does not. */
  inUse: number;

  /** The cap. Six, and the number is about how many documents one person keeps current. */
  cap: number;

  /** Whether another may be written, as the library stood when this was read. */
  hasRoomForAnother: boolean;
}

/**
 * The whole library page in one answer: the rows, the nudge and the room.
 *
 * One response rather than three, because they are one read - and because three answers from
 * three moments is exactly how a summary sentence ends up disagreeing with the rows under it.
 */
export interface CvLibraryResponse {
  /** Live variants first, then archived, each in authoring order. Never a ranking. */
  items: CvVariantSummary[];
  staleness: CvLibraryStaleness;
  capacity: CvLibraryCapacity;
}

/** One concept a gap is made of, with the weight it carries inside that gap. */
export interface CvGapConcept {
  /** The concept key, as the vocabulary spells it: `skill.kubernetes`. Identity, not prose. */
  key: string;

  /** The preferred name from the same vocabulary. What a sentence says. */
  label: string;

  /** How many of the gap's own postings ask for this one. */
  postings: number;
}

/**
 * One CV worth writing, named by the concepts it would have to speak to.
 *
 * A cluster rather than a concept, because concepts do not arrive alone: Kubernetes and
 * Terraform missing together across nine postings is one afternoon and one document, and
 * reported as two rows it reads as two - the second worth nothing once the first is written.
 */
export interface CvGap {
  /** What the CV has to cover, heaviest first - so reading them in order names the gap. */
  concepts: CvGapConcept[];

  /**
   * Applyable postings this gap blocks, after the gaps ranked above it have taken theirs.
   *
   * The business case, and greedy on purpose: ranked independently, one set of nine postings
   * wanting two things would report two gaps of nine and read as eighteen postings of payoff.
   */
  postings: number;
}

/**
 * What to write next, and why - the whole of an abstention that says more than "no".
 *
 * Aggregate by construction. There is no per-posting version and no limit to raise, because
 * fifty "could not apply" notices is a queue nobody reads and one ranked list of three is a
 * Saturday afternoon with an obvious payoff.
 */
export interface CvGapBriefResponse {
  /**
   * Every applyable posting currently blocked for want of a CV, counted once each.
   *
   * Deliberately larger than the gaps add up to: the remainder is gaps under the floor, gaps
   * past the third, and postings whose requirements the vocabulary cannot name.
   */
  blockedPostings: number;

  /**
   * How many of those postings named something a CV could actually be written about.
   *
   * **This is what tells two empty gap lists apart, and they mean opposite things.** Zero says
   * the blocked postings are held up by generic tags or keys the vocabulary does not carry -
   * a fault in this system rather than a document anybody can write. Greater than zero says
   * the requirements were nameable but too scattered for one CV to unblock more than one
   * posting, which is ordinary and is the candidate's judgement to make.
   */
  nameablePostings: number;

  /** The CVs to write, best first. */
  gaps: CvGap[];

  /**
   * How many blocked postings a gap needs before it is listed.
   *
   * Stated rather than assumed, because a client that does not know it reads a gap blocking
   * one posting being absent as a bug rather than as the floor doing its job.
   */
  minimumPostingsPerGap: number;

  /** How many gaps a brief will name. Stated for the same reason as the floor. */
  maxGaps: number;
}

/**
 * A new CV, in the candidate's own words.
 *
 * No bounds are restated here: the server is the only thing that validates, and a second copy
 * of a number that has already drifted from a column width once in this codebase is how a save
 * turns into a 500 with somebody's document lost.
 */
export interface CreateCvVariantRequest {
  label: string;
  markdown: string;
}

/**
 * A new name for a CV, and nothing else about it.
 *
 * Separate from the reauthor request because the two writes must not share a path: a rename
 * has to leave `authoredAtUtc` alone, or a document nobody edited looks freshly written and
 * last week's PDF is quietly marked current.
 */
export interface RenameCvVariantRequest {
  label: string;
}

/**
 * Replacement words, dated as of now.
 *
 * Saving without changing a word is still an edit, and that is the point rather than an
 * accident: somebody who reads a CV this page flagged as stale, decides it is still accurate
 * and presses save has answered the nudge.
 */
export interface ReauthorCvVariantRequest {
  markdown: string;
}

/** Whether a CV is retired from selection. One request with a direction, and no delete. */
export interface ArchiveCvVariantRequest {
  archived: boolean;
}

// --- pipeline settings ------------------------------------------------------
//
// The nine levers one candidate may set on their own pipeline, mirroring
// `JobPlatform.Core.Settings.PipelineSettings` field for field. Per-principal like the profile
// and the searches, and scoped the same way: the API resolves whose settings these are from the
// token's `oid` claim, and there is deliberately no field in either shape below that could name
// somebody else's pipeline.
//
// Which control renders on which page, and why they are split across two, is on
// `PipelineSettingsRequest` below rather than here. A `//` banner is read by somebody already
// in this file; a JSDoc block is what an editor shows the person building the form, and that
// note is aimed at them.

/**
 * The nine levers, as a save sends them.
 *
 * **Where each control renders, and why the split is not decoration.** The seven judgement and
 * drafting levers belong on the Pipeline page under System, beside Searches and Model calls;
 * `dailySendCap` and `chaseAfterDays` belong on the Applications page. What separates them is
 * *when a saved value takes effect*, and that is the only line worth drawing here:
 *
 * - The seven change what the **next nightly pass buys**. The match sweep runs at 03:30 UTC and
 *   the writing pass at 04:30 UTC, so a value saved at noon changes nothing anybody can see
 *   until the following morning. Grouping those beside a read-time filter is a trap rather than
 *   an untidiness: the shortlist's `minScore` (see `MatchQuery.minScore`) is a number on 0-100
 *   that re-renders the list as it is dragged, and `assessmentThreshold` is a number on 0-100
 *   that looks exactly like it and answers fifteen hours later. Somebody who cannot tell those
 *   apart concludes the control is broken, and the next thing they do is change it again - so
 *   the two never share a page, and the page that carries these says when its values land.
 * - `dailySendCap` and `chaseAfterDays` take effect **on the next write and the next read**.
 *   The cap is enforced in the submission repository the instant the next `Submitted` event
 *   arrives; staleness is a fold over the event log rather than a stored column, so lowering
 *   the chase window makes quiet applications stale on the very next render, with nothing
 *   migrated and nothing to migrate. Both are about the list they would sit beside, and a
 *   control whose effect is visible on the page carrying it is one nobody has to be told about.
 *
 * System is also where the honest framing of the whole record belongs: these are levers on what
 * the pipeline *buys*, and not one of them changes what a match means. Neither of the two
 * thresholds that are **not** settings - `MatchRanker.FusionFloor` and
 * `CvVariantSelector.SelectionFloor` - appears in these shapes, and neither may ever appear on
 * a form built from them. Both are reasoned from measurements that a number somebody typed
 * cannot be held to.
 *
 * **Every field is required, and that is a guard rather than ceremony.** The PUT replaces the
 * stored row, and the C# record fills an absent property from its own initialiser - so a body
 * that omits `chaseAfterDays` does not leave it as it was, it resets it to the shipped
 * fourteen. That is exactly right for an override built on purpose, which is what
 * `PipelineSettings.Default with { ... }` spells, and exactly wrong for a form that dropped a
 * field on the way to `JSON.stringify`. Both are the same bytes on the wire, so the compiler is
 * the last place they can still be told apart, and optional properties here would spend that.
 *
 * The bounds are deliberately not restated in this file. The server is the only thing that
 * validates, and a second copy of a number here is a second thing to forget when
 * `PipelineSettingsValidation` moves - the same reason `CreateCvVariantRequest` carries none
 * either. What a form needs beyond the bounds is what a refusal says, and a refusal says all of
 * it at once.
 */
export interface PipelineSettingsRequest {
  // Judgement. Consumed by the nightly match sweep - 03:30 UTC.

  /**
   * How many postings the model judges per night. 0-200, default 40.
   *
   * The number that is a bill. Zero is a real value and it is the off switch: the sweep's
   * scoring pass needs no model at all and still writes ranked matches, so zero here keeps the
   * shortlist and stops buying verdicts.
   */
  assessmentsPerNight: number;

  /**
   * The **deterministic match score** a pair must clear before a judgement is bought. 0-100,
   * default 45.
   *
   * Low by design: the arithmetic under-scores a candidate whose relevant experience is in
   * prose the extractor read cautiously, and the model exists to catch exactly that. Zero does
   * not mean "judge everything" - it means every scored pair is a candidate and
   * `assessmentsPerNight` alone decides how many are drawn.
   *
   * Not the same quantity as `draftMinAssessmentScore`, which reads the model's score. Two
   * numbers on 0-100 measuring different things, which is why the ordering between them is a
   * stated rule the server enforces rather than something a form may treat as obvious.
   */
  assessmentThreshold: number;

  /**
   * The percentage of each night's shortlist reserved for postings inside `recentWindowDays`.
   * 0-100, default 67.
   *
   * A reservation and never an ordering: the recent draw is sorted by score like every other
   * draw, and whatever the reservation cannot fill goes back to the top-down draw over the
   * whole corpus, so a quiet day costs nothing and a backlog still drains. Zero is a real value
   * - no reservation, pure top-down - and 100 is legal and stops the backlog draining at all on
   * any day with enough arrivals to fill the shortlist.
   *
   * A percent rather than the numerator and denominator it replaces, because a settings form
   * cannot sensibly ask for two fields that must be read together and will therefore be
   * half-edited.
   */
  recentSharePercent: number;

  /**
   * What that reservation counts as "recent", in days. 1-30, default 3.
   *
   * **This is the sweep's own reservation window and not the system-wide age rule.** Every age
   * *filter* in the product - the shortlist's, the corpus search's, the apply queue's - answers
   * to `PostingAge.DailyWindowDays`, which this does not replace, is not coupled to, and must
   * never be labelled as. The default merely equals it, because that is what the sweep passes
   * today and an unconfigured deployment has to be unchanged.
   *
   * Floor 1 rather than 0: a window of zero would leave the reservation unfilled every night
   * and falling back to the top-down draw, which is indistinguishable from the feature having
   * been switched off. The control that actually switches it off is `recentSharePercent` at
   * zero, and a person should have to type that one.
   */
  recentWindowDays: number;

  // Drafting. Consumed by the nightly application generation pass - 04:30 UTC.

  /**
   * How many drafts one nightly pass writes. 0-25, default 10.
   *
   * The other bill, and the expensive one: these calls go to the writing deployment priced
   * roughly twenty-five times the bulk one. Its effective ceiling is the lower of 25 and this
   * candidate's own `dailySendCap` - a night that writes more letters than a day can send is
   * buying prose for adverts nobody will reach - and the server refuses that combination rather
   * than clamping it, because a setting that is silently clamped is a setting that lies to the
   * person who typed it. Zero switches the pass off.
   */
  draftsPerNight: number;

  /**
   * The **model's assessment score** a posting must carry before a document is written for it.
   * 0-100, default 80.
   *
   * It has to be able to equal the floor the unattended run pulls with: set above it and the
   * queue offers postings whose documents were never written, set below it and the pass buys
   * drafts nothing will look at.
   *
   * Must be at least `assessmentThreshold`, which the server enforces and which is not the
   * arithmetic it looks like - the two read different columns. A posting below the threshold is
   * never sent to the assessor, so it carries no assessment score for this floor to read, and a
   * floor set beneath the threshold widens the drafting band by nothing at all.
   *
   * Zero is not "no floor": a pair the model scored no number for clears no floor at any value,
   * so zero means "anything actually judged".
   */
  draftMinAssessmentScore: number;

  /**
   * Only draft for postings posted within this many days, or `null` for postings of every age.
   * 1-90 when set. Default `null`.
   *
   * **The only nullable lever, and `null` is not `0`.** Null means "no age bound"; zero would
   * mean "posted since this instant" through `PostingAge.Cutoff`, selects almost nothing, reads
   * as the writing pass being broken, and is refused. So a cleared box must serialise to `null`
   * - never to `0`, and never by being omitted. `Number('')` is `0`, which is precisely the one
   * transcription that turns an empty input into a refused save, and the reason this is
   * `number | null` rather than an optional property.
   *
   * The same rule the scraper config already runs under: "did not choose" and "chose nothing"
   * have to be different bytes on the wire.
   */
  draftPostedWithinDays: number | null;

  // Sending. Consumed by the submission write path and by the fold over the event log.

  /**
   * How many applications may be recorded as *sent* in one UTC day. 0-100, default 25.
   *
   * A bound on the blast radius of a client that loops, set well above what a person does in a
   * day and well below what a loop does in a minute. It bounds `Submitted` events alone -
   * recording that a hundred applications exist is fine, claiming a hundred were sent today is
   * not - and it is enforced in the submission repository, which is the only thing that
   * enforces it. Zero pauses the loop.
   *
   * Also the ceiling on `draftsPerNight`, which is why the two are validated together even
   * though they render on different pages.
   */
  dailySendCap: number;

  /**
   * Silence for this many days makes an application stale. 1-365, default 14.
   *
   * A day count rather than a duration, deliberately: a `TimeSpan` is a shape a form cannot
   * render and the wire spells several ways - `"14.00:00:00"`, `"P14D"`, `1209600000` - so it
   * would be three contracts wearing one type. The fold keeps its `TimeSpan` and builds it from
   * this number.
   *
   * Changing it re-reads history rather than rewriting it: staleness is derived and never
   * stored, so lowering this makes older quiet applications stale immediately and raising it
   * makes them live again, with nothing migrated. A closed application is never stale at any
   * value - an employer who stopped replying has gone quiet, one who said no has not.
   */
  chaseAfterDays: number;
}

/**
 * The nine levers as they will run tonight, with one fact a request has no use for.
 *
 * Extends the request rather than restating it, the same way `ScraperSearchResponse` extends
 * `ScraperSearchRequest`: nine numbers written out twice is nine chances for a rename to reach
 * one copy and not the other.
 */
export interface PipelineSettingsResponse extends PipelineSettingsRequest {
  /**
   * When these were last saved, or `null` where nothing has ever been stored.
   *
   * **The whole of the "has anybody configured this" answer, and one field rather than two on
   * purpose** - a boolean beside a timestamp is two things that have to agree, and a page would
   * have to pick one to believe on the day they do not.
   *
   * Null does not mean the read failed and it does not mean the nine values are missing: an
   * unconfigured candidate runs on `PipelineSettings.Default`, whose every value is the
   * constant the shipped code already used, so the API answers all nine numbers and this
   * timestamp says only whether anybody chose them. That distinction is what lets a page say
   * "these are the shipped defaults" instead of showing an empty form - and it is why this
   * route has no 404, unlike the profile.
   */
  updatedUtc: string | null;
}

/**
 * How a refused save arrives: an RFC 9457 problem document, and never a field on a success.
 *
 * This is the shape the searches routes already answer a bad save with - `SearchEndpoints`
 * joins everything `ScraperSearchValidation.Validate` returned into one `detail` and answers
 * 400 - and the settings routes follow it rather than inventing a second spelling of failure.
 * `JobPlatformApi.request` parses this into an `ApiError` carrying `status` and `detail`, and
 * `ErrorNote` already renders that pair, so a page written against this contract writes no
 * error handling of its own.
 *
 * **There is deliberately no success-shaped `{ ok, problems[] }` union.** A client with two
 * ways for a save to fail is a client where every caller has to handle both and half of them
 * handle one - and the half that gets missed is the one that fails silently, because a rejected
 * save that *resolves* looks exactly like a successful one to a `.then`.
 *
 * `detail` carries **every** problem rather than the first, because a form with four bad fields
 * should say so once rather than over four saves. It is one string with the problems joined by
 * a space, exactly as the searches routes send it. Splitting it back into a list here would be
 * this client parsing prose, which is how the full stop inside a message becomes two problems.
 *
 * Named for this route because this is the first place the client needed to describe the shape.
 * It is the API's general problem document, so a second route that needs it should widen this
 * name rather than add a second copy of it.
 */
export interface PipelineSettingsProblem {
  /** 400 for a value out of bounds or a broken cross-field rule; 401 for a token with no `oid`. */
  status: number;

  /** The status phrase the server filled in - "Bad Request". Rarely the useful half. */
  title?: string;

  /** Every problem, joined into one string. The half worth showing somebody. */
  detail?: string;
}
