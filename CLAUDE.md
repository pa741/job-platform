# CLAUDE.md

Guidance for Claude Code (or any assistant) working in this repository.

## Project overview

The Azure side of a job-market data pipeline. A separate repository
([`job-scrapper`](https://github.com/pa741/job-scrapper)) runs a JobSpy scraper on a NAS
and uploads timestamped CSVs to Blob Storage. This repository ingests them: postings into
Azure SQL, metrics into Cosmos DB, triggered by Event Grid on blob creation.

`../model.md` holds the target architecture for the whole system. Ingestion, Data and the
API are built; Realtime and Frontend are not.

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
- **Never introduce a secret.** The architecture has one, and only one (see below). If a
  change seems to need a password, key or connection secret, that is a signal the design
  is being worked around — find the identity-based path instead.
- `local.settings.json`, `*.PublishSettings` and `*.pubxml` are gitignored because they
  genuinely carry credentials.
- Before pushing, check `git ls-files` for stray CSVs and `git grep` for identifiers.
- `.gitleaks.toml` allowlists the Azure built-in **role definition** GUIDs by value. They are
  public constants, but they look exactly like generic API keys to the scanner. Allowlisting
  by value rather than by path keeps a real secret in the same file detectable.

## Authentication model

There is no password, key or connection secret anywhere, and it should stay that way:

- Azure SQL: `azureADOnlyAuthentication: true`. No SQL login exists. Connection strings use
  `Authentication=Active Directory Default`.
- Cosmos DB: `disableLocalAuth: true`. Access is a data-plane role assignment
  (`sqlRoleAssignments`), not a key.
- Storage: identity-based connections (`__serviceUri` + `__credential=managedidentity`).
  The Functions host account sets `allowSharedKeyAccess: false`.
- GitHub Actions: OIDC federated credential pinned to `main`. No client secret.
- Container Apps: the API runs under the same identity, and validates callers' Entra bearer
  tokens. No inbound key either.

The function runs under a **user-assigned** managed identity, deliberately: the Container
Apps API needs the same grants, and a shared identity means granting once. That has now paid
off — the API required no new role assignment and no new database user.

**The one exception is the Anthropic API key**, needed only when `Matching:Provider` is
`anthropic`. It lives in Key Vault, is read by the shared identity through a Container Apps
secret reference, and its *value* is set out of band with `az keyvault secret set` — never a
Bicep parameter, never a template output, never in deployment history. The default provider
is `keyword`, which provisions no vault and needs no key, so a fresh clone still deploys with
nothing to leak.

## Key files

- `src/JobPlatform.Core/Parsing/JobCsvParser.cs` — the JobSpy CSV contract. Written against
  the real 34-column export; changes here need a fixture case.
- `src/JobPlatform.Core/Metrics/MetricsCalculator.cs` — every metric. Pure and Azure-free,
  which is why the metric surface is fully unit-testable.
- `src/JobPlatform.Data/Sql/JobPostingRepository.cs` — the upsert, and the daily rollup
  aggregates.
- `src/JobPlatform.Ingestion/IngestionPipeline.cs` — the digest, shared by the blob trigger
  and the admin reprocess endpoint so both run the same path.
- `infra/main.bicep` — the whole stack.
- `src/JobPlatform.Api/Program.cs` — composition only. Routes live in `Features/<Name>/`, one
  `IEndpointGroup` each, registered in `Endpoints/EndpointGroupExtensions.cs`. Adding a
  feature is a folder plus one line there.
- `src/JobPlatform.Core/Matching/CvMatchingService.cs` — the retrieve → prefilter → rerank
  pipeline, and the fallback that keeps matching working when a paid ranker is not.
- `src/JobPlatform.Data/Sql/JobPostingQueryRepository.cs` — every SQL read the API makes.

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
  ids derive from the blob path. Event Grid redelivers; a replayed blob must converge.
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
  until the 1st of the next month. SQL is for posting browse/search/detail and match
  retrieval, behind output caching, and nothing else.
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
  `/match` or `/me`. Do not make it depend on whether `AzureAd` happens to be configured — a
  mistyped section name would then silently publish the whole dataset.
- **Matching must degrade, not fail.** `CvMatchingService` computes the keyword ordering
  first and returns it if the configured ranker throws or returns nothing, reporting
  `degradedToFallback`. A third party's rate limit must not 500 the endpoint.
- **Semantic Kernel is the LLM abstraction, deliberately.** There is no official Microsoft SK
  connector for Anthropic - only third-party alphas - so `MatchingRegistration.BuildKernel`
  composes one: the Anthropic SDK's `AsIChatClient()` handed to SK's `AsChatCompletionService()`.
  Keep prompts as Kernel prompt templates with `KernelArguments`; do not reach past the Kernel
  to the SDK, or the point of the abstraction is lost.
- **`TreatWarningsAsErrors` is off** because SK's Extensions.AI bridge is experimental
  (SKEXP0001). Warnings still appear in build output - do not let them accumulate.
- **`Microsoft.Extensions.*` is pinned to 10.x on a net9.0 target**, because SK and the
  Anthropic SDK both require `Microsoft.Extensions.AI` 10.5. With transitive pinning on,
  dropping these back to 9.0.0 fails the build with CS1705.
- **The SK path cannot use structured outputs.** SK's execution settings are provider-neutral,
  so the model may return fenced or prose-wrapped JSON; `ExtractJsonObject` absorbs that and
  `SemanticKernelRankerTests` pins the behaviour. Do not assume a bare JSON body.

## Common tasks

```bash
dotnet build
dotnet test                       # no Azure credentials needed, API suite included

# Run the API locally (copy src/JobPlatform.Api/appsettings.Local.example.json first)
dotnet run --project src/JobPlatform.Api

# Schema change
dotnet ef migrations add <Name> --project src/JobPlatform.Data --output-dir Sql/Migrations
dotnet run --project tools/JobPlatform.DbAdmin -- migrate "<connection-string>"

# Full provision (idempotent)
./scripts/provision.ps1 -ResourceGroup <rg> -LandingStorageAccount <account>

# ...with the Claude-backed ranker; the script prints the `az keyvault secret set` to run
./scripts/provision.ps1 -ResourceGroup <rg> -LandingStorageAccount <account> -MatchingProvider anthropic

# Build the API image the way CI does (context is the repo root, not the project directory)
docker build -f src/JobPlatform.Api/Dockerfile -t job-platform-api .
```

Regenerating the test fixture is deliberate manual work — it is hand-built so its counts
are known by construction, which is what makes the metric assertions exact.
