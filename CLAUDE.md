# CLAUDE.md

Guidance for Claude Code (or any assistant) working in this repository.

## Project overview

The Azure side of a job-market data pipeline. A separate repository
([`job-scrapper`](https://github.com/pa741/job-scrapper)) runs a JobSpy scraper on a NAS
and uploads timestamped CSVs to Blob Storage. This repository ingests them: postings into
Azure SQL, metrics into Cosmos DB, triggered by Event Grid on blob creation.

`../model.md` holds the target architecture for the whole system, and it is binding: amend it
before building something it does not name, not after. Every row is built - Ingestion, Data, the
API, the Frontend, the candidate profile, matching, applications, Realtime, Submissions and the
agent surface - the last of which now writes as well as reads. The Applications and Candidate
profile rows were amended for the CV library *before* it was built, which is the order that
document is binding in. What stays open there is the
questions *channel*: `list_open_questions` is a poll, and pushing a question to the person who can
answer it needs a per-candidate send the broadcast feed does not have. See
[`mcp_handoff.md`](mcp_handoff.md) section 1.4.

## Public repo hygiene

**This repository is public.** It is a portfolio piece, so the bar is absolute rather than
best-effort.

- **Never commit real identifiers.** Subscription ids, tenant ids, object ids and resource
  names belong in environment variables. `infra/main.bicepparam` reads them via
  `readEnvironmentVariable()`; real values go in `infra/main.local.bicepparam`, which is
  gitignored.
- **Never commit scraped data.** Real exports contain populated `emails` and descriptions
  carrying recruiter names and contact details. `*.csv` is gitignored except under
  `tests/**/Fixtures/`, and the fixture there is entirely synthetic.
- **Never introduce a secret.** The architecture has none at all - the one exception it used
  to carry was removed, not merely contained (see below). If a change seems to need a password,
  key or connection secret, that is a signal the design is being worked around; find the
  identity-based path instead.
- **Never commit a real profile.** The profile tables hold somebody's employment history,
  contact details and salary expectations. Nothing under `tests/` may contain a real one, and
  no fixture, screenshot or example in this repository may either.
- `local.settings.json`, `*.PublishSettings` and `*.pubxml` are gitignored because they
  genuinely carry credentials.
- Before pushing, check `git ls-files` for stray CSVs and `git grep` for identifiers.
- `.gitleaks.toml` allowlists the Azure built-in **role definition** GUIDs by value. They are
  public constants, but they look exactly like generic API keys to the scanner. Allowlisting
  by value rather than by path keeps a real secret in the same file detectable.

## Authentication model

There is no password, key or connection secret anywhere, without qualification:

- Azure SQL: `azureADOnlyAuthentication: true`. No SQL login exists. Connection strings use
  `Authentication=Active Directory Default`.
- Cosmos DB: `disableLocalAuth: true`. Access is a data-plane role assignment
  (`sqlRoleAssignments`), not a key.
- Storage: identity-based connections (`__serviceUri` + `__credential=managedidentity`).
  The Functions host account sets `allowSharedKeyAccess: false`.
- **Azure OpenAI: `disableLocalAuth: true`.** Access is a `Cognitive Services OpenAI User`
  role assignment on the account. The keys the resource still mints in the portal will not
  authenticate anything, so a leaked one is inert.
- GitHub Actions: OIDC federated credential pinned to `main`. No client secret.
- Container Apps: the API runs under the same identity, and validates callers' Entra bearer
  tokens. No inbound key either.

The function runs under a **user-assigned** managed identity, deliberately: the Container
Apps API needs the same grants, and a shared identity means granting once. That has now paid
off twice - the API required no new role assignment and no new database user, and the AI
provider needed one role assignment covering both hosts.

**There is exactly one secret, and it is optional.** The Anthropic key that used to be the
exception was deleted outright when the provider moved to Azure OpenAI, and every Azure-side
connection is Entra-authenticated. What brought a vault back is narrower: OpenAI's Batch API
carries the `gpt-5.6` family, which Azure's batch matrix does not, and gives corpus-wide
extraction a rate pool separate from the interactive deployment's - which is what stalled the
first real backfill. `api.openai.com` has no identity-based path.

It is fenced as tightly as the design allows. It is off unless `aiOpenAiBatchEnabled` is set, so
a clone still deploys with no vault. It reaches **job adverts only** - candidate profiles stay on
the Azure path, so personal data never leaves the tenant, and that split is enforced by which
function calls which extractor rather than by a setting. Its value is set out of band with
`az keyvault secret set` and appears in no template, parameter file, output or deployment
history. Anything else that seems to need a credential is still a signal the design is being
worked around.

## Key files

- `src/JobPlatform.Core/Parsing/JobCsvParser.cs` — the JobSpy CSV contract. Written against
  the real export; changes here need a fixture case. The column count is not fixed - the
  scraper runs a JobSpy fork that adds columns (freehire's freshness signals, LinkedIn
  applicant counts), so the parser reads by name and ignores what it does not model.
- `src/JobPlatform.Core/Metrics/MetricsCalculator.cs` — every metric. Pure and Azure-free,
  which is why the metric surface is fully unit-testable.
- `src/JobPlatform.Core/Enrichment/concepts.json` — the vocabulary. 222 concepts on a DAG,
  and the source of truth: the SQL tables are a projection of it, and it is also what the
  model is handed as its allowed output set. Changing it is a reviewable diff.
- `src/JobPlatform.Core/Enrichment/ConceptGraph.cs` — the loader, the matcher and the
  in-memory closure. Read the remarks on `NameChar` before touching the boundaries.
- `src/JobPlatform.Core/Enrichment/PostingEnricher.cs` — the composition root for every
  deterministic classifier. Decides precedence between them; the classifiers themselves stay
  separate so each keeps its own tests.
- `src/JobPlatform.Data/Sql/ConceptSeeder.cs` — projects the vocabulary into SQL. The only
  writer of those tables, and idempotent.
- `src/JobPlatform.Ingestion/Curated/CuratedExporter.cs` — the Parquet analysis surface,
  recomputed per partition rather than appended.
- `src/JobPlatform.Data/Sql/JobPostingRepository.cs` — the upsert, and the daily rollup
  aggregates.
- `src/JobPlatform.Ingestion/IngestionPipeline.cs` — the digest, shared by the blob trigger
  and the admin reprocess endpoint so both run the same path.
- `infra/main.bicep` — the whole stack.
- `src/JobPlatform.Api/Program.cs` — composition only. Routes live in `Features/<Name>/`, one
  `IEndpointGroup` each, registered in `Endpoints/EndpointGroupExtensions.cs`. Adding a
  feature is a folder plus one line there.
- `src/JobPlatform.Data/Sql/JobPostingQueryRepository.cs` — every SQL read the API makes.
- `src/JobPlatform.Ai/AiRegistration.cs` — the whole LLM abstraction: `BuildKernel` puts two
  Azure OpenAI deployments on one Kernel, addressed by service id. No key anywhere in it.
- `src/JobPlatform.Ai/AiPrompt.cs` — the execution settings every prompt uses. Two of them are
  load-bearing in ways that fail with a 400 rather than a compile error; read the remarks.
- `src/JobPlatform.Core/Matching/MatchScorer.cs` — the whole scoring rulebook. Pure and
  Azure-free, like `MetricsCalculator`, which is what makes the numbers assertable exactly.
- `src/JobPlatform.Core/Matching/MatchRanker.cs` — what the matches page is *ordered* by, which
  is not the score. Pure and Azure-free for the same reason, and it carries the measurement that
  justifies every constant in it.
- `src/JobPlatform.Core/Model/PostingAge.cs` — how old a posting is, as one rule: the board's
  stated date where it published one, first-seen where it did not. Pure, and the two relational
  spellings in `src/JobPlatform.Data/Sql/PostingRecency.cs` are transcriptions of it that
  `PostingRecencyTests` holds to it. Every age filter in the system goes through one of the three.
- `src/JobPlatform.Core/Profiles/CandidateProfile.cs` — the supply side of the match, and
  `ToDocument()`, which is the exact text the extractor reads and the hash is taken over.
- `src/JobPlatform.Data/Sql/CandidateProfileRepository.cs` — every profile read and write.
  Takes a subject id and never a profile id; that is the authorisation boundary.
- `src/JobPlatform.Core/Searches/ScraperConfigDocument.cs` — the contract with the scraper repo,
  and **the only place in the system that writes a jobspy parameter name**. Read the remarks on
  `ToParams` before adding a field to a search.
- `src/JobPlatform.Data/Sql/ScraperSearchRepository.cs` — every search read and write. Takes a
  subject id like the profile repository, with one named exception for the publisher.
- `src/JobPlatform.Ingestion/Functions/MatchSweepFunction.cs` — the nightly pass. Scores
  everything, then spends the model budget on what clears the threshold.
- `src/JobPlatform.Core/Submissions/SubmissionState.cs` — the fold from an event log to a
  status. Pure and Azure-free like `MatchScorer`, which is what makes its answers assertable
  exactly. There is no status column anywhere; read the remarks before adding one.
- `src/JobPlatform.Core/Submissions/FormFieldCatalog.cs` — the only profile data the agent
  surface will ever answer, one field at a time. What is absent from it is as considered as what
  is present.
- `src/JobPlatform.Core/Submissions/ParkReason.cs` — why an application was parked, and the whole
  of the retry policy as one pure function. Read the remarks before adding a reason, and before
  reaching for a `Blocked` event instead of a park.
- `src/JobPlatform.Api/Features/Mcp/SubmissionTools.cs` — the whole agent surface, reads and
  writes both, and nothing on it applies to anything. The `[Description]` on each tool is the
  interface a model reads, so it is documentation that changes behaviour.
- `src/JobPlatform.Documents/MarkdownPdfRenderer.cs` — markdown to PDF, through a parsed AST and
  a fixed node mapping. No HTML step exists. It renders a cover letter the model wrote and a CV
  the candidate wrote, and it cannot tell them apart, which is the point.
- `src/JobPlatform.Core/Applications/CvVariant.cs` — what a CV variant is, its bounds, and the
  library rules as pure functions. It has no concepts field, and that absence is the guard rather
  than an omission; read the remarks before adding one.
- `src/JobPlatform.Core/Applications/CvVariantSelector.cs` — which CV is sent, or why none is. The
  floor and the margin are argued from the credit table rather than measured, and it says so.
- `src/JobPlatform.Core/Applications/CvGapBrief.cs` — what to write next, ranked by how many
  postings each missing CV blocks. Aggregate by construction: there is no per-posting overload.
- `src/JobPlatform.Core/Applications/ApplicationPackFile.cs` — what a rendered file is called and
  where it is kept. Neither `FileName` nor `VariantBlobPath` has a parameter a variant's label
  could be passed in, which is what keeps one name on every CV.
- `src/JobPlatform.Data/Sql/CvVariantRepository.cs` — every variant read and write. Takes a
  profile id the caller resolved, like the submission and answer stores.

## Conventions and constraints

- **Flex Consumption supports only the event-based blob trigger.** The polling trigger will
  not fire there. Hence `Source = BlobTriggerSource.EventGrid` and the Event Grid system
  topic in `infra/modules/eventgrid.bicep`.
- **Cosmos serialization is pinned to camelCase System.Text.Json** in `CosmosClientFactory`.
  The SDK default emits PascalCase, which breaks both the required `id` field and the
  `/searchTerm` partition key path. `CosmosClient` must stay a singleton — it owns the
  connection pool.
- **The SQL database auto-pauses.** Waking it takes 30-60s, so EF is configured with
  `EnableRetryOnFailure` and a 60s connect timeout. Keep connections short-lived: an open
  connection prevents auto-pause and burns the free vCore-second grant. Avoid adding
  per-row round trips for the same reason.
- **The Cosmos SDK needs `Newtonsoft.Json` at runtime**, even though item serialization is
  pinned to System.Text.Json. It is referenced explicitly in `JobPlatform.Data`. Do not
  "fix" the build check with `AzureCosmosDisableNewtonsoftJsonCheck` - that only hides it
  until the published app fails with `Could not load file or assembly 'Newtonsoft.Json'`.
- **A user-assigned identity needs its client id in the SQL connection string**
  (`User Id=<clientId>`), otherwise `Authentication=Active Directory Default` fails with
  "Unable to load the proper Managed Identity". It is appended in
  `infra/modules/functionapp.bicep`, not in the sql module's output, so that output stays
  usable by a developer signing in as themselves.
- **HTTP functions use ASP.NET Core integration types** (`HttpRequest`/`IActionResult`),
  because the host is built with `ConfigureFunctionsWebApplication`. Mixing in
  `HttpRequestData` silently leaves the route unmapped. Also avoid an `admin/` route
  prefix - it is reserved by the host and puts the function in an error state.
- **The blob trigger's poison queue lives on the trigger's connection** (the landing
  account), not on host storage, so the identity needs Queue Data Contributor on both.
- **Ingestion must stay idempotent.** `ScrapeRuns.BlobPath` is unique and metric document
  ids derive from the blob path. Event Grid redelivers; a replayed blob must converge. The
  same contract extends to the derived rows: assertions are rewritten only for postings whose
  content changed, and `PostingExtractions` is keyed on `(PostingId, ExtractorVersion,
  InputHash)`.
- **The concept key is the identity; the label is an attribute.** `skill.kubernetes` is what
  is stored and what a backfill is written against. Renaming a label is an edit; renaming a
  key is a data migration. An earlier design used the canonical name as the key and had no
  way to separate the string in the advert from the concept it denotes.
- **`broader` is a DAG, not a tree**, and the data needs it: Python is a language *and* is
  used in backend, data and ML. The flat `category` field this replaced could record only one
  of those and was wrong three ways.
- **A change to `concepts.json` needs `EnrichedPosting.CurrentVersion` bumped with it.** The
  vocabulary carries its own version and nothing reads it when deciding staleness:
  `JobPostingRepository` compares `EnrichedPosting.CurrentVersion` against
  `JobPostings.EnrichmentVersion`, so a vocabulary edit that leaves the constant alone is an
  edit no stored posting will ever pick up. Bumping it marks the corpus stale, and a reprocess
  or the next re-scrape rebuilds the assertions. It also needs `seed-concepts` re-run, because
  the SQL label tables are a projection of the file - resolution reads the embedded copy, so
  matching is right either way, but the projection would drift.
- **Seed before reparsing, and reparse before reprocessing.** `deploy.yml` runs `seed-concepts`
  in the same job as `migrate`, which is **skipped on an ordinary push** - so a commit that adds
  concepts deploys a build whose vocabulary is ahead of the SQL projection. `PostingExtractionWriter`
  resolves keys through that table and drops what it cannot find, having already deleted the
  posting's model rows, so a reparse run first would strip assertions and report success. It now
  warns and names the keys and the command, but the ordering is the actual fix:
  `gh workflow run deploy.yml -f run_migrations=true`, then `reparse-extractions`, then
  `reprocess` for the enrichment bump.
  **Dispatch that run after the last push, not before it.** The concurrency group cancels the
  *pending* run when a newer commit queues behind it, and a dispatched migration run is pending
  while the push ahead of it deploys - so a dispatch followed by any push is silently cancelled.
  It shows in `gh run list` as `cancelled` with no jobs at all, which reads like a platform
  glitch rather than the group working as designed.
- **Run `dbadmin seed-concepts` after any migration.** The concept tables are a projection of
  the vocabulary shipped in the build; a schema that has moved without them silently stops
  recording assertions for anything new. `deploy.yml` runs it in the same job as `migrate`,
  and the ingest logs a warning naming the command when it notices.
- **Read the mention log with `dbadmin coverage` before adding vocabulary, and add from it.**
  It is the growth mechanism and it had no reader until 2026-08-31; the first read said the
  vocabulary was missing the entire AI-engineering cluster the corpus is full of - Claude Code in
  248 postings, RAG 155, Cursor 136, MCP 117, LangGraph 111 - none of which the matcher could see.
  Rank by how many postings name a form, not by total occurrences: one advert repeating a word
  twenty times is one employer's habit, twenty adverts saying it once is a gap. And add nothing
  for symmetry - iOS is absent because Android appeared in the log and iOS did not.
- **A surface form that cannot be resolved is recorded, never dropped.** `PostingMentions`
  exists because the previous vocabulary handled ambiguous names — Go, R, C, Julia — by
  refusing to match them, which meant the data was wrong with no way to find out by how much.
  It is also where new vocabulary comes from: the most frequent unresolved forms each month.
- **`PostingExtractions.PayloadJson` is why a parser change is free, and why you must not bump
  the extractor version for one.** The stored answer means re-reading the corpus costs a query;
  re-asking it costs about 10 million tokens at the measured 1,700 per document over 5,822
  postings. `DocumentExtraction.CurrentVersion` means "the stored answer is stale and must be
  asked for again" - a change to how an answer is *read* does not make it stale. Bumping it for a
  parser or vocabulary change marks the whole corpus for re-extraction and leaves that bill for
  whoever next runs the backfill, to buy nothing. Use `POST /api/reparse-extractions` instead;
  it is explicit, idempotent and resumable from the `nextPostingId` it returns.
- **The model's `unknownSkills` list is resolved through the graph before it becomes a mention.**
  The prompt sends `key = label` and no aliases, so a model reading "generative AI" sees only
  `skill.llms = LLMs` and reports it unknown - and that list used to be recorded verbatim with
  nothing ever checking. Measured: "AI" in 89 postings, "machine learning" in 52, "generative AI"
  in 43, every one an alias the resolver already knew. Resolving here rather than shipping the
  aliases in the prompt is both cheaper - the vocabulary precedes every extraction call - and
  safer, because it inherits the ambiguity refusal: Go, C, R and Claude stay unresolved because
  the graph refuses them, not because this code remembered to.
- **Never let the model invent a concept key.** `KernelDocumentExtractor` re-checks every key
  against the graph and demotes anything unknown to a mention. A hallucinated key is
  indistinguishable from a real one in SQL and would quietly split a concept in two.
- **A rule list has to be written in the tokeniser's spelling, not the reader's.**
  `RoleFamilyClassifier` named `.net`, `node.js` and `ui/ux` for as long as it existed, and
  `TitleTokenizer` splits on `.` and `/` — so all three had already been cut into `net`,
  `node` + `js` and `ui` + `ux` by the time a rule saw them. The rules read as though they
  worked and 24 corpus titles saying ".NET Developer" came out `Unknown`, which the dashboard's
  `roleFamily` filter shows as much as matching does. `TitleTokenizer.DottedNames` folds the
  three technology names whose spelling contains a separator; the rules now name the folded
  form. **A test that asserts the classifier is not enough — assert the tokenisation too**, or
  the next dead entry looks exactly like a working one.
  Folding a short list rather than making `.` a word character, and that was measured: treating
  it as part of a token fixed .NET and broke "Sr.Product Manager" and "React.js Developer",
  because any `Word.Word` spelling then becomes a single token. Thirteen fixes for two
  regressions is the shape of a change to reject, so it was.
- **Do not add a column to `TrackedColumns` before the scraper emits it.**
  `JobDigestFunction` warns on every column at 0% fill, and "not shipped yet" is not the
  regression that warning exists to catch. The parser reads by name, so mapping a column
  early is free; tracking it early is noise on every run.
- **The curated zone is a separate container, not a prefix.** The Event Grid subscription is
  scoped to `jobs-landing`, so a curated write can never trigger an ingest; and the identity
  holds Blob Data *Reader* on the landing account deliberately, because that container is the
  only copy of the source data. `jobs-curated` gets its own scoped Contributor grant.
- **The SQL server lives in a different region from the rest of the stack** (`sqlLocation`
  in `infra/main.bicep`). This is not an oversight: the free offer is not provisionable in
  Spain Central or West Europe on this subscription, and several other regions refuse new
  SQL servers entirely. France Central works. Probe with a throwaway server before changing
  it, and remember a server's region is immutable — changing it means deleting and
  recreating.
- **The deployed database is Basic (DTU), not the free serverless offer.** `sqlSku` in
  `infra/modules/sql.bicep` selects; it defaults to `free-serverless` so a clone stays free,
  and the repository variable `JP_SQL_SKU=basic` opts this deployment in. Never remove that
  variable: CI redeploys on every push, the parameter would fall back to the free offer, and
  a database cannot be converted *back* to it. A repository variable is not enough on its
  own - it must also be mapped into `deploy.yml`'s `env:` block, or `readEnvironmentVariable`
  never sees it. Azure rejects the downgrade rather than performing it, so the failure mode
  is a red pipeline rather than a lost database. Basic has no auto-pause because the DTU model
  has no serverless tier — that is what removed the ~1 minute cold start.
- **The API keeps one replica resident** (`apiMinReplicas` in `infra/main.bicep`, repository
  variable `JP_API_MIN_REPLICAS=1`). It defaults to 0 so a clone stays free. Unlike
  `JP_SQL_SKU`, dropping this one **fails silently** - Azure accepts the change, the API goes
  back to scale-to-zero, and the only symptom is a ~20 second wait on the first request after
  an idle period. That 20 seconds is Container Apps scheduling and sidecar startup, not the
  image pull, so it cannot be fixed in the application. A keep-alive ping is not a substitute:
  a min-replica bills at the idle rate, a pinged one at the active rate, ~3x more for the same
  warm replica.
- **Basic is always-on, not always-warm.** The remaining first-request latency is SQL, and it
  is not auto-pause - Basic cannot pause. A cold buffer pool refilled at 5 DTU is IO-bound
  (`physical_data_read_percent` ~90%, `cpu_percent` ~30%); a shortlist query measured 12.6s
  cold against ~100ms warm. Only a higher tier fixes it; indexing does not, because the warm
  path already uses the same plan.
- **Free-tier ceilings are load-bearing**, not incidental: Cosmos autoscale max 1000 RU/s,
  SQL `useFreeLimit` with `freeLimitExhaustionBehavior: AutoPause`. Raising either starts
  billing.
- Metrics changes belong in `MetricsCalculator` with a matching assertion in
  `MetricsCalculatorTests`, against the synthetic fixture's known-by-construction counts.

### Scraper searches

- **The slug is the identity and the name is an attribute**, exactly as `skill.kubernetes` is the
  identity and "Kubernetes" the label. A search's slug becomes the blob name the scraper writes,
  which `BlobNameParser` reads back, which keys `JobPostingSearchTerms`, which partitions the
  Cosmos metrics and names a curated Parquet partition. Renaming a search is an edit;
  `ScraperSearchRepository.UpdateAsync` will not move a slug, and nothing should teach it to.
- **`SearchSlug.Slugify` has to agree with `slugify` in the scraper repo's `scrape_jobs.py`,
  character for character.** The scraper still slugifies on its fallback path, so a name
  producing two different slugs would attach one search's postings to two search terms with
  nothing reporting it. That is why the non-ASCII cases are pinned and why nothing here
  transliterates: agreeing beats being pretty.
- **The slug namespace is global; a name is unique only per owner.** Two people may both call a
  search "London backend" and `SearchSlug.Unique` suffixes the second slug. Refusing the save
  instead would tell one person that another's search exists under that name.
- **`ScraperConfigDocument.ToParams` is the only code that writes a jobspy parameter name, and
  that is the security property of the whole feature.** The scraper calls
  `scrape_jobs(**params)`; a second writer is a second route for an unvalidated key to reach a
  call whose keyword arguments include `proxies` and `freehire_api_key`. The request contract is
  typed fields and carries no parameter map — keep it that way.
- **An option nobody chose is omitted, never sent as null.** The scraper merges the published
  params over its own `defaults:`, where its operational settings live, so a null would blank one
  of them. "Did not choose" and "chose nothing" have to be different bytes on the wire.
- **A failed publish must not fail the save.** `ScraperConfigPublisher` swallows and logs; the
  response carries the timestamp, the page shows it, and `POST /searches/publish` is the retry.
  Losing somebody's typing to a role assignment that has not propagated is the worse outcome, and
  a stale blob is recoverable where typing is not.
- **The publish is a blob and never an endpoint the scraper calls.** The NAS has no managed
  identity, so an API would put a client secret or a function key on it — the thing this
  architecture does not have. If a change here seems to need the scraper to authenticate against
  the API, that is the signal the design is being worked around.
- **Nothing under `/searches` may join a client's bootstrap sequence.** It reads SQL, and
  `/search-terms` is served from Cosmos precisely so opening the dashboard cannot wait on a
  database that pauses when idle. The Searches page is opened by a person and may wait; the
  picker every page depends on may not.
- **Searches are owned; the corpus is not.** `OwnerSubjectId` decides who may edit a search, not
  who may read the postings it found — `JobPostingSearchTerms` already attributes one advert to
  every search that turned it up, and an advert is public text. Scoping the browse experience
  later is a filter over that column, which is why it exists now.
- **There is no coalescing and no cap.** Two users configuring the same search scrape it twice,
  and a run costs the sum of every enabled search across every user. That is a deliberate choice,
  not an oversight; `ScraperSearchValidation` bounds one search and nothing bounds the total.

### The concept vocabulary

Curated in-house, deliberately. The alternatives were checked rather than assumed:

- **ESCO** is CC BY 4.0 and freely redistributable, but querying its API directly returns
  **zero results for Kubernetes, Docker and Terraform**, and "React" matches the English verb
  ("react calmly in stressful situations"). It is strong on generic competency phrasing and
  unusable for a software-engineering corpus.
- **Lightcast Open Skills** has the best technology coverage of anything tested, but its
  licence forbids redistribution and embedding the dataset in a product — fatal for a public
  repository, and no bulk download exists anyway.
- **SFIA** is deliberately technology-agnostic (no Kubernetes, no React) and its free licence
  covers one individual's private career development, not use inside software.
- **O\*NET** is CC BY 4.0 *and* carries Kubernetes as a "Hot Technology". It is used as an
  **offline gap-check only**: periodically diff the vocabulary against its Technology Skills
  list to find terms we have missed. No runtime dependency, no committed data, no attribution
  obligation.

At 222 concepts in a slow-moving domain, the "taxonomies go stale" argument that justifies
Lightcast's model at 34,000 skills does not apply. The unresolved-mention log is the growth
mechanism, and it is derived from the corpus rather than guessed at.

### Submissions and the agent surface

- **A submission, not an application.** `ApplicationDocuments` already means generated drafts and
  `Candidacy` is taken by `CandidacyAssessment`. Reusing either would put two meanings on one word
  in a system whose matching code reads both.
- **The event log is the record and the status is a fold over it.** Not a mutable `Status` column.
  These events are written by a client reading recruiter email and deciding what a message means,
  and that client will sometimes be wrong: a stored status tells you it is wrong *now*, an event
  log tells you what it saw, when, and from where. That is the lesson the AI ledger taught on the
  other side of the same problem.
- **`Stale` is derived, never stored.** Storing it means a timer to write it, a race between that
  timer and a real event, and a row that is wrong between the two. A closed application is never
  stale — an employer that stopped replying has gone quiet; one that said no has not.
- **A terminal event wins outright, and otherwise the furthest-advanced phase wins — never the
  most recent.** Both rules exist because the obvious implementation is wrong in a specific way:
  events arrive late and out of order from an inbox reader, so a late `Acknowledged` would walk an
  `OfferReceived` backwards, and an automated "thanks for applying" after a rejection would reopen
  a dead application. `SubmissionStateTests` asserts both against the naive versions.
- **`Type` is the phase; `Stage` is a label inside it.** "Tech round 2" is text on an
  `InterviewScheduled` event, not a member of the enum — the enum is what the dashboard groups by
  and what the fold switches on, and it must not grow every time a company invents a round.
- **Parking is an attribute on the submission and never a member of `SubmissionEventType`.** The
  apply-loop spec asked for a `Blocked` member, and it is the change most likely to be tried again
  because it looks smaller than a pair of columns: the log is already the record, and "we could not
  apply" plainly happened. It does not work, and the reason is mechanical rather than a matter of
  taste - a new member has to survive both of the fold's rules. Ordered above `OfferReceived` it is
  what `max` picks, so a captcha parked in the morning outranks an offer that arrived in the
  afternoon, and the next run - which reads the parked columns rather than the log, and so still
  sees a parked row - appends another and reports that offer as blocked. Made terminal it wins
  outright, so one park closes an application that was in fact sent, and `IsClosed` then makes the
  row that most needs chasing permanently un-stale. Below `Submitted` there is nowhere to put it:
  **the only free low value is 0, which is the deliberate "nothing was established" slot** - the
  one enum here with a zero member, `SubmissionChannel`, spends it exactly that way, and a nullable
  `ParkedReason` already spells the same absence - and such a row would still carry a non-null
  phase. There is no numbering that works, because the enum is a total order
  over how far an application got and parking is not a point on it: it says no attempt was made at
  all. **And parking is reversible where an event is not.** `UnparkedAtUtc` lets a posting back
  into the queue; undoing a `Blocked` would need "the most recent event wins" for one member,
  precisely the rule the fold was written to refuse. So the fold is untouched, the fact lives on
  `Submissions` as `ParkedReason` / `ParkedAtUtc` / `UnparkedAtUtc`, and **every reader that counts
  applications has to be taught that a parked row is not a sent one** - the dashboard counts any
  row with a non-null phase, and a park must not land in that total.
- **No deletes, anywhere on this table, with exactly one operator-only exception.** Withdrawing
  is a `Withdrawn` event; an append-only log with no eraser is the only version worth auditing.
  `dbadmin delete-submissions` is the exception and it exists for rows that never described a
  real application - a test of the write path, a client that misfired. Those are not history to
  preserve, they are noise that makes the history wrong, and a `Withdrawn` event on them asserts
  something equally untrue.
  **It must stay a console command.** An HTTP route would be reachable with the same token the
  MCP client carries, so a misbehaving agent could erase real applications - which inverts the
  entire argument for the tool surface being safe to expose. Needing a connection string, a
  database user and a firewall rule is the point, not friction to be removed. It is a dry run
  unless `--confirm` is passed, because the ids are typed by a person reading a list.
- **Both unique indexes are the feature, not an optimisation**, and they went in before the write
  path that needs them: retro-fitting a unique index to a table that already holds duplicates is a
  data migration rather than a schema change.
- **`SubmissionChannel` reads `JobPostings.OffsiteApply` first and the apply URL only as a
  fallback, and that ordering is a fix rather than a preference.** The design originally had only
  the URL and read its absence on a board posting as "the board hosts it". Measured on 2026-09-01
  that was wrong for the whole corpus: all 4,470 LinkedIn postings of the previous week carried no
  direct link, and the job detail page had been fetched for 98.4% of them - LinkedIn had stopped
  publishing apply URLs to signed-out clients entirely, and its guest page now contains no
  non-LinkedIn URL at all. The fork reads LinkedIn's own offsite markers instead and emits
  `offsite_apply`, so the route survives where the destination does not.
- **`OffsiteApply` is three-state and the third state is load-bearing.** Null means nothing was
  established - the detail page was not read, the board does not say, or the posting predates the
  column - and it is not the same as `false`, which means the board hosts the application.
  Collapsing them is the fault the column was added to undo. `Ats` can therefore be true with only
  the board's own URL to offer, which is correct: the employer takes the application and the
  posting is where you find out how.
- **Two nulls that share one representation can often be told apart by a second column, and it is
  worth looking for one.** "No apply URL" meant either "the board hosts it" or "nobody opened the
  detail page", documented as indistinguishable. They are not: the description comes off the same
  page, so a posting with one is a posting the scraper read. `dbadmin apply-links` reports both and
  names which fault it is; the digest warning uses the same pair. That cost one extra
  `SUM(CASE WHEN ...)`.
- **There are two fingerprints and merging them is a regression.** `JobFingerprint.ContentHash`
  answers "did this posting change", is stored on every row, and gates embedding staleness -
  `EmbeddingRepository` compares it, so widening it marks the whole embedded corpus stale.
  `JobFingerprint.CrossBoardKey` answers "is this the same job as that one" and parses the city
  out of the location first. `CountCrossSiteDuplicates` read ContentHash until 2026-09-01 and
  therefore reported **zero every run since it was written**: the stored hash folds in the raw
  location string, and boards write it differently - "London, England, United Kingdom" against
  "London, UK".
- **The city is required in `CrossBoardKey`, and that is measured rather than cautious.** Title
  and employer alone matched 285 postings across boards; adding the city left 211. So 74 of them,
  better than a quarter, were one employer advertising one title in several cities - and merging
  those hands somebody the apply link for the wrong city's vacancy, which is worse than no link.
- **`CrossBoardKey` is stored hashed, and exactly one function in Core produces the hash.** The
  readable form cannot be indexed: title, employer and city at this schema's own widths reach 952
  characters, 1,904 bytes, against SQL Server's 1,700-byte cap on a nonclustered index key - the
  same arithmetic already written down on the `(Company, LocationCity)` index. So the column is
  `char(64)` and `JobFingerprint.CrossBoardKeyHash` is the only thing that may write to it. **The
  single-writer half is the load-bearing one**: ingest stamps the key on every upsert and
  `dbadmin backfill-crossboard` stamps it on the corpus that predates the column, so two code paths
  reach the same rows, and a second spelling of the hash - another separator, another normaliser -
  splits one cluster in two with nothing failing and no count to compare against. Null propagates
  rather than hashing the empty string, for the same reason the key itself answers null: a posting
  with no city is not the same job as another posting with no city.
- **A missing apply link is recovered from the same job on another board, and says so.**
  `ApplyableRow.ApplyUrlSource` distinguishes `Posting` (published by the board it came from)
  from `MatchedOnAnotherBoard` (an inference) from `BoardPosting` (none known). Roughly 5% of the
  links LinkedIn stopped publishing come back this way at no request and no risk. **The
  provenance is not decoration**: a caller that cannot tell an inference from a published fact
  has no way to notice when the match was wrong.
- **The posting's own board outranks a title match.** `OffsiteApply == false` means that board
  says it hosts the application, and it is talking about this listing rather than one that
  resembles it - so no link is borrowed for those.
- **The shortlist's channel filter and its projection are written out twice and must agree.** EF
  translates one and materialises the other, so a shared helper would have to be an expression
  tree nobody can read. `The_channel_is_projected_from_the_apply_link_and_filters_before_the_bound`
  is what holds them together, and it has already caught them diverging once.
- **A static predicate over a column has no SQL, and that decides the shape of two files.**
  `ParkReasonPolicy.Retryable` is the whole retry policy and EF cannot translate a call to it, so
  the queue predicate would have to spell the rule out a second time in a `where` clause - the
  channel filter's problem, survivable only because a test holds the two spellings together. Here
  it was avoidable, so it was avoided: the policy also exposes `Permanent` and `AwaitingAnswer` as
  lists **derived from that same function**, and `Contains` over a list becomes an `IN`. One
  definition, two readers, nothing to drift. `AtsVendorDetector.Detect` has the same limitation
  with no such escape - it reads a URL rather than compares one - so it runs after materialisation,
  in the mapping and not in the projection. The consequence is worth knowing before somebody
  promises it: the queue cannot *filter* on the vendor, and making it able to means persisting the
  vendor on the posting, not teaching the query to call a function.
- **A synthetic fixture can be too clean to catch a real bug.** The cross-board duplicate in
  `jobs-sample.csv` carries an identical location string on both rows, so the broken metric
  matched it and its assertion passed for as long as the metric was wrong. The test that catches
  it writes the two locations the way two boards actually write them.
- **The daily cap on `Submitted` events lives in `SubmissionRepository`, not at the call sites.**
  Two paths reach it today and a third will; a guard written at the call sites survives until
  then. It bounds `Submitted` alone - recording that a hundred applications exist is fine, and
  claiming a hundred were sent today is not - and counts by the event's `AtUtc` rather than by
  when the row was written, so backdating a hundred is the same assertion and is capped the same
  way. The idempotency check runs *before* the cap: a client retrying a write it is unsure landed
  must not be refused for a quota that very event already spent.
- **The burn-down belongs on `list_applyable` and `record_event`, and never on
  `create_submission`.** The spec put `remaining` on the create call, which cannot work: creating a
  submission spends no quota - the cap counts `Submitted` events, and a row with no events has
  claimed nothing - so a client creating twenty rows before recording a single event is handed the
  same number twenty times. **A figure that does not move while the client works is worse than no
  figure**, because it reads as twenty applications of headroom and is one. The two places it
  belongs are the two where a run can still act on it: `list_applyable`, where the batch is chosen
  before the first tab opens, and `record_event`'s success answer, where a long run watches it
  fall. Discovering the cap by being refused means discovering it at `record_event`, which by the
  loop's design runs *after* the browser has sent the form - an application that exists in the
  world and cannot be recorded, which is the worst state this system has, because every later
  decision reads the log rather than the world. **It is a plan and never a reservation**: nothing is
  held, another client sharing the candidate may spend some in between, and the cap in
  `SubmissionRepository` remains the only thing that enforces.
- **The digest's apply-link warning is keyed on the route being unknown, not on the URL being
  absent, and the first version had that wrong.** It alarmed at a 98% "board-hosted" share, which
  was right on the day LinkedIn's selector broke and wrong forever afterwards: LinkedIn publishes
  no apply URLs at all now, so that share is pinned at 100% and the warning would have fired on
  every ingest for the rest of time. A warning that fires on the ordinary case is one people learn
  to scroll past. `RouteUnknown` - no link *and* no offsite flag - has no legitimate steady state,
  because every board answers that question one way or the other when it is working.
- **The server records that something was submitted; it never submits.** There is no
  `submit_application` tool and there must never be one — applying is irreversible and
  outward-facing, so keeping it outside means no bug here can reach an employer. `McpEndpointTests`
  asserts the tool surface is *exactly the list it names*, an equality rather than a superset, so
  adding one is a red build. **The equality is the property; the number is not**, and this rule
  said "four names" while the surface was six and then fourteen - a count repeated in prose goes
  stale silently, where the list in the test cannot. What the test defends is that moving the
  surface is a diff somebody signs off: a superset assertion would have accepted the apply loop's
  eight new tools without a word, and would accept a fifteenth just as quietly.
- **There is no `get_profile` either.** A tool result is transcript content wherever the client
  runs. `get_form_field` is the substitute: one answer, from `FormFieldCatalog`, logged.
  `get_submission_pack` is the honest exception — a tailored CV is the profile rewritten in prose
  — and is logged on the same terms rather than treated as a public-text read.
- **Two answer namespaces, and they never mix.** `FormFieldCatalog` is the *derived* one: it reads
  the profile and answers a fixed allowlist. `FormAnswers` is the *declared* one: it holds only
  what a person typed as the answer to a question. That split is what makes the sensitive case safe
  without depending on a flag being set correctly. Marking catalogue fields `sensitive: true` -
  which is what was asked for - converts "cannot be answered" into "answered unless a boolean was
  right", a weaker guarantee wearing the same word. An EEO question, a salary expectation, a date
  of birth are not reachable from the profile at all, so **a sensitive value can exist only because
  somebody wrote it, and nowhere else because there is nowhere else.** `Sensitive` on the row
  drives redaction in the disclosure log and a confirmation on the dashboard; it is never
  permission to infer. Answers are superseded rather than updated, for the reason the event log is
  append-only: a store that overwrites cannot say what was submitted last year.
- **Resolution runs on this side of the tool call, and abstains by default.** `resolve_form_field`
  hands the question to `IFormFieldResolver`; the alternative - shipping the candidate's stored
  answers to the client to choose between - is the whole-profile disclosure this surface exists
  instead of, with an extra hop and a bill attached. Three of its four stages need no provider, so
  a deployment with no AI still answers from the allowlist, from what the candidate has typed, and
  from what the same question resolved to before; only the fourth is a model call, and it abstains
  rather than failing. **The characteristic failure of a matcher is the confident near-miss**, and
  a wrong answer on an application is read as a statement the candidate made rather than as a bug
  in a tool they were using - so below the confidence floor, on a sensitive field with no exact
  stored answer, or where an option set will not map cleanly, the answer is that a person is
  needed. An interruption is the cheaper mistake, and it is only cheap because it is recoverable:
  the question is raised as an `OpenQuestion` and the posting returns **when that is answered**
  rather than on the next run, or the agent parks the same posting for the same missing answer
  every run, which is a loop and not a retry.
- **A disclosure record names what was asked for and never the value.** An audit log holding the
  data it audits has moved the problem rather than solved it. Cosmos, not SQL, for the reason every
  dashboard read is; its own container rather than `aiCalls`, because the two answer different
  questions and their retention is not one decision.
- **`list_applyable` gates on the model's verdict, not on a score cut, and its threshold is a
  third constant.** `MatchRanker.FusionFloor` and `MatchSweepFunction.AssessmentThreshold` answer
  different questions and briefly merging those two was already a mistake; do not merge a third
  into either. The reason it is the verdict is the finding behind `MatchRanker` — the score is a
  good filter and a bad final sort.
- **The queue excludes on what a submission row *says*, never on one existing, and that single
  clause is what made parking impossible.** `ListApplyableAsync` used to ask only whether any
  submission existed for the pair - so the instant a park wrote the row it needs to park against,
  the posting left the queue forever, and "come back to this once the captcha is gone" and "never
  show me this again" were the same operation. Five clauses replace it and each holds a posting
  back for a different reason: a live application on this posting, a live application on any other
  listing of the same job, a permanent park, a park waiting on an answer that has not arrived, and
  a park waiting on a CV, held while nothing in the library covers what that park recorded as
  missing - see **The CV library** below for why that fifth one cannot be simplified.
  Everything else - a captcha, a login wall, a spent day's quota - comes back on the next run,
  which is the entire point. **Only a live application suppresses the rest of a cross-board
  cluster**: a permanent park is about one listing, so suppressing a cluster on the strength of
  another board's 404 would hide a live vacancy for good.
- **`ListApplyableAsync` honours `DismissedAtUtc`, and it was the only match query that did not.**
  `ListAsync` and `GetUnassessedAsync` both exclude dismissed pairs; this one did not, so a posting
  the candidate had said no to on the dashboard came back to the agent on every run, with the agent
  given no way to know it had been refused. A dismissal is the candidate's own decision about a job
  and it outranks the model's verdict. It stayed invisible for the reason these always do: **a row
  that should be absent from a list is not something anybody notices.**
- **The queue's filters are enforced server-side, and the strictest combination of them returns
  nothing today.** `minAssessmentScore` is applied in SQL so that a prompt-level bug cannot fire
  applications at bad matches, and a null score does not clear it - reading "not judged" as "let it
  through" turns a safety rail into a way past one. But measured on 2026-09-02,
  `ApplicationDocuments` holds **one row for the entire system**, so `documentsReady=true` with
  `applyUrlSource=Posting` and `minAssessmentScore=80` yields **zero** postings. That is a correct
  predicate over an empty input and must not be "fixed" by loosening it: **document generation is
  what the loop is blocked on.** `GenerateApplicationsFunction` is the nightly pass that answers
  it - 04:30 UTC, an hour after the sweep, bounded per night and to one document per HTTP nudge -
  and the CV library takes the expensive half of that work away entirely, since a chosen CV costs
  no model call at all. What is left to generate is the covering letter and the drafted answers,
  which is where the cap now binds. Re-measure before quoting the zero: it was true of a corpus
  with one draft in it and a writer that was still writing CVs.
- **The MCP surface gets its own rate-limit policy**, not `RateLimitSetup.ReadPolicy`. A client
  polls differently from a browser and must not exhaust the budget the dashboard shares — and
  these tools read SQL, which is billed on wall-clock time against a monthly grant.
- **Read the caller from `RequestContext.JsonRpcRequest.Context.User`, not from
  `IHttpContextAccessor`.** The SDK populates the principal per message; an `AsyncLocal` may not
  survive the transport's async boundaries.
- **No tool takes a profile id.** A tool signature is an easier place to get this wrong than a
  route, because the argument is named by a model rather than by a router — an unused `profileId`
  parameter is precisely what a model would helpfully fill in.
- **An app-only client is mapped to a candidate by configuration, never by a tool argument.**
  A token from the client-credentials flow names software: its `oid` is a service principal's, it
  matches no profile, and every tool would answer "this candidate has no profile yet" against a
  pipeline that is full. `McpOptions.AppPrincipals` says whose pipeline such a principal acts on,
  which keeps the rule above literally true - the identity still arrives with the token, through
  an indirection an operator wrote and no caller can name. **Resolve it in the MCP feature and
  never in `CallerIdentity`**: the API has one authenticated policy and no per-scope
  discrimination, so an app role admits its holder to every route, and resolving the map centrally
  would let an unattended client act as the candidate across all of them instead of on six tools.
  An unmapped app-only token is told so specifically - "not finished deploying" and "has not
  filled the form in" produce the same empty answer and want opposite fixes.
- **Pin an authorization policy as endpoint metadata, not only as a 401.** Written after the
  behavioural version of that test turned out not to pin what it claimed: every handler also calls
  `CallerIdentity.TryGetSubjectId`, which answers 401 when the token carries no `oid`, so swapping
  `AuthenticatedPolicy` for `PublicReadPolicy` left the read cases green. Defence in depth working,
  and a test measuring the second layer while describing the first.

### Apply links, and where they come from

- **No account is used to read any board, and that is a decision with a record rather than a
  preference.** `mcp_handoff.md` 3.2 rejected authenticated LinkedIn and 3.2a says why the answer
  did not change when its own reopening condition fired: hiQ ended with LinkedIn winning on
  *contract* - an injunction, a judgment and the corpus destroyed - so a terms breach is actionable
  even over public data; the 2026 enforcement wave targets browser automation specifically, which is
  the shape an authenticated fetch takes; and there is no sanctioned route, because LinkedIn's Job
  Posting API is write-only and closed to new partners. If a change here seems to need a cookie, a
  session or a login, that is the signal the design is being worked around.
- **The employer will tell you, and their ATS is a documented public endpoint.** Greenhouse, Ashby,
  Lever and SmartRecruiters serve board listings unauthenticated, verified live against real tokens
  from this corpus. That is a service to *their* customers rather than to us: one fetch per board per
  pass rather than per posting - Cloudflare answers 333 jobs in one request - a descriptive
  User-Agent, a timeout, and probes bounded to employers that actually have blocked postings.
- **A 404 is an answer, not a fault.** It is how a probe fails, and logging it as an error makes the
  log noise nobody reads.
- **A board's identity is vendor, token, region *and* employer, and each part prevents a named
  failure.** Lever serves European tenants from a separate host and API, so folding
  `jobs.eu.lever.co` into `jobs.lever.co` asks the loser of an endpoint that answers nothing - which
  reads as "not on Lever" and is indistinguishable from the employer genuinely not being there, with
  no count that moves. Drop the employer and a token becomes globally unique, so one wrong probe of
  `orbital` blocks the company that can prove that token from a link they published.
- **A probed token is confirmed before it is trusted, and trust is a timestamp rather than a flag.**
  `ConfirmedAtUtc` set is the whole test, so no value nobody set can read as permission - and a
  learned board is stamped as it is learned, because the link the employer published *is* the
  confirmation, which removes the "learned boards are exempt" branch somebody would forget. Probing
  matters because slugs collide: `Dex`, `Kernel`, `Fin` and `Orbital` are all real boards owned by
  *a* company, not necessarily the one on the advert.
- **A recovered link never overwrites `JobUrlDirect`, and reaches a client as its own provenance.**
  That column is what the board itself published, and the whole argument rests on the two being
  distinguishable. `MatchedOnEmployerAts` ranks between a published link and one borrowed from a
  stranger's listing: the recovered link is real and the form opens either way, so nothing a browser
  sees separates a good recovery from a bad one - only the provenance can. Folded into `Posting`
  both its failure modes become invisible.
- **A posting is stamped as checked even when nothing matched.** Otherwise "no recovered link" means
  both "asked, nothing there" and "nobody has asked", the pass re-asks the same employer every night,
  and it is the same two-nulls-in-one-column fault `OffsiteApply` was added to undo.
- **The matcher abstains rather than guessing, for the reason the cross-board key requires a city.**
  An employer's board is a catalogue - several listings can match one title - and an abstention costs
  a posting that stays where it already was, while a wrong match is an application sent to the wrong
  vacancy. Title and employer alone matched 285 postings across boards and adding the city left 211;
  the same failure is available here and is worse, because this link is the one an agent opens.
- **Boards are ranked by the postings somebody is waiting on, not by the postings that exist.**
  The first pass ordered by how many blocked postings an employer had in the corpus, which sounds
  like the same question and is not. Measured on the 2026-09-08 run: 45 boards fetched, of which
  **5** were among the 22 that would have unblocked an applyable posting - corpus-wide recoveries
  went 29 to 175 while the applyable queue moved by 9. Nothing failed and nothing could have; every
  count was right and the pass reported exactly what it did. **A bounded budget spent top-down on
  the wrong ranking is a budget spent on employers nobody is applying to.** `BlockedForSomebody` is
  a sort key and never a filter: eligibility stays corpus-wide, so a link is still recovered for a
  posting no profile has matched - after the ones somebody is waiting on rather than instead of
  them. With 22 such boards against a 40-board pass, both happen.
- **SmartRecruiters has no 404, so "it answered" is not evidence there.** Verified live on
  2026-09-08: `api.smartrecruiters.com/v1/companies/{token}/postings` returns 200 with
  `{"totalFound":0,"content":[]}` for `jackandjill`, for `hunterbond` and for
  `definitely-not-a-real-company-xyz99` alike. `AtsBoardReadOutcome.NotABoard` is therefore
  unreachable for that vendor and every guess "answers" - which is why all twelve probed rows in
  the corpus are SmartRecruiters and none confirmed. An empty answer is still *recorded*, because
  the request was spent and the row is the memory that stops it being spent again, but it no longer
  counts as `Answered`: confirmation reads the company name off a listing, so an empty board cannot
  confirm however often it is asked, and counting it would put the Answered-to-Confirmed distance -
  the figure that argues for a name-bearing endpoint - permanently wrong.
- **The remaining backlog is recruitment agencies, and no ATS recovery reaches it.** Measured
  2026-09-08, the employers holding the most link-less applyable postings are Jack & Jill 29,
  VirtueTech 17, Noir 14, Client Server 7, Harnham 6, Saragossa 5, Harrington Starr 4, Hunter Bond
  4, Oho Group 4 - agencies advertising a client's vacancy under their own name. The employer on
  the advert is not the employer with the board, so probing them is sound and finds nothing, which
  is exactly what it did. **Quote the ceiling before promising a number**: the recoverable share is
  roughly the non-agency share of the queue, and in this corpus that is small. Dex (14) and AVEVA
  (4) are the real employers in that list.
- **Do not expect much from Workable.** Every Workable link in this corpus is
  `apply.workable.com/j/{code}`, which names no board, so those employers are reachable only through
  a probe. Workday has no clean public listing at all and is deliberately out of scope.

### Dashboard (`web/`)

- **`tokens.css` is a contract, not just a palette.** `chartTheme.ts` resolves
  `--series-1..4`, `--seq-250..650`, `--gridline`, `--axis`, `--text-muted`, `--surface-1`
  and `--text-primary` **by name**. Adding tokens is fine; renaming or shadowing one of those
  with a parallel vocabulary leaves the charts holding one theme's palette across a toggle,
  which is the exact failure `useChartTokens` was written to prevent and which nothing tests.
- **The motion budget is four things**: the nav marker, the drawer, an expanding entry, and
  chart marks on first mount. Everything animates from a state the DOM already rests in, so
  `prefers-reduced-motion` skips rather than shortens. **No count-ups on figures** - a page
  mid-animation and a page legitimately reading zero are indistinguishable, and the lede spent
  the first second of every visit saying the scrapers had found none.
- **Indicators move by transform, and are placed with `set` on first paint.** Animating
  `width` is a layout property once a frame; tweening from zero on mount leaves the selected
  label sitting on a zero-width pill, reading as white-on-white until the tween lands.
- **Routing is the History API over an in-repo router**, not a dependency:
  `staticwebapp.config.json` already rewrites navigation and 404s to `/index.html`. Page
  changes and opening a posting push; filter changes and walking the concept graph replace, or
  Back steps through every keystroke somebody typed into a search box.
- **Chart colours are validated, not chosen.** The categorical slots in
  `src/theme/tokens.css` clear CVD-separation and contrast gates in both modes. Never
  substitute a hex by eye - colour-vision separation is not something the eye can check, and
  the slot *order* is itself the safety mechanism. Add series by taking the next slot; a
  fifth series folds into "Other" rather than getting a generated hue.
- **Never add a dual-axis chart.** Two y-scales on one plot is the single most misleading
  thing a dashboard can do. Two measures of different magnitude means two charts, or one
  chart and one stat tile.
- **`PostedWithin` is one control shared by the shortlist and the corpus search.** Same option
  list, same words, one place to change when the cadence does - and it defaults to "Any time",
  because a list silently showing only today's postings reads as a quiet market rather than as a
  filter.
- **Never fetch metrics directly in a component.** Go through `MetricsFeed`. That interface
  is what lets the planned Web PubSub push replace polling without touching components; a
  component with its own timer is exactly the shape that cannot be converted.
- **Recharts cannot take `var(--token)` for fill/stroke.** `useChartTokens` resolves tokens
  to hex and re-resolves on theme change - both on the `data-theme` attribute and on the
  `prefers-color-scheme` media query, or a toggle leaves charts in the old palette.
- **Vite 8 builds with rolldown**, so `manualChunks` must be a function; the object form
  fails with "manualChunks is not a function".
- **Static Web Apps region is a trap like the SQL one.** `westeurope` refuses new customers
  on this subscription. `webLocation` is separate for that reason - probe before changing.
- **The client must never wait indefinitely.** Requests carry a deadline and raise a
  distinct `ApiTimeoutError`, which the UI explains as a waking database with a retry. A
  promise that never settles is a spinner with nothing to click - the worst failure mode
  this architecture can produce, because pausing is normal here rather than exceptional.
- **The dashboard's origin reaches the API's CORS list through the template**, from the
  Static Web App module's output. Do not hard-code it: the hostname is generated at creation.

### Deployment traps

Each of these cost a red CI run; none of them fail locally.

- **`host.json`'s `logLevel` does not configure the isolated worker, and nothing says so.**
  `ConfigureFunctionsApplicationInsights()` registers the Application Insights logger provider
  with a filter rule of its own, defaulted to Warning, and that rule is not reachable from
  configuration. So `"JobPlatform": "Information"` in host.json governs the *host's* copy of a log
  and every `logger.LogInformation` in the worker assembly is dropped before it leaves the
  process. The symptom is telemetry that looks fine: the host's own
  `Executing 'Functions.MatchSweepFunction'` pairs arrive under `Function.*`, so a query finds
  rows and a reader concludes logging works. Measured on 2026-09-09 over fourteen hours, no
  category beginning `JobPlatform` existed at all, and the nightly sweep's own account of itself -
  requested against written against discarded - had never been queryable. `Program.cs` removes the
  rule and then restates the levels where the worker can hear them, including the EF command
  logger at Warning: without that second half, removing the rule puts every SQL statement the
  sweep runs into telemetry billed by the gigabyte. `WorkerLogging` holds the removal and
  `WorkerLoggingFilterTests` asserts it, because the first attempt was written inline with the
  test asserting a copy of it.
- **Worker traces are categorised under `CategoryName`; host traces under `Category`.** A KQL
  query written for one finds nothing in the other and reads exactly like a logger that is still
  filtered - `traces | where customDimensions.Category startswith 'JobPlatform'` returns zero rows
  while the lines are sitting there under `customDimensions.CategoryName`. Add ingestion latency
  of a few minutes and a fix that worked looks like a fix that did nothing, which is what happened
  on 2026-09-09. Query on the message or on `CategoryName`, and give it five minutes before
  concluding anything.

- **Event Grid can refuse a dead-letter container ARM has just finished creating.** The
  subscription validates `deadLetterDestination` by reading the blob container, and that read
  goes through a different plane from the one that created it. The template is not the
  problem: `deadLetterContainer.name` *does* emit a `dependsOn` in the compiled ARM, which is
  worth knowing because the failure reads exactly like a missing dependency and the obvious
  "fix" of adding one changes nothing. Measured on 2026-09-02: run 33652768241 failed with
  `Deadletter destination not found`, and the identical deployment succeeded on re-run with
  nothing changed. `Deploy infrastructure` now retries once for this; an ARM deployment is
  idempotent, so the retry costs time and nothing else. If the retry also fails, the error is
  real - read it rather than running it again.

- **`readEnvironmentVariable`'s default does not fire in GitHub Actions.** An undefined
  `vars.X` becomes an environment variable that is *set and empty*, not absent, so the
  fallback argument is skipped and `''` reaches the template. `infra/main.bicepparam`
  therefore defaults with `empty(readEnvironmentVariable('X', '')) ? default : ...`. Follow
  that pattern for every new parameter, or a missing repo variable fails the deploy with
  BCP033 rather than taking the default.
- **`LangVersion` was `latest` and the SDK is not pinned, so the runner compiled a different
  language than the developer did.** There is no `global.json` and `deploy.yml` asks
  setup-dotnet for `9.0.x`, so a laptop on 9.0.304 compiled C# 13 while the runner compiled
  C# 14. `field` became a contextual keyword inside property accessors in C# 14, and a lambda
  parameter named `field` built clean locally and failed the deploy at `Publish function` with
  CS9273. `LangVersion` is now pinned to `13.0`, the version paired with net9.0 - raise it
  deliberately, with the target framework. **A green local build is only evidence if both
  machines compile the same language.**
- **Do not create a user in the API Dockerfile.** The .NET base images already ship a
  non-root `app` user and expose `APP_UID`; `useradd` fails the build with exit code 9,
  "username already in use".
- **`cache-to: type=gha` needs `docker/setup-buildx-action`.** On the default docker driver
  buildx aborts with "Cache export is not supported for the docker driver".
- **The container app cannot be deployed before its image exists.** The tag is referenced by
  the template, so `deploy.yml` builds and pushes the image in a job that the infrastructure
  job depends on. A missing tag fails provisioning outright, not just the revision.
- **The GHCR package must stay public** for the credential-free pull to work. It inherits the
  repository's visibility; if that ever changes, the app needs a registry credential and the
  no-secrets property is gone.
- **A push deploys the code but never the schema, and the gap is a broken endpoint.** The
  `Apply migrations` job is gated on `github.event_name == 'workflow_dispatch' && inputs.run_migrations`,
  deliberately - a schema change should not be a side effect of a code push. The consequence is
  the part to plan for: a commit that adds a column ships code selecting that column against a
  database that does not have it, and every query touching the table answers **500** until
  somebody dispatches the migration by hand. Measured, on the change that added `RankScore`:
  Deploy went green, the smoke test passed because it does not touch `/matches`, and `/matches`
  returned 500 for the eleven minutes between the two runs.
  **So a migration-bearing change is two steps, and the second is not optional:**
  ```bash
  git push                                        # deploys the code
  gh workflow run deploy.yml -f run_migrations=true   # then the schema
  ```
  Neither ordering is safe on its own - migrating first would have the old code running against
  a new schema, which is the better failure but still one. The real fix is for a schema change to
  be additive and for the code to tolerate both shapes; short of that, dispatch the migration
  immediately after the push and check the endpoint the change touches, because **the smoke test
  will not catch this.**
- **A resource provider namespace new to the subscription must be registered before first use,
  and the failure does not say so clearly.** Adding the SignalR resource failed the deploy with
  `MissingSubscriptionRegistration: The subscription is not registered to use namespace
  'Microsoft.SignalRService'` - nested four levels deep inside a `DeploymentFailed` whose outer
  message is the generic "at least one resource deployment operation failed", so it reads as a
  Bicep problem. It is not; the template was correct and deployed unchanged afterwards.
  `az provider register --namespace <Namespace>` fixes it, takes a couple of minutes, and is a
  one-off per subscription - which also means a fresh clone into a new subscription hits it for
  every namespace this repo uses that the subscription has not seen. Check
  `az provider show -n <Namespace> --query registrationState` before blaming the template.
- **`deploy.yml` has a `concurrency:` group, and it is load-bearing.** Without it runs are
  concurrent and land in whatever order the runners free up. During a GitHub Actions outage a
  Deploy for a superseded commit sat queued for hours while a newer commit deployed; had it
  started it would have rolled the container app back to the older image with nothing failing.
  The group serialises runs and cancels the *pending* one when a newer commit queues behind it.
  `cancel-in-progress` is false deliberately - a run already executing may be part way through
  a Bicep deployment or `dbadmin migrate`. Note the group is read from each run's own copy of
  the file, so adding or changing it cannot rescue runs already queued. The image tag is the
  full commit SHA, which is what makes a rollback detectable after the fact:
  `az containerapp list --query "[0].properties.template.containers[0].image" -o tsv`.

### API-specific

- **Never set `InvariantGlobalization`.** `Microsoft.Data.SqlClient` builds a `CultureInfo`
  from the database's collation when materialising string columns; in invariant mode that
  throws `CultureNotFoundException` and every SQL-backed endpoint answers 500. The SQLite
  tests never touch a collation, so this only shows up against the deployed database.
- **Read Entra claims under both names.** The JWT handler rewrites short claim names to
  legacy URIs, so `scp` and `name` are usually absent under the names the token carried.
  `oid` specifically must not fall back to `ClaimTypes.NameIdentifier`, which resolves to
  `sub` - pairwise per application, where `oid` is the stable directory object id.
- **The app registration is `scripts/setup-api-app.ps1`**, not Bicep: an Entra application
  has no ARM representation. It is idempotent and preserves the existing scope id, because
  changing that id invalidates every consent already granted.

- **The API must never serve dashboard metrics from Azure SQL.** They all exist in Cosmos
  already. SQL is billed on wall-clock time *online* against a monthly grant one daily ingest
  half-consumes; a polling dashboard reading SQL exhausts it and the database auto-pauses
  until the 1st of the next month. SQL is for posting browse/search/detail, behind output
  caching, and nothing else.
- **Nothing a client needs before its first real request may touch SQL.** `/search-terms` is
  the call every page waits on, so it is served from Cosmos. When it read SQL, opening the
  dashboard while the database was paused hung *every* page - the Cosmos-only overview
  included - behind a wake-up that logs SQL error 40613 for minutes. Adding a new bootstrap
  call means asking which store it reads before anything else.
- **No health probe may touch SQL**, for the same reason — a probe alone would keep the
  database awake permanently. `/health` touches nothing; `/health/ready` checks Cosmos.
- **EF cannot project a `GroupBy` straight into a positional record's constructor.** It
  compiles and then fails at runtime with "could not be translated". Project into an
  anonymous type and map afterwards — see `CountByAsync` and the daily-rollup aggregates.
- **Every read that is then mutated says `AsTracking()` explicitly, and it is not redundant.** It
  reads as a restatement of EF's own default, which is why it was left off: the API host once set
  `NoTracking` globally on the argument that this side only ever read from SQL, and under that a
  read-then-mutate **saves nothing and throws nothing**. Four write paths had been doing exactly
  that before anybody noticed, because the failure is a `SaveChangesAsync` returning zero on a page
  that reported success. Stating it at the query means a write does not depend on a line in a
  composition root a long way away, which is also the only version of this rule that survives
  somebody adding a second host.
- **SQLite cannot `ORDER BY` a `DateTimeOffset`.** `JobsDbContext.ConfigureConventions`
  converts to ticks under SQLite only, so the tests can exercise the real orderings; SQL
  Server keeps native `datetimeoffset`.
- **An age filter is a window in days, never a date the caller sends.** `postedWithinDays` on
  the shortlist, on the corpus search and on `list_applyable` is resolved against the server's
  `TimeProvider`, because "what is new" is relative to now: a browser minutes out of step, a
  bookmarked filter a day out, or a model client doing the subtraction itself all ask a different
  question from the one on the screen. A negative window is a 400 - a refusal, never a clamp -
  because it would otherwise return everything and nothing in the response would say the argument
  was dropped. It filters in the query, before `Skip`/`Take`, like every other filter here.
- **List responses must not carry `Description`.** It is unbounded `nvarchar(max)`; only
  `PostingDetail` returns it. `PostingEndpointTests` asserts this, because nothing else fails
  when it regresses.
- **`Api:AllowAnonymousReads` is the only switch that opens reads**, and it never opens
  `/me`. Do not make it depend on whether `AzureAd` happens to be configured — a mistyped
  section name would then silently publish the whole dataset.
- **There are three deployments and the embedding one is not on the Kernel.** A Kernel holds
  chat completion services and invokes prompts against them; an embedding returns a vector and no
  completion, so `AddAiProvider` registers `IEmbeddingGenerator<string, Embedding<float>>` on the
  container directly — the same Microsoft.Extensions.AI abstraction Semantic Kernel itself sits
  on, so no package and no credential is added. The SK helper that registers it is experimental
  and is suppressed at that one line, and it is also **hidden from extension-method lookup by
  that diagnostic**, which is why the call is written against an aliased static class rather than
  as `services.AddAzureOpenAI...`. Its parameter is `credentials:`, not `credential:`.
- **Semantic Kernel is the LLM abstraction, and the connector under it is first-party.**
  `AiRegistration.BuildKernel` registers *two* Azure OpenAI chat services on one Kernel, under
  the service ids `bulk` and `writing`, and a prompt names which one it wants through
  `ServiceId` on its execution settings. That is the only thing deciding whether a call costs
  Luna money or Sol money, and it fails silently if either registration is missing - Semantic
  Kernel falls back to the only service present rather than throwing, so a missing writing
  deployment means CVs quietly written by the cheap model. `AiRegistrationTests` asserts both
  resolve.
- **`AddAiProvider` registers a Kernel only when `Ai:Provider` is `azureopenai` *and* an
  endpoint is present**; anything else registers nothing rather than throwing, so a missing
  environment variable cannot take down endpoints with nothing to do with AI. There is no key
  to check for. `IDocumentExtractor`, `ICandidacyAssessor` and `IApplicationWriter` are all
  registered inside that same `if`, so consumers resolve them as **nullable** and skip their
  step - never as required.
- **Degrading silently is not the same as degrading invisibly, and this codebase has confused
  the two.** The rule above is right: a provider failure must not take down endpoints with
  nothing to do with AI. But every AI path here also swallows its failures without recording
  them, and it has cost real work three times - a nightly sweep that discarded five of nine
  batches while reporting success, a backfill that spent its calls on HTTP 429s and extracted
  almost nothing, and `Distribute` dropping misaligned answers to be re-extracted later by
  something nobody was watching. In every case the symptom was a count nobody was comparing to
  anything.
  **Every model call should leave a record, and a failed one should be visible to the user.**
  What was asked for against what came back, which deployment served it, and on failure a
  bounded reason plus the ids affected - never the prompt, which carries the candidate's
  profile. It belongs in Cosmos with the other metrics, never in SQL, for the reasons in the
  API section. App Insights is not a substitute: traces are sampled here and these failures
  throw nothing, so the record is incomplete exactly when it matters.
  **The mechanism is `IAiCallLog` and the `aiCalls` container**, read back through
  `GET /api/v1/ai-calls`. A new model call site reports to it the way
  `KernelCandidacyAssessor` does: an optional `IAiCallLog`, a `LedgerOperation` constant naming
  the pass, and the record written inside a `try` even though the interface says implementations
  must not throw - the cost of that comment being wrong is losing the work the call just paid
  for. `AiCallRecord.Create` is the only constructor, so the bounds on the reason and the id
  list cannot be skipped. See `HANDOFF.md` 1.1 for what is still unwired.
- **`AiPrompt` carries two settings that fail obscurely when wrong.**
  `SetNewMaxCompletionTokensEnabled` must be true or SK serialises `max_tokens`, which every
  GPT-5 series model rejects with a 400 on the first real call. And `Temperature` is left
  unset deliberately: reasoning models accept only the default and answer 400 for anything
  else.
- **`ReasoningEffort` is `low` for extraction and `medium` for assessment, and never `none`.**
  At `none` the model stops reasoning about whether a phrase means "essential" or "desirable",
  which is the one thing the deterministic pass cannot do and therefore the entire reason for
  calling it.
- **The queue's concurrency and the deployment's capacity are one setting in two files.**
  `host.json`'s `batchSize` + `newBatchThreshold` decide how many invocations run at once;
  each makes one model call at a time. Multiply those by the batch's token cost and it has to
  fit under the deployment's TPM. Left at 4/2 against a 100k-TPM deployment, the first real
  backfill spent its calls collecting HTTP 429s and quietly extracted almost nothing - the
  function swallows a provider failure by design, so the symptom was a stalled count rather
  than a red anything. Change either number and check the other.
- **There are two extraction paths and the split is along the data, not preference.**
  `IDocumentExtractor` is synchronous, packs documents, and serves anything a person is waiting
  for - profiles especially, where the alternative is telling somebody to come back tomorrow.
  `IBatchDocumentExtractor` submits to OpenAI's Batch API and is collected within 24h by
  `CollectExtractionBatchesFunction`; it serves job adverts, which are public text nobody is
  waiting for. Keeping profiles off it is what keeps personal data inside the tenant.
- **The batch path sends one document per request and must not be made to pack.** Packing is
  what forces the synchronous extractor to police returned indices, because a misaligned answer
  is wrong, self-consistent and undetectable. A batch API echoes a `custom_id` per request, so
  correlation is the platform's problem - packing would trade that guarantee away to save
  roughly a pound across the whole corpus.
- **Collection is bounded and resumable, because a corpus-sized batch outlasts an HTTP
  request.** Applying 2,459 results returned 504 three times at the gateway's ~230s before one
  got through; the work survived only because the writer is idempotent. The timer owns this
  path and gets a generous budget; the HTTP route is a nudge and applies at most a few hundred,
  leaving the batch open. What is "already applied" is asked of `PostingExtractions` rather than
  flagged on the item - that table's unique key is the definition of applied, and a flag would
  be a second copy of the fact, free to disagree.
- **A bound that can only act between pages is not a bound.** `BoundedWalk` is the reprocess
  endpoint's loop, kept apart from the function and free of Azure types so it can be asserted
  exactly - the same reason `MatchScorer` takes records rather than a database. Its first
  version read the clock only at a page boundary, because that is the only place the
  continuation token is accurate, and was therefore committed to a whole further page every time
  the check passed. Measured while re-enriching the corpus, pages of five blobs took 4s, 11s,
  12s, 47s and **151s** - one page outlasting the entire 150s budget - so a call that checked at
  149s ran to ~225s and the gateway gave up. **A 504 carries no continuation token**, so the
  caller loses its place and restarts the listing; that was survivable only because the writes
  are idempotent, which is the second time that property has covered for a bound that could not
  act.
  It now stops *between items* and hands back the token for the start of the page it was in, so
  the resume point is still a real boundary and nothing is skipped - at the cost of redoing that
  page's finished items, which idempotency makes free. It will not stop before one page has
  completed: bailing out of the first page hands back the token the call arrived with, and the
  next call would stop in the same place forever. The residual limit is pinned in a test - a
  single item slower than the whole budget still overshoots by its own duration, and the margin
  to the gateway's ~230s is what absorbs it.
- **`ExtractionBatchItems.InputHash` is captured at submission, never recomputed.** A batch is
  answered up to a day later and the scraper may have re-listed the posting with edited text in
  between. The extraction row has to be keyed on what was actually read, or the idempotency key
  lies and the next backfill does the wrong thing in either direction.
- **`keyVaultReferenceIdentity` must name the user-assigned identity.** Key Vault references
  resolve against the *system-assigned* identity by default and this app has only a user-assigned
  one; without it the setting is left as its literal `@Microsoft.KeyVault(...)` text, which the
  application then sends to OpenAI as an API key.
- **Extraction sends many documents per call, and the index is checked rather than trusted.**
  The concept vocabulary is several thousand tokens and has to precede every extraction, so
  sent per posting it dwarfs the adverts themselves; ten to a call amortises it tenfold. The
  cost of that is a new failure mode - an answer landing against the wrong posting would be
  wrong, self-consistent and undetectable afterwards - so `KernelDocumentExtractor.Distribute`
  drops any out-of-range or duplicated index rather than clamping it, and the affected postings
  are simply re-extracted by the backfill. `DocumentExtractorTests` pins reordering, duplicates,
  short responses and out-of-range indices.
- **The Azure OpenAI connector can express JSON mode**, which the provider-neutral settings
  could not, so `AiJson.ExtractJsonObject` is now a net rather than the normal path. It stays,
  and stays tested: a response format is a request to a provider, not a property of the
  transport.
- **`Microsoft.Extensions.*` is pinned to 10.x on a net9.0 target**, because Semantic Kernel and
  its Azure OpenAI connector require `Microsoft.Extensions.AI` 10.5. With transitive pinning on,
  dropping these back to 9.0.0 fails the build with CS1705.
- **`TreatWarningsAsErrors` is on.** It was off for the experimental SK/Anthropic bridge, which
  no longer exists. The one experimental API still used is suppressed at the single line that
  needs it in `AiPrompt`, so the next experimental API somebody reaches for still fails the
  build.

### Realtime

- **The Realtime row of `../model.md` is built, and it is the last one that was not.** A Cosmos
  change-feed trigger over the `aiCalls` container pushes failed model calls to the dashboard
  through Azure SignalR. It exists because every AI path here degrades silently by design and
  that cost real work three times: the ledger made those losses readable afterwards, this makes
  them visible while the 03:00 and 03:30 passes are still running.
- **Serverless mode, and the mode is not a detail.** Default mode expects an ASP.NET Core app
  hosting the hub and holding connections; Serverless is the one where everything reaches the
  service over its REST API, which is the shape here. Get it wrong and negotiate returns a URL no
  hub is listening on — a client that connects and never hears anything.
- **`ServiceTransportType.Transient` for the same reason.** The default, Persistent, opens a
  websocket back to the service and holds it, which on Flex Consumption means one opened and
  abandoned per invocation — against a free tier capped at 20 connections that exhausts the quota
  the dashboard's own clients need.
- **Negotiate lives on the API, not the Function app**, even though the Functions SignalR binding
  is what every serverless sample uses. A Function route is protected by a function key, and a
  browser holding one holds a credential that also opens reprocess, backfill and sweep. The
  dashboard already carries an Entra token for the API. It is behind `AuthenticatedPolicy` and
  **must never move to `PublicReadPolicy`** — `Api:AllowAnonymousReads` would then let anyone mint
  tokens against a service with a connection budget. `RealtimeEndpointTests` pins that.
- **On Flex Consumption the Cosmos trigger runs in its own function group, and a cold one takes
  minutes.** Per-function scaling means each instance serves one group, so an instance handling
  HTTP logs `Stopped the listener 'FunctionGroupListenerDecorator+NoOpListener' for function
  'AiFailureFeedFunction'` - which reads exactly like a broken trigger and is not. Measured on
  2026-08-31: a document written at 14:10:56 was delivered at 14:13:48, about three minutes, while
  the scale controller started an instance for that group. Warm it is seconds.
  **So "live" means within a few minutes of a cold start, not instantly**, and the dashboard's
  tail should never be described as instantaneous. Check for invocations before concluding the
  trigger is dead: `requests | where name contains 'AiFailure'`.
- **Failures only on the wire.** The container carries every successful call too, and the free
  tier allows 20,000 messages a day — one per successful extraction would exhaust it on a single
  backfill and take the failures down with it.
- **The notification is a projection, never `AiCallRecord`.** That type carries an optional prompt
  holding employment history and salary expectations; the three guards keeping it off the list
  endpoint would have to be reproduced to keep it off a socket. `AiFailureNotice` has no field for
  it, so it cannot leak one.
- **The feed broadcasts, and the next consumer is where that stops being free.** `PublishAsync`
  sends to every connected client, which is correct for an AI failure - that is a fact about the
  system - and wrong for anything about a person. `NegotiateAsync` already takes a `subjectId` and
  passes it through unused, so a per-user send is a small addition rather than a redesign, but it
  *is* an addition: the questions channel in `mcp_handoff.md` section 1.4 was planned on the
  assumption that reusing this transport was free, and it is not. Sending one candidate's question
  down the existing channel would deliver it to every signed-in dashboard.
- **`MetricsFeed` stays polling, deliberately.** It was built as the seam for exactly this
  conversion, but its data changes once a day when the scraper runs, so a socket there would
  mostly deliver silence — and the honest "polling" label the UI shows would become a lie. A
  failed model call is an event; a daily rollup is not. Different data, different transport.
- **The feed is optional everywhere.** No `Realtime:ServiceUri` registers nothing, `IRealtimeFeed`
  resolves null, the trigger returns early, and the negotiate route answers 503 rather than 500 —
  "not here" invites a fallback, "broken" invites a retry loop.
- **`CosmosFeed__*` is a second connection name on purpose.** `Cosmos:*` is plain configuration
  read by `CosmosOptions` for the SDK client; the trigger binding wants a settings *group*
  (`__accountEndpoint`, `__credential`, `__clientId`). One name serving both would make a change
  to either silently reinterpret the other.
- **The `leases` container is provisioned by Bicep and `CreateLeaseContainerIfNotExists` is
  false.** Created by the extension it would arrive with its own throughput charged against the
  free tier's 1000 RU/s. `LeaseContainerPrefix` is set so a second change-feed function later
  cannot steal these checkpoints and leave the two taking turns missing documents.

### Matching

- **The arithmetic runs on everything; the model runs on what survives it.** A corpus-wide pass
  is tens of thousands of pairs and almost all are obvious rejections. `MatchScorer` is pure and
  Azure-free like `MetricsCalculator`, and `MatchScorerTests` asserts exact numbers because of
  it. Changes to the weights belong there with a matching assertion.
- **Silence drops an axis rather than failing it.** Most postings state no salary, many state no
  arrangement, 18% of titles carry no seniority. An axis the posting cannot answer contributes
  nothing to the numerator *and* nothing to the denominator, and `MatchComponent.Weight` records
  what each carried for that pair. Scoring silence as zero would rank a posting that says
  nothing below one that says something incompatible; scoring it as full marks would make
  vagueness a competitive advantage. **A client rendering a zero-weight axis as a zero score is
  showing a penalty that was never applied.**
- **Silence has a floor, and it was missing.** The rule above bounded the numerator and the
  denominator but not how little could remain, so a posting with no readable requirements,
  scored on the city it was in, came out at 100 - against the real corpus, 44 of the top 60
  matches had no skills axis at all. A posting answering neither concept axis now scores zero,
  and `MatchResult.Coverage` reports the share of nominal weight answered. **Coverage is
  reported, never multiplied into the score**: a terse posting whose stated skills are all met
  is a genuine 100. Coverage is recomputed from the components on read rather than stored - it
  is a pure function of them, so a column would be a second copy that could drift.
- **Counting string-matched concepts was tried as a floor and withdrawn. Do not reintroduce
  it.** "Home Delivery Driver" scored 94 on one Taxonomy hit - the word "containers", in an
  advert about delivering physical ones - which the candidate's Kubernetes implied. The
  apparent fix was to require several such hits before they could carry a score. Measured
  against the corpus it removed one bad match and four good ones: `.NET Developer` rests on
  exactly one Taxonomy hit too, and no threshold separates the two. The count is not the
  signal.
  What fixed it was the vocabulary. `containers` is `ambiguous` rather than an alias, so that
  advert now resolves to nothing, records a mention instead, and fails the version 2 floor on
  its own. **A bad assertion is a vocabulary bug; fix it there, not in the scorer.** The
  unresolved-mention log is how the next one is found.
  `MatchResult.CurrentVersion` remarks carry the same history, and `HANDOFF.md` has the
  measured before/after.
- **The concept floor asks what the demands are, not how many.** A concept that cannot
  discriminate cannot carry a match by itself: `tagOnly` concepts and every domain. "Agile",
  "cloud", "api" and a board's `area.*` tags appear on adverts for every kind of job, so a
  posting whose stated requirements are all of that kind has not said what the job is, and
  meeting them says nothing about whether this candidate fits. The scorer reads
  `Concept.IsDiscriminating`, which is the vocabulary's own `tagOnly` flag plus domains - the
  same judgement already made for resolution, read one layer later, so the two cannot drift
  apart. `skill.agile` joined `tagOnly` with this change.
  Measured against the corpus: eight matches left the top 60 and every one was correct - two
  Transformation Managers and a Senior Product Manager scoring 92 to 100 on the single word
  agile, a Space Data Engineer at 100 on one board tag reading "Data Engineering" - and no good
  match went with them. That ledger is the point, against 1.4's one bad and four good.
- **Two other ranking rules were measured against the corpus and rejected. Do not reach for
  either.** Damping the score by `Coverage` is the one that looks obvious, and it punishes the
  employer's terseness rather than the thin evidence: it dropped a `.NET Developer` with twelve
  concepts read out of the top 60 for stating no salary, while keeping a Product Manager that
  answered every peripheral axis on one word. Damping by the number of demands, `n/(n+k)`,
  repeats 1.4 exactly - it removes "Yardi Implementation Consultant" (100, one concept) and
  "Senior Software Engineer - C#" (100, two concepts) together - and it ranks by how long an
  advert is, which is a fact about the recruiter. **Neither how much a posting said nor how
  much of it you can answer is the signal; which concept carried the match is.**
- **A widely-held skill on a role from another field still scores 100. No rule over the concept
  axes fixes it, and the fix is not to try.** "Yardi Residential Implementation Consultant"
  requires SQL, genuinely, and a SQL-holding candidate genuinely meets it. `skill.sql`
  discriminates - most postings naming it do mean an engineering role - so the floor above
  correctly leaves it alone. What separates them is what the *role* is, and that is not a fact
  the assertions carry.
- **So the list is ordered by something other than the score, and that is `MatchRanker`.**
  The score inverts inside its own top band: measured against the model's judgement, 90-100 holds
  a higher share of Weak verdicts than the bands below it, and the score's correlation there is
  **-0.051** on fresh labels, with an interval containing zero. A `text-embedding-3-small` cosine
  between the profile document and the advert is **+0.520** in exactly that band, interval
  excluding zero. That is the finding, it replicated out of sample, and it is what the ranking is
  for.
- **The corpus-wide claim took three cohorts to settle, and it now holds.** In-sample on the 194
  labels α was fitted to, the ranking beat the score by +0.123. On the 154 labelled afterwards it
  managed only **+0.045, CI [-0.015, +0.101] - not significant**, which is why this rule used to
  read "better at the top, not established as better overall". On 2026-09-02, with 151 labels
  clean for both α and the floor, it beats the score by **+0.129, CI [+0.012, +0.248] -
  significant**, jackknife-stable, and the floor is vindicated at 75/80/85 on data it was not
  fitted on. **The ranking is better overall.** Any figure quoting +0.521 or 68.5% is the
  in-sample one and is superseded.
- **What has *not* held still is which band the embedding earns its weight in, and the design's
  explanation depends on it.** Two cohorts put it in 90-100, where the score is flat. The
  2026-09-02 cohort puts it in **80-89** (+0.456, excluding zero) and has it slightly *negative*
  in 90-100 (-0.109, containing zero, n=31). That is weak evidence rather than a refutation, but
  it means **the mechanism must not be described as "the embedding rescues the top band" without
  saying the latest cohort disagrees about which band that is.** What is stable across all of them
  is that the two signals are complements. Re-measure the bands before re-tuning α; do not assume
  90-100.
- **The score is untouched, deliberately, and this is the part not to "simplify".** Folding the
  embedding into `MatchResult.Score` would clear every stored assessment - a moved score is the
  signal that a judgement was made against different arithmetic - and would therefore destroy the
  labels the weight was fitted on. `RankScore` is a separate column with a separate version
  constant, and a moved rank clears nothing.
- **The fusion is floored at 80, because below it the embedding is noise.** Per band, out of
  sample, the embedding's interval excludes zero only in 90-100 and the score's contains zero only
  in 90-100 - they are near-perfect complements. At the shipped floor of 45 the embedding was
  therefore taking 0.6 of the weight across 45-79 while contributing nothing, which is why the
  whole-range gain was not significant. Re-run at several floors, every value from 70 to 92 beats
  the score significantly and 45 does not; 80 is chosen from inside that range because it is the
  boundary the original research already named ("the top two bands"), not because it is the
  argmax - taking 70 would be fitting the floor to the data meant to test it.
  The floor also still does its original job: fusing globally would let a posting the concept
  floor scored at zero climb on textual resemblance alone, the failure that once put 44 of the
  top 60 matches there.
- **`MatchRanker.FusionFloor` and the sweep's `AssessmentThreshold` are not the same constant,
  and briefly making them one was a mistake.** They answer different questions: the threshold is
  where a model judgement is worth buying, which is deliberately low because that is where the
  arithmetic might be wrong; the floor is where the embedding earns its weight. Coupling them
  would have stopped the model from ever assessing below 80 - and with it the only source of
  labels that can show whether the score works down there. Two constants that happened to share
  a value are not one constant.
- **`RankScore` is an ordering key, not a score, and no client may display it.** It is min-maxed
  over one profile's eligible pool, so it is not comparable between candidates or between nights,
  and the top of any pool is always exactly 100. `Similarity` is the durable half - the same pair
  gives the same cosine in any pool - which is why both are stored rather than only the derived
  one. It is rounded to two decimals so that an unchanged night writes no rows; at full precision
  a scrape widening the pool nudges every key and the sweep's skip-the-write test never passes
  again.
- **Silence drops the embedding axis too.** A posting the pass has not reached ranks on its score
  alone rather than on a similarity of zero - absence of a vector is a fact about the queue, not
  about the job - and an axis that does not vary across the pool is dropped for the same reason.
  With no embeddings at all the ranker returns the score unchanged, so a deployment with no AI
  provider gets exactly the ordering it had before.
- **`EmbeddingText` is the one place that decides what gets embedded, and it has to be.** A
  cosine between a profile and an advert means nothing unless both were built by the same recipe,
  and the two are built by different code - a corpus pass over SQL rows on one side, the sweep on
  the other. Changing anything in it is a `EmbeddingVector.EmbeddingVersion` bump, exactly as
  changing model would be.
- **Staleness for a vector is decided without reading a description.** `PostingEmbeddings` copies
  the posting's `ContentHash` and `DescriptionLength` - the same two signals
  `JobPostingRepository.HasMaterialChange` uses - so "what needs embedding" is a join over short
  columns rather than a nightly pull of every unbounded advert. The profile side reuses
  `ExtractionInputHash`, which is already a hash of the exact text embedded.
- **The judgement layer is still the half that knows what a role *is*.** The ranking makes the
  top of the list better; it does not make a Yardi consultant into an engineering job.
  `ICandidacyAssessor` remains the answer to that, and its verdict is what the two numbers exist
  to be checked against.
- **`AssertionPolarity.Unspecified` is weighted as preferred, not as required.** It is by far the
  most common polarity - only the model pass can tell essential from desirable and it has not
  necessarily run - so treating it as a hard requirement would score most of the corpus at zero.
- **Both verdicts are stored and neither overwrites the other.** The score says how much of the
  posting the profile covers; the model's verdict says whether the rest matters. They disagree
  fairly often and the disagreement is the informative part: a 58 the model calls strong is
  precisely the posting worth surfacing. A re-score that moves the number clears the assessment,
  because the judgement was made against different arithmetic; a re-score that does not move it
  leaves the assessment alone, because paying for it again buys nothing.
- **The HTTP sweep and the timer sweep cannot share an assessment budget.** A trigger gets
  roughly 230 seconds and the timer gets minutes; forty pairs at raised reasoning effort does not
  fit the first. The sweep that followed full extraction was cut off before the model ran at all,
  leaving every verdict null with nothing saying why. Scoring is deliberately *not* bounded the
  same way - it is arithmetic over rows already in memory, and stopping half way would rank a
  profile against an arbitrary subset, which is worse than not ranking it.
- **The nightly assessment budget is split 30/10, and the ten are not for the candidate.**
  Selecting top-down by score is right for the product and produces labels that describe only the
  top of the range - three consecutive nights returned 92-100, then 89-100 - which is the same
  pooling bias that made the score look anti-correlated at -0.198 when it is really +0.31, built
  into the mechanism that produces the evidence. So ten of the forty are drawn across 45-59,
  60-69, 70-79 and 80-89 instead. **It costs nothing**: they are merged into the same batches, and
  the assessor sends the profile once per batch, so they cost ten adverts' worth of tokens rather
  than a second pass. A band-bounded request stratifies nothing - it is already a sample, and
  adding rows from outside the requested band would silently corrupt a hand-drawn draw.
  `StratifiedShortlist` holds the merge, kept pure and tested exactly for the reason `BoundedWalk`
  is: the round robin and the deduplication are the parts that are quietly wrong. In particular a
  band whose next row is already in the shortlist **advances rather than forfeiting its turn** -
  collisions land almost entirely on 80-89, because that is the band the shortlist is drawn from,
  so forfeiting would under-sample the band nearest where the ranking acts.
- **Two thirds of the shortlist is reserved for postings from the last three days, and it is a
  reservation rather than an ordering.** The whole pipeline is built around a day - one scrape,
  one sweep, one apply run - and an application sent a week after the advert went up is competing
  against a shortlist the employer has already drawn. Top-down by score alone does not deliver
  that: pairs above the threshold accumulate, the highest are judged first whatever their age, and
  a corpus with a backlog spends every night on the backlog. That is the state a first sweep over
  forty-five days of postings begins in.
  **Sorting by age instead is the obvious fix and is worse.** Age says nothing about whether the
  candidate fits, so a fresh posting scoring 46 would outrank a three-day-old one scoring 97; and
  it is absorbing - once the daily arrivals exceed the budget, nothing older is judged again,
  ever. So the budget splits, exactly as it already splits for the measurement sample: the recent
  draw is ordered by score like every other, it is capped, and whatever it cannot fill goes back
  to the top-down draw over the whole corpus. A quiet day costs nothing and the backlog still
  drains at the remaining third a night. `MatchSweepShortlistTests` pins all four of those.
- **Recency is a claim on the budget and never on the match.** No score, ranking, threshold or
  fusion floor reads a date; the arithmetic cannot tell a posting's age and is not meant to. A
  recency term in `MatchScorer` would also clear every stored assessment the moment it moved, for
  the reason the embedding is kept out of `Score`. The window decides which rows are *eligible*
  for the reserved share - it is not a way past `AssessmentThreshold`, and a fresh posting the
  arithmetic rejected stays rejected.
- **How old a posting is has exactly one definition, and it is `PostingAge`.** The board's stated
  `DatePosted` where it published one - two postings in five - and `FirstSeenUtc` where it did
  not. Believing only the stated date would hide most of the market; believing only first-seen
  would let a search term added this week deliver three-week-old adverts as today's work. Never
  `LastSeenUtc`: a re-scrape moves it on every live posting, so a window over it returns
  everything, every day. The rule is written out twice more, over postings and over matches, for
  EF to translate, and `PostingRecencyTests` asserts both against the pure one.
- **The sweep is a timer, not a page load.** `MatchSweepFunction` runs at 03:30 UTC, after the
  ingest and extraction queues have drained. A shortlist that costs model calls to look at is one
  nobody can afford to browse. `run-match-sweep` exists for the case the timer cannot serve -
  somebody who has just filled in their profile - and is a Function-key admin route deliberately.
- **The scoring pass needs no model at all.** `MatchSweepFunction` is registered
  unconditionally and `ICandidacyAssessor` is the nullable half, so a deployment with no AI
  provider still produces ranked matches - just without the judgement layer.

### Profiles and generated documents

- **A form, not an uploaded CV. The CV library does not reverse this, and it will be read as
  though it does.** Parsing a PDF back into structure is a lossy guess at what the person already
  knows, so **the profile remains the only source of truth for matching and nothing parses a CV
  back into it** - not the form, not `ProfileConcepts`, not a score. What changed is only which
  document the candidate's CV is: it used to be an *output* of the profile written by a model, and
  it is now one of a handful of markdown variants the candidate wrote themselves. A variant is a
  *sendable artefact*, never an input, and it is read for concepts that go somewhere else
  entirely. Stated here rather than left to be re-derived, because the next reader who finds the
  library and this rule on the same page will otherwise conclude one of them is stale and remove
  it. The genuinely new guard is in **The CV library** below.
- **`CandidateProfileRepository` takes a subject id and never a profile id.** That is the
  authorisation boundary expressed as a type: there is no overload an endpoint could hand a
  route parameter to, so a stranger's employment history cannot be read by mistake.
  `ApplicationDocumentRepository` follows the same rule. Read `oid` through
  `CallerIdentity.SubjectId`, never `ClaimTypes.NameIdentifier` - that resolves to `sub`, which
  is pairwise per application, so a profile stored under it is orphaned by a second app
  registration.
- **A save is a replace, not a merge.** A partial update cannot express "delete the third job",
  which is a thing people do. Concept rows survive it, because they are derived from the text
  rather than submitted with it.
- **`ExtractionInputHash` is computed from `ToDocument()`, not from the whole record.** A profile
  is saved repeatedly while somebody edits it and most of those saves change a phone number. Only
  a change to the text the extractor actually reads should cost a model call and invalidate
  scored matches.
- **Only `AssertionSource.Model` rows are replaced by an extraction.** Declared skills are the
  candidate's own structured claim and are stored under `AssertionSource.Board` - the supply-side
  equivalent of an employer's tagging - and the model has no business overwriting them, exactly
  as it does not overwrite a board tag on the posting side.
- **`ProfileConcepts` mirrors `PostingConcepts` column for column, `Source` in the key
  included.** That is the payoff of a shape fixed before there was a profile: matching is a join
  between two tables of identical shape. Do not let them drift.
- **The demand and supply halves of `AssertionPolarity` are numerically separated on purpose.**
  `Required` is 3 and `Expert` is 13, so a demand value stored in a supply column compares as
  weaker than every genuine claim and quietly deflates the match. Both the API mapping and the
  repository clamp rather than store.
- **The profile endpoints read and write Azure SQL, which is otherwise reserved for posting
  browse and search.** That is deliberate and bounded: fetched once when the page opens, written
  when somebody presses save. It must never become a polling path, and **nothing here may join a
  client's bootstrap sequence** - the reason `/search-terms` is served from Cosmos.
- **No output cache on any per-principal route.** A shared cache keyed on a URL with no user in
  it is exactly how one person is served another's record.
- **Generation requires an existing match.** The writer is handed the gap list as the set of
  claims it must not make; a document written without one has nothing stopping it from inventing
  the skills the candidate lacks. Refusing to generate for an unscored posting is what keeps that
  guarantee real. It now covers the covering letter and the drafted answers rather than a CV,
  which narrows what a bad generation can cost without weakening the rule - a letter that invents
  a year of Kubernetes still falls apart in the interview.
- **The markdown is the record; the PDF is rendered per request.** Storing the PDF would mean a
  layout change could not reach documents already generated, and would put megabytes into a
  database billed by the second. **A CV variant is the deliberate exception and the reasons
  invert**: its bytes were uploaded into somebody else's system, so "what exactly did we send
  them" is a question about a file that has to still exist and still hash the same. It is rendered
  once to blob storage and hashed; a template fix reaches it by re-rendering, which is free and
  deterministic, rather than by silently changing what a stored hash describes.
- **`MarkdownPdfRenderer` walks a parsed AST and emits from a fixed set of node types.** There is
  no HTML step and nothing the model returns is ever interpreted as markup. An unmapped node
  renders as its plain text rather than being dropped - silently losing a node would take content
  out of a document somebody is about to send to an employer.
- **An embedded font file is only what its `name` table says it is, and all four were wrong.**
  `Roboto-Regular.ttf` held Roboto **Bold**, `Roboto-Bold.ttf` held Regular, and the italic pair was
  swapped the same way - verified by reading the `name` table out of each file. `EmbeddedFontResolver`
  maps a face to a *file name*, so every CV this system rendered set its body text in bold and its
  bold headings in regular, and moved every line break with it. **Nothing downstream could see it**:
  PDFsharp embeds whatever bytes come back and MigraDoc measures them, so the document is correctly
  typeset in the wrong font - self-consistent, valid, the right length, the right words.
  `EmbeddedFontResolverTests` asserts each face's own `name` table rather than a filename, a byte
  length or a hash, because the only authority on what a face is, is the face.
- **`Inline.ToString()` is not the text of an inline, and the fallback that assumed it was put
  framework internals on people's CVs.** Markdig overrides `ToString` on `LiteralInline` alone, so
  the walkers' documented "anything unmapped keeps its text" default emitted a .NET *type name* for
  everything else: a CV writing `R&amp;D` rendered `R Markdig.Syntax.Inlines.HtmlEntityInline D`, and
  a code span inside `**bold**` rendered `Markdig.Syntax.Inlines.CodeInline`. The nested walkers -
  the `FormattedText` and `Hyperlink` overloads - are the ones to check when adding an inline type,
  because the top-level switch handled `CodeInline` and `LineBreakInline` and neither of the other
  two did. **An unmapped node now emits nothing**: the content-bearing inlines are all handled, and
  a type name in a document somebody sends to an employer is worse than a gap. The DOCX renderer
  handled all three correctly from the start, which is why comparing the two outputs would not have
  found it - only reading one back would, and every test here asserted that rendering did not throw.
- **The PDF draws H2's grey rule under H3 and H4 as well, and the DOCX draws it only under H2.**
  MigraDoc's built-in `Heading3` is based on `Heading2`, and `Heading4Local` is based on `Heading3`,
  so the border set at one style inherits down the chain - confirmed by decompressing a probe
  render's content stream and counting three stroked lines for one H2, one H3 and one H4. The DOCX
  writes `ParagraphBorders` inside the Heading2 style alone. **This is a live divergence, not a
  fixed one**: it is a visible design decision about what a CV looks like, so it is written down
  rather than changed. It costs roughly 4.7pt per third- and fourth-level heading, which is where
  PDF and DOCX page counts drift apart even when both machines have Roboto installed.
- **PDFsharp's platform-independent build resolves no fonts at all** and throws on its first call
  without a resolver, including for its own internal error font. `EmbeddedFontResolver` embeds
  Roboto (SIL OFL 1.1) and resolves *any* family name, because MigraDoc asks for "Courier New"
  whatever the document says. Installing fonts in the container was rejected: it renders
  differently on a developer's machine and turns a missing apt package into a 500 on somebody's
  CV download.

### The CV library

The CV stopped being written per posting and became a library of markdown variants the candidate
authors; a pass chooses one, and where none fits the candidate is told what is missing rather than
sent the nearest. What follows is what that change established, each rule with the failure it
prevents. It exists because of a sentence: asked what else an employer should know, the writer gave
the candidate's citizenship - correctly, out of their own summary - and added *"I am an AI and they
should have seen this."* A guard now drops that class of sentence, and a guard is a net under a
trapeze. **A curated CV takes the model out of the document that matters most**, because a model
that is not writing the CV cannot invent a claim in it.

- **A variant's concepts feed selection only.** They *are* extracted - that is how selection works
  at all - and they must never reach `ProfileConcepts`, never move a match score, and never widen
  what the candidate is judged to have. A CV is written *from* the profile, so letting its reading
  back in would let a document inflate the record it came from, and the loop would then apply to
  jobs on the strength of its own prose. **The guard is four structural refusals rather than a rule
  anybody has to remember**, and each is worth knowing before it is "simplified".
  `CvVariantConcepts` is its own table, so which rows are the candidate's qualifications is
  answered by which table you are reading; `CvVariantExtraction` is a type that
  `CandidateProfileRepository.ApplyExtractionAsync` cannot be handed, where a third `DocumentKind`
  would have produced the exact object it accepts; `CvVariantFacts` carries concept keys
  **without their polarity**, so a document that describes itself emphatically cannot outscore one
  that mentions the same work plainly; and no field on
  `CvSelection` carries a concept a *variant* asserted - the missing set is what the **posting**
  asked for. A convenience returning a `DocumentExtraction` from the variant path undoes all four
  at once, and nothing would fail.
- **A page shows the CV that was *chosen*, and asking which one is a route of its own.**
  `ApplicationDetail.CurriculumVitaeMarkdown` is null for every draft written since the library
  replaced per-posting generation, and the dashboard went on rendering it - so the shortlist showed
  an empty box under a "CV" heading beside a perfectly good cover letter, which reads as a document
  that failed to write rather than as the design. `GET /matches/{postingId}/cv` answers instead,
  and the client branches on the null rather than rendering it: a draft old enough to carry its own
  CV still shows it, because an application somebody received has to stay explicable.
  **That route runs the selector and stops there.** `get_submission_pack` does two more things with
  the same selection - it puts a genuine tie to a model, and it parks a posting nothing fits -
  and neither may happen because somebody expanded a row: one spends money on a route a client can
  call repeatedly, the other puts a posting down without anybody deciding to. So a tie is reported
  as a tie, `decidedBy` is never `model` from the dashboard, and `CvChoiceEndpointTests` asserts
  that a NoFit writes no submission row at all.
  The demands both surfaces choose against come from `CvVariantSelector.DemandsOf` rather than from
  a second read of `PostingConcepts`, so the CV is chosen against exactly the requirement set the
  breakdown on the same page was computed from - and the two surfaces cannot disagree about the
  same posting on the same day.
- **No route, tool or scheduled pass may rewrite a variant's markdown with a model.** The prose in
  `CvVariants.Markdown` is the candidate's, and that is the whole of what this feature buys.
  Staleness is the pressure that will come for this rule - a variant is a file that ages while the
  profile moves, and "regenerate the ones that fell behind" is exactly the shape of a helpful
  automation. So `CvVariant.PredatesProfileUpdate` *reports* and nothing acts on it, and
  `CvVariantStaleness` has no field for a suggested action, deliberately: a summary carrying a verb
  is how a scheduled pass acquires one six months later. The correct response to a stale CV is a
  sentence on the page the variants live on - the same answer the apply loop's answer TTL gets.
  Tell the person, and let them decide whether the change was one their CV needed to mention.
- **The library caps at six, counted over unarchived rows only.** Six rather than the spec's eight
  because the number that matters is how many CVs a person will keep *current*, not how many they
  will happily create: one profile edit puts every variant behind the profile on the same
  afternoon, and at eight what happens is that two get updated and six quietly rot - where a stale
  variant looks exactly like a current one in the picker, and the system will send it. The counting
  rule is the load-bearing half. **Archived variants do not occupy the cap**, because archiving is
  not deletion here - `Submissions.CvVariantId` names the row and an application made last year has
  to stay explicable - so a cap counting them would make the seventh rewrite of a CV impossible
  until somebody erased a file an employer was actually sent. That trades an auditable history for
  a row count, and the history is the more expensive of the two.
  `CvVariantLibrary.HasRoomForAnother` is where that reading lives so no caller has to remember it,
  and a library already over the cap answers "no room" rather than throwing - lowering the constant
  leaves every library above it above it, which is nobody's mistake.
- **One filename for every variant, and the path carries the rule rather than the header.** Every
  application uploads `Firstname_Surname_Curriculum_Vitae.pdf` - and `.docx` - whichever variant
  was chosen. The reason is not tidiness: `Pablo_De_Groot_AI_Engineer_CV.pdf` tells an employer
  that a different CV is kept for other roles, which is a true fact they have no business being
  handed, and it arrives in the file list before anybody opens the document. Nothing in the loop is
  choosing to disclose it, so nothing in the loop would notice it being disclosed. The stable name
  goes at the **end of the blob path** and not only in `Content-Disposition`, because the loop
  fetches a signed URL and hands a local path to a browser's file input: a client that saves the
  URL by its path and ignores the header uploads the last segment verbatim. The header is a
  request; the path is the guarantee. Neither `ApplicationPackFile.FileName` nor `VariantBlobPath`
  has an argument a label could be passed in, which is what makes this a property of the code
  rather than a rule. The variant id is a *directory* in that path and never part of the name, so
  re-rendering still overwrites the file it replaces and the stored hash still describes the bytes
  at that path.
- **Variants get their own blob container, and that is a constraint rather than a preference.** A
  document id and a variant id are independent identity spaces that both start at one, so document
  34 and variant 34 belonging to one profile address the same directory - and now that a chosen CV
  and a generated one spell the filename the same way, the same file. Nothing in
  `ApplicationPackFile` can detect it, because the container is a string it is handed: the caller
  passes `profile-cvs` for variants and `application-packs` for per-posting documents.
- **`ParkReason.NoCvVariant` is a third retry class, and getting that wrong reintroduces a bug this
  repository has already paid for once.** `ParkReasonPolicy` sorts reasons into permanent,
  retryable and - since `MissingAnswer` - conditional, and this is the second conditional kind.
  Not retryable: the gap is a pure function of what the advert asks for and what the library holds,
  and a run changes neither, so the next pass meets the same posting, scores the same variants,
  computes the same gap and parks it again, having spent a page load to learn what the last run
  already knew. Because one library serves the whole queue, a single missing CV does that to
  *every* posting that wanted it, on every run, for ever. Not permanent either: the two permanent
  reasons are statements about a vacancy, and this is a statement about a library the candidate can
  change on a Saturday afternoon, so permanence would turn the report of what is missing into a
  list of postings that cannot come back - and acting on the report would buy nothing. **And it did
  not join `AwaitingAnswer`, though a list called "the conditional ones" would have taken it and
  every caller would have gone on compiling**: that clause holds a posting while a question is
  outstanding, and its fallback for a park naming no question holds it only while *some other*
  advert's question is unanswered. Filed there, a CV park would be released the moment an unrelated
  question queue drained - with no variant written and nothing about it changed. The same loop,
  arriving through an answer given to a different advert, which is harder to see than the original.
- **Its condition is coverage, not authorship.** `ParkRequeue.WhenCovered` waits on a variant that
  covers *this* posting's recorded gap, never on the library having grown. Releasing on any
  authoring event means writing an eighth backend CV releases a posting parked for Kubernetes: the
  same loop at a longer period, with the bill handed to the person who has just done the work. So
  the queue clause joins `SubmissionParkGaps` - the concepts *this* park recorded as missing, dated
  at or after `ParkedAtUtc`, so a superseded park's rows leave the predicate by arithmetic rather
  than by a delete - and coverage is a walk through `ConceptClosure` in the direction `MatchScorer`
  credits, the gap the ancestor and the variant's concept the descendant, so a CV naming EKS covers
  a gap asking for Kubernetes. It is deliberately narrower than Core's own reading, which also
  entails through the curated `implies` edges: a variant covering a gap only by implication holds
  the posting rather than releasing it.
- **The release covers ANY recorded gap and never all of them, because releasing sends nothing.**
  Requiring one variant to cover every recorded gap was written first and is wrong. The argument
  for it - a library holding Kubernetes in one CV and Terraform in another would get a posting
  needing both released, and be sent a document answering half the advert - does not survive the
  mechanics: a released posting returns to the *queue*, where `CvVariantSelector` runs again and
  applies the floor and the margin, so a library that still does not fit produces `NoFit` and parks
  it once more. Selection decides what goes out; this clause decides only whether the question is
  worth re-asking. The universal therefore buys no protection at all, while it silently strands the
  posting whose park recorded five missing concepts and whose candidate then wrote a CV covering
  four of them - and since the floor is half the stated requirements rather than all of them, that
  CV would have been chosen. **A wrong release costs one re-park, in arithmetic, bounded by how
  often a person writes a CV. A wrong hold costs the application, and nothing reports it.**
- **A park with no standing gaps is held rather than released, and the explicit `Any()` is why.** A
  universal over an empty set is vacuously true, so without it a park whose gap rows failed to be
  written - a bug, an older row, a client that stopped half way - would be released the moment the
  candidate had any CV at all. That is the loop again, arriving through an oversight rather than
  through a decision.
- **There is no default variant: abstaining is the product rather than the failure.** Sending the
  nearest CV to a job it does not fit is exactly what this replaces, and it is invisible - the
  application simply never comes back, and nothing in the system ever learns why. An abstention is
  visible and leaves behind the one thing anybody can act on. So `CvSelectionOutcome` has three
  members rather than a nullable variant - `Ambiguous` is a question worth putting to a model with
  the advert in front of it, `NoFit` is not a question at all but a brief for a CV nobody has
  written - and `CvSelection.Chosen` is null on both of the others rather than holding the leader
  anyway, because a field holding the best of a bad set is a field somebody eventually sends.
- **The gap report is aggregate by construction, not by discipline.** Fifty individual "could not
  apply" notices is a queue nobody reads; one ranked list of three gaps is a Saturday afternoon
  with an obvious payoff. `CvGapBrief` therefore has no per-posting overload and no posting id in
  its answer, and its `MinimumPostings` floor of two makes the per-posting version *unbuildable*:
  hand `Compute` a single posting and every concept in it blocks exactly one, which is below the
  floor, so the answer is a brief with no gaps in it. `MaxGaps` is a constant rather than a
  parameter for the same reason - a caller free to ask for fifty rebuilds the queue the aggregation
  exists to prevent. A brief reporting blocked postings and *no* gaps is a real signal rather than
  an empty answer: it says postings are being parked over requirements that are all tags, all
  domains or all unknown keys, which is a selection or vocabulary fault rather than a CV anybody
  can write.
- **`get_submission_pack` chooses the CV, and the one write that read can make is a park.** The
  decision belongs in the pack rather than in the browser loop - the apply loop's own opening
  principle - so the pack returns the chosen variant's links, reports *why* in the same breath as
  the numbers, and the choice is recorded on the submission, which is what finally gives outcome
  feedback something real to correlate. It takes an optional `runId` **only** so that a
  `NoCvVariant` park made inside a run belongs to it: without it the commonest kind of park would
  be the one kind a run's summary could not be checked against, and `RunSummary.Parked` would read
  as an overstatement on every unattended pass. **That park is bounded to postings the queue could
  actually offer** - judged at least `Possible`, not dismissed, which `IsQueueEligibleAsync` asks
  one pair at a time. The pack assembles for any *matched* posting, so without the bound it could
  put down a posting `ListCvBlockedPostingsAsync` then filters out on those same two tests: a
  standing block sitting in `list_submissions` while `list_cv_gaps` answered that nothing was
  blocked at all. Three copies of that predicate is the arrangement that drifts, so
  `CvParkQueueTests` pins the single-pair one against the queue's own answer.
- **The selection floor and margin are their own constants and must not be merged with the
  matcher's.** `MatchRanker.FusionFloor` and `MatchSweepFunction.AssessmentThreshold` were briefly
  collapsed into one and that was already a mistake; `CvVariantSelector.SelectionFloor` answers a
  fourth question - how much of an advert a document has to actually answer before it is worth
  sending - and it is **reasoned rather than measured**, from the credit table: every partial-credit
  relation is worth less than half, so a variant answering every requirement by transferable ground
  alone tops out at 45 and fails, while one answering half of them outright passes. **A CV cannot
  be sent on resemblance.** Say that it is reasoned when quoting the number: there is no library to
  fit it against yet, which is itself the finding that motivated the feature.

## Common tasks

```bash
dotnet build
dotnet test                       # no Azure credentials needed, API suite included

# Run the API locally (copy src/JobPlatform.Api/appsettings.Local.example.json first)
dotnet run --project src/JobPlatform.Api

# Schema change
dotnet ef migrations add <Name> --project src/JobPlatform.Data --output-dir Sql/Migrations
dotnet run --project tools/JobPlatform.DbAdmin -- migrate "<connection-string>"

# Project the concept vocabulary into SQL. Idempotent; required after any migration and
# after any change to concepts.json.
dotnet run --project tools/JobPlatform.DbAdmin -- seed-concepts "<connection-string>"

# Stamp the cross-board key on the postings that predate the column, so the corpus and every row
# written since land in the same clusters. A console command rather than a step in the migration
# because the key is C#: JobFingerprint.CrossBoardKey parses a city out of a free-text location
# and folds case, punctuation and whitespace, and rewriting that in T-SQL would be a second
# implementation of the one rule deduplication rests on - the two would disagree on the first
# posting either spelled differently. It writes only where the value differs, so it is idempotent
# and it is not a one-off: re-running it is how the corpus is re-keyed after a change to the
# normalisation, which has already happened once to the other fingerprint. Dry run unless
# --confirm, and required once after the apply-loop migration or the queue clusters nothing.
dotnet run --project tools/JobPlatform.DbAdmin -- backfill-crossboard "<connection-string>" --confirm

# Remove submissions that never described a real application - a test of the write path, a
# client that misfired. The one eraser in an otherwise append-only pipeline, and deliberately a
# console command: an HTTP route would be reachable with the token the MCP client carries.
# Dry run unless --confirm.
dotnet run --project tools/JobPlatform.DbAdmin -- delete-submissions "<connection-string>" 12 13

# Where applications are actually made, per site: how many postings carry no direct apply link.
# There is no Easy Apply column anywhere - the missing link is the flag - and freehire is excluded
# because its scraper sets the field unconditionally. A site at ~100% board-hosted is a broken
# scraper selector, not a market that moved.
dotnet run --project tools/JobPlatform.DbAdmin -- apply-links "<connection-string>" 7

# Full provision (idempotent)
./scripts/provision.ps1 -ResourceGroup <rg> -LandingStorageAccount <account>

# ...with the AI provider enabled. Provisions a Foundry resource with two deployments and
# grants the shared identity Cognitive Services OpenAI User on it. There is no key to set
# afterwards, and no follow-up command to run.
./scripts/provision.ps1 -ResourceGroup <rg> -LandingStorageAccount <account> -AiProvider azureopenai

# Embed the recent corpus now, rather than waiting for 03:00 UTC. Bounded to ~150s per call,
# and resumable from the database, so calling it repeatedly is how a first pass gets finished.
curl -X POST "https://<function-app>.azurewebsites.net/api/run-embed-corpus?code=<function-key>"

# Score every profile against the corpus now, rather than waiting for 03:30 UTC. Wants the
# embedding pass to have run first; without it the matches rank on the score alone.
curl -X POST "https://<function-app>.azurewebsites.net/api/run-match-sweep?code=<function-key>"   -H 'Content-Type: application/json' -d '{}'

# Build the API image the way CI does (context is the repo root, not the project directory)
docker build -f src/JobPlatform.Api/Dockerfile -t job-platform-api .
```

Regenerating the test fixture is deliberate manual work — it is hand-built so its counts
are known by construction, which is what makes the metric assertions exact.
