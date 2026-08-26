# CLAUDE.md

Guidance for Claude Code (or any assistant) working in this repository.

## Project overview

The Azure side of a job-market data pipeline. A separate repository
([`job-scrapper`](https://github.com/pa741/job-scrapper)) runs a JobSpy scraper on a NAS
and uploads timestamped CSVs to Blob Storage. This repository ingests them: postings into
Azure SQL, metrics into Cosmos DB, triggered by Event Grid on blob creation.

`../model.md` holds the target architecture for the whole system. Ingestion, Data, the API,
the Frontend, the candidate profile, matching and generated applications are built; Realtime
is not.

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
- `src/JobPlatform.Core/Profiles/CandidateProfile.cs` — the supply side of the match, and
  `ToDocument()`, which is the exact text the extractor reads and the hash is taken over.
- `src/JobPlatform.Data/Sql/CandidateProfileRepository.cs` — every profile read and write.
  Takes a subject id and never a profile id; that is the authorisation boundary.
- `src/JobPlatform.Ingestion/Functions/MatchSweepFunction.cs` — the nightly pass. Scores
  everything, then spends the model budget on what clears the threshold.
- `src/JobPlatform.Documents/MarkdownPdfRenderer.cs` — model output to PDF, through a parsed
  AST and a fixed node mapping. No HTML step exists.

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
- **Run `dbadmin seed-concepts` after any migration.** The concept tables are a projection of
  the vocabulary shipped in the build; a schema that has moved without them silently stops
  recording assertions for anything new. `deploy.yml` runs it in the same job as `migrate`,
  and the ingest logs a warning naming the command when it notices.
- **A surface form that cannot be resolved is recorded, never dropped.** `PostingMentions`
  exists because the previous vocabulary handled ambiguous names — Go, R, C, Julia — by
  refusing to match them, which meant the data was wrong with no way to find out by how much.
  It is also where new vocabulary comes from: the most frequent unresolved forms each month.
- **Never let the model invent a concept key.** `KernelDocumentExtractor` re-checks every key
  against the graph and demotes anything unknown to a mention. A hallucinated key is
  indistinguishable from a real one in SQL and would quietly split a concept in two.
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
- **Free-tier ceilings are load-bearing**, not incidental: Cosmos autoscale max 1000 RU/s,
  SQL `useFreeLimit` with `freeLimitExhaustionBehavior: AutoPause`. Raising either starts
  billing.
- Metrics changes belong in `MetricsCalculator` with a matching assertion in
  `MetricsCalculatorTests`, against the synthetic fixture's known-by-construction counts.

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

### Dashboard (`web/`)

- **Chart colours are validated, not chosen.** The categorical slots in
  `src/theme/tokens.css` clear CVD-separation and contrast gates in both modes. Never
  substitute a hex by eye - colour-vision separation is not something the eye can check, and
  the slot *order* is itself the safety mechanism. Add series by taking the next slot; a
  fifth series folds into "Other" rather than getting a generated hue.
- **Never add a dual-axis chart.** Two y-scales on one plot is the single most misleading
  thing a dashboard can do. Two measures of different magnitude means two charts, or one
  chart and one stat tile.
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

- **`readEnvironmentVariable`'s default does not fire in GitHub Actions.** An undefined
  `vars.X` becomes an environment variable that is *set and empty*, not absent, so the
  fallback argument is skipped and `''` reaches the template. `infra/main.bicepparam`
  therefore defaults with `empty(readEnvironmentVariable('X', '')) ? default : ...`. Follow
  that pattern for every new parameter, or a missing repo variable fails the deploy with
  BCP033 rather than taking the default.
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
- **SQLite cannot `ORDER BY` a `DateTimeOffset`.** `JobsDbContext.ConfigureConventions`
  converts to ticks under SQLite only, so the tests can exercise the real orderings; SQL
  Server keeps native `datetimeoffset`.
- **List responses must not carry `Description`.** It is unbounded `nvarchar(max)`; only
  `PostingDetail` returns it. `PostingEndpointTests` asserts this, because nothing else fails
  when it regresses.
- **`Api:AllowAnonymousReads` is the only switch that opens reads**, and it never opens
  `/me`. Do not make it depend on whether `AzureAd` happens to be configured — a mistyped
  section name would then silently publish the whole dataset.
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
- **A lone string-matched concept is not evidence a posting wants anything.** The floor above
  was still too weak once the corpus was fully extracted: "Home Delivery Driver" scored 94 on
  one Taxonomy hit - the word "containers", in an advert about delivering physical ones - which
  the candidate's Kubernetes implied. The model had read that advert and correctly extracted
  nothing. A Board tag or a Model assertion is a deliberate claim and counts on its own; a
  Taxonomy hit needs `MinimumTaxonomyOnlyConcepts` of them, because that resolver's own remarks
  admit it finds things mentioned in passing as readily as things required. **Run the vocabulary
  fix and the scorer fix together**: the alias that caused it is now `ambiguous`, so the same
  advert would record a mention rather than an assertion either way.
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
- **The sweep is a timer, not a page load.** `MatchSweepFunction` runs at 03:30 UTC, after the
  ingest and extraction queues have drained. A shortlist that costs model calls to look at is one
  nobody can afford to browse. `run-match-sweep` exists for the case the timer cannot serve -
  somebody who has just filled in their profile - and is a Function-key admin route deliberately.
- **The scoring pass needs no model at all.** `MatchSweepFunction` is registered
  unconditionally and `ICandidacyAssessor` is the nullable half, so a deployment with no AI
  provider still produces ranked matches - just without the judgement layer.

### Profiles and generated documents

- **A form, not an uploaded CV.** Parsing a PDF back into structure is a lossy guess at what the
  person already knows. The generated CV is an *output* of the profile, not a rewrite of an
  input.
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
  guarantee real.
- **The markdown is the record; the PDF is rendered per request.** Storing the PDF would mean a
  layout change could not reach documents already generated, and would put megabytes into a
  database billed by the second.
- **`MarkdownPdfRenderer` walks a parsed AST and emits from a fixed set of node types.** There is
  no HTML step and nothing the model returns is ever interpreted as markup. An unmapped node
  renders as its plain text rather than being dropped - silently losing a node would take content
  out of a document somebody is about to send to an employer.
- **PDFsharp's platform-independent build resolves no fonts at all** and throws on its first call
  without a resolver, including for its own internal error font. `EmbeddedFontResolver` embeds
  Roboto (SIL OFL 1.1) and resolves *any* family name, because MigraDoc asks for "Courier New"
  whatever the document says. Installing fonts in the container was rejected: it renders
  differently on a developer's machine and turns a missing apt package into a 500 on somebody's
  CV download.

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

# Full provision (idempotent)
./scripts/provision.ps1 -ResourceGroup <rg> -LandingStorageAccount <account>

# ...with the AI provider enabled. Provisions a Foundry resource with two deployments and
# grants the shared identity Cognitive Services OpenAI User on it. There is no key to set
# afterwards, and no follow-up command to run.
./scripts/provision.ps1 -ResourceGroup <rg> -LandingStorageAccount <account> -AiProvider azureopenai

# Score every profile against the corpus now, rather than waiting for 03:30 UTC.
curl -X POST "https://<function-app>.azurewebsites.net/api/run-match-sweep?code=<function-key>"   -H 'Content-Type: application/json' -d '{}'

# Build the API image the way CI does (context is the repo root, not the project directory)
docker build -f src/JobPlatform.Api/Dockerfile -t job-platform-api .
```

Regenerating the test fixture is deliberate manual work — it is hand-built so its counts
are known by construction, which is what makes the metric assertions exact.
