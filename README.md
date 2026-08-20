# job-platform

Azure-side ingestion for a job-market data pipeline. A scraper
([`job-scrapper`](https://github.com/pa741/job-scrapper)) runs on a NAS and uploads
timestamped CSVs of job postings to Blob Storage; this repository turns each upload into
queryable relational data and a set of market metrics, within seconds of it landing.

Built to run at **zero cost** on Azure free tiers, with **no secrets anywhere** — every
service-to-service hop is authenticated by managed identity, and CI deploys through OIDC
federation rather than a stored credential.

```
NAS scraper ──CSV──> Blob Storage: jobs-landing/jobs/*.csv
                          │
                    Event Grid  (BlobCreated, filtered to jobs/*.csv)
                          │
                    Azure Function  (Flex Consumption, .NET 9 isolated)
                          │
              ┌───────────┴────────────┐
              ▼                        ▼
      Azure SQL (serverless)      Cosmos DB (free tier)
      postings + run history      run digests + daily rollups
              │                        │   └─ change feed → realtime dashboard (next)
              └───────────┬────────────┘
                          ▼
                 API  (Container Apps, scale-to-zero)
                 postings, metrics, CV matching
```

## What it does

On every uploaded CSV the function:

1. **Parses** the 34-column JobSpy export, tolerating what that data actually contains —
   descriptions with embedded newlines and quotes, Python `True`/`False`, columns that are
   empty in every row, multi-valued `job_type`. A row it cannot parse is counted, not fatal.
2. **Reconciles** postings against Azure SQL, keyed by `site:external_id`, tracking
   first-seen and last-seen so *new* postings can be distinguished from re-listings.
3. **Computes metrics** and writes them to Cosmos DB.

Ingestion is idempotent: run identity is the blob path (unique in SQL) and metric document
ids are derived from it, so a redelivered event or a manual replay converges instead of
duplicating.

## The metrics

Each run produces a `run-digest` document, and each search term/day a `daily-rollup`
recomputed from SQL:

| Group | What it answers |
| --- | --- |
| `counts` | How many rows arrived, parsed, were invalid, were duplicates — and how many postings were **new** vs. updated vs. unchanged |
| `bySite` | Which boards are actually producing results |
| `byJobType` | Full-time / contract / part-time mix |
| `remote` | Remote vs. on-site share |
| `freshness` | `date_posted` coverage, median age, how much is stale |
| `salary` | Salary disclosure rate, min/median/max where present |
| `topCompanies`, `topLocations` | Who is hiring, and where |
| `titleKeywords` | Normalised title tokens — the demand signal |
| `fieldFillRates` | Per-column non-empty ratio |

`fieldFillRates` is the one worth explaining. In a real London run, `min_amount` and
`currency` were populated in **0%** of rows and `date_posted` in only 40%. A column that
silently drops to zero is the earliest available signal that a job board changed its markup
and the scraper degraded without failing. The function logs a warning for every such column,
so the alert fires before anyone notices the dashboard looks thin.

## The API

An ASP.NET Core minimal API on Container Apps, scaled to zero when idle, authenticated by
Entra ID bearer tokens and reaching both databases through the same managed identity the
ingest function uses.

| Route | |
| --- | --- |
| `GET /api/v1/postings` | Search: free text, site, company, job type, location, remote, salary, date ranges; five sort orders |
| `GET /api/v1/postings/{id}` | One posting in full |
| `GET /api/v1/postings/facets` | Filter vocabulary and totals, for building a filter UI in one round trip |
| `GET /api/v1/search-terms` | The axis everything partitions on |
| `GET /api/v1/runs`, `/runs/{id}` | Ingestion history — did the pipeline run, did it get anything |
| `GET /api/v1/metrics/latest` | The most recent run digest |
| `GET /api/v1/metrics/digests` | Run history over a time range |
| `GET /api/v1/metrics/rollups` | Daily rollups, oldest first — the dashboard time series |
| `GET /api/v1/metrics/summary` | Headline numbers and a day-over-day delta |
| `GET /api/v1/metrics/scraper-health` | Which columns have silently gone empty |
| `POST /api/v1/match` | Rank stored postings against a CV |
| `POST /api/v1/match/profile` | The structured profile a match runs from |
| `GET /health`, `/health/ready` | Liveness (touches nothing), readiness (Cosmos only) |

OpenAPI at `/openapi/v1.json`, with a Scalar UI at `/scalar/v1`.

### What shapes it

**Metrics never come from SQL.** Every dashboard number already exists in Cosmos as a run
digest or daily rollup. That is not a shortcut — see the cost section below: SQL here is
billed on wall-clock time *online*, and a dashboard polling it would exhaust the monthly
grant and take the database offline until the following month. SQL serves posting search and
CV-match retrieval only, behind output caching and a rate limiter. For the same reason the
readiness probe checks Cosmos and never SQL: a probe alone would hold the database awake
around the clock.

**Matching degrades rather than fails.** `ICvRanker` has two implementations. The default is
a deterministic keyword ranker — no credentials, no cost, no network — and it also serves as
the retrieval prefilter that narrows candidates before a paid ranker sees them, which is what
bounds the cost of a request by configuration rather than by how many postings exist. The
Claude-backed ranker runs through **Semantic Kernel** and returns a score, a rationale, and
matched/missing skills. If it throws, is rate-limited, or returns nothing, the keyword
ordering is already computed and is returned instead, with `degradedToFallback: true` saying
so.

**Semantic Kernel is the LLM abstraction, with a composed connector.** There is no official
Microsoft SK connector for Anthropic — the only NuGet packages are third-party alphas, which
is not a dependency worth carrying. So the Kernel is assembled from supported parts: the
official Anthropic SDK exposes an `IChatClient` through `Microsoft.Extensions.AI`, and SK
consumes any `IChatClient` as an `IChatCompletionService`. SK owns the prompt templates and
the chat abstraction; the SDK owns the wire. Swapping provider is a change to one method.

That choice has a visible cost, which is the honest part: SK's execution settings are
provider-neutral, so they cannot express Anthropic's structured-output constraint. The model
may wrap its JSON in a code fence or a sentence of preamble, and the ranker has to tolerate
that rather than being guaranteed a bare body. `SemanticKernelRankerTests` pins that
behaviour.

## Repository layout

```
src/JobPlatform.Core         Domain, CSV parsing, metrics, matching. No Azure dependencies.
src/JobPlatform.Data         EF Core (SQL) + Cosmos repositories, read and write sides.
src/JobPlatform.Ingestion    The Azure Function.
src/JobPlatform.Ai           Semantic Kernel + Claude ranker. Isolated so the SDK reaches nothing else.
src/JobPlatform.Api          The API. Vertical slices under Features/.
tools/JobPlatform.DbAdmin    Schema migration and identity grants.
tests/                       Unit and integration tests. No credentials required.
infra/                       Bicep for the whole stack.
scripts/                     Provisioning and OIDC setup.
```

`Core` and `Data` were separated from the function so the API could reuse the same model
rather than redefining it, and it did: the API added read-side repositories beside the
existing write-side ones and reused the domain unchanged.

## Running the tests

No Azure account, no credentials, no network:

```bash
dotnet test
```

That includes the API's integration tests, which boot the real application against SQLite and
an in-memory metrics source. The keyword ranker being the default is what makes the matching
feature testable here at all — retrieval, prefiltering, ranking and fallback are all
exercised without a key.

The fixture is **synthetic** — hand-built to reproduce the shape of real scraper output
(empty salary columns, 40% date coverage, a description full of newlines and quotes)
without committing any scraped data. See "Public repository" below.

## Deploy your own

Requires the Azure CLI, the GitHub CLI, and the .NET 9 SDK.

```powershell
az login
./scripts/provision.ps1 -ResourceGroup <rg> -LandingStorageAccount <existing-storage-account>
./scripts/setup-api-app.ps1 -Repository <owner>/<repo>
```

That registers the resource providers, deploys `infra/main.bicep`, applies the database
schema, and grants the function's managed identity a database user. Then, for CI:

```powershell
./scripts/setup-github-oidc.ps1 -Repository <owner>/<repo> -ResourceGroup <rg> -LandingStorageAccount <account>
```

Finally, point the scraper at the landing container by setting
`AZURE_CONTAINER_NAME=jobs-landing` in its `.env` and NAS `docker-compose.yml`.

### Backfilling

The container may already hold runs from before the function existed:

```bash
curl -X POST "https://<function-app>.azurewebsites.net/api/reprocess?code=<function-key>" \
     -H 'Content-Type: application/json' -d '{"prefix":"jobs/"}'
```

Same pipeline, same idempotency guarantees.

## Cost

Designed to sit inside the free tiers, not merely to be cheap:

| Service | Free allowance | How this stays inside it |
| --- | --- | --- |
| Cosmos DB | 1,000 RU/s + 25 GB, lifetime | Database-level shared autoscale capped at exactly 1,000 RU/s |
| Azure SQL | 100,000 vCore-seconds/month | Serverless, `minCapacity 0.5`, 60-minute auto-pause |
| Functions | Monthly grant on Flex Consumption | One short execution per day |
| Container Apps | 180k vCPU-s + 360k GiB-s/month | API scales to zero when idle; max 3 replicas |
| Container registry | — | Public image on GHCR, so no ACR (~$5/month) and no registry credential |
| Log Analytics | — | 1 GB/day ingestion cap |

> **Region note.** The SQL free offer is not provisionable in every region, and the
> restriction is per subscription, not just per region. On this subscription Spain Central
> and West Europe reject it outright (`ProvisioningDisabled`), while North Europe, UK South
> and East US 2 refuse new SQL servers altogether. **France Central works**, so the SQL
> server sits there while everything else runs in Spain Central. Cross-region costs a few
> milliseconds per round trip, which is immaterial for a once-daily batch. `sqlLocation`
> is a separate parameter for exactly this reason — probe before changing it.

**The one number to watch is SQL vCore-seconds.** Serverless bills wall-clock time *online*,
not CPU. One daily ingest keeps the database awake about an hour — roughly 1,800 vCore-s/day,
about 54k/month against the 100k grant. Several runs a day would exceed it. The database is
configured with `freeLimitExhaustionBehavior: AutoPause`, so if the grant does run out it
pauses until the first of the next month rather than falling through to paid rates. Cost is
structurally capped at zero; the failure mode is unavailability, not a bill.

That number is also what dictates the API's shape rather than being a footnote to it. An API
serving dashboard reads from SQL would keep the database awake for as long as anyone had a
tab open, and the remaining ~46k vCore-seconds is a few days of that. So metrics are served
from Cosmos, which is always on and RU-billed inside its own free ceiling; SQL is reached
only for posting search and match retrieval, behind output caching; and no health probe
touches it at all.

## Calling the API

Reads require an Entra token by default. `scripts/setup-api-app.ps1` creates the app
registration the API validates against - it exposes a `Jobs.Read` scope and pre-authorises
the Azure CLI, so a token is one command away with no consent prompt:

```powershell
./scripts/setup-api-app.ps1 -Repository <owner>/<repo>
```

```bash
TOKEN=$(az account get-access-token --scope api://<api-client-id>/Jobs.Read --query accessToken -o tsv)
API=https://<container-app>.<region>.azurecontainerapps.io

curl -H "Authorization: Bearer $TOKEN" "$API/api/v1/search-terms"
curl -H "Authorization: Bearer $TOKEN" "$API/api/v1/metrics/summary?searchTerm=software-engineer"
curl -H "Authorization: Bearer $TOKEN" "$API/api/v1/postings?searchTerm=software-engineer&remote=true&limit=5"

curl -X POST "$API/api/v1/match" -H "Authorization: Bearer $TOKEN"      -H 'Content-Type: application/json'      -d '{"cvText":"Backend engineer, 7 years. C#, .NET, Azure, Kubernetes.","topN":5}'
```

`/health` needs no token, by design — the platform's probe does not carry one.

Setting `Api:AllowAnonymousReads` opens the read endpoints so a frontend can be built against
real data before app registrations exist. It never opens `/match` (which costs money per
call) or `/me` (which is meaningless without a principal), and it is keyed on the flag alone
rather than on whether an identity provider happens to be configured — otherwise a mistyped
config section would silently publish the whole dataset.

### Enabling the Claude-backed ranker

```powershell
./scripts/provision.ps1 -ResourceGroup <rg> -LandingStorageAccount <account> -MatchingProvider anthropic
az keyvault secret set --vault-name <vault> --name anthropic-api-key --value '<key>'
az containerapp revision restart -g <rg> -n <api-app>
```

The key is set out of band on purpose. Passing it as a deployment parameter would write it
into the deployment history, where it would outlive any rotation.

## Public repository

There is exactly one secret in this design, and it is optional:

- Azure SQL is **Entra-only** (`azureADOnlyAuthentication`) — no SQL login exists.
- Cosmos DB has **local auth disabled** — the keys are not usable.
- Storage uses **identity-based connections**, with `allowSharedKeyAccess: false` on the
  Functions host account.
- CI authenticates by **OIDC federation** pinned to this repo's `main` — no client secret.
- The API validates **Entra bearer tokens** and reaches both databases with the shared
  managed identity — no inbound key, no outbound connection secret.
- The API image is **public on GHCR**, so pulling it needs no registry credential either.

The single exception is the **Anthropic API key**, and only when the Claude-backed ranker is
enabled. It lives in Key Vault, read by the managed identity through a Container Apps secret
reference; its value is set with `az keyvault secret set` and appears in no template,
parameter file, output or deployment history. The default ranker needs no key and provisions
no vault, so a fresh clone of this repository still deploys with nothing to leak.

What still needs care, and how it is handled:

- **Identifiers.** `infra/main.bicepparam` reads subscription, tenant and resource names
  from environment variables. Nothing identifying is committed.
- **Scraped PII.** Real exports contain populated `emails` and descriptions carrying
  recruiter contact details. No scraped output is committed; the test fixture is synthetic.
- `.gitignore` was the repository's first commit, before any other file existed.
- CI runs `gitleaks`, and `pull_request` (never `pull_request_target`) keeps fork code away
  from secrets.

## Operating it

The SQL server's only standing firewall rule allows Azure services, so the function can
reach it but your workstation cannot. `scripts/provision.ps1` opens a rule for your current
IP and removes it again when it finishes. To inspect the data outside that window, add a
rule for yourself first:

```bash
MYIP=$(curl -s https://api.ipify.org)
az sql server firewall-rule create -g <rg> -s <server> -n my-client   --start-ip-address $MYIP --end-ip-address $MYIP
```

Then:

```bash
CONN="Server=tcp:<server>.database.windows.net,1433;Database=jobsdb;Authentication=Active Directory Default;Encrypt=True;Connect Timeout=90;"
dotnet run --project tools/JobPlatform.DbAdmin -- status  "$CONN"
dotnet run --project tools/JobPlatform.DbAdmin -- metrics "https://<cosmos-account>.documents.azure.com:443/"
```

Remember to delete the rule when you are done.

## Status

Ingestion is complete, deployed, and verified end to end against real scraper output:
three uploads totalling 455 rows reduced to 286 distinct postings, with a re-uploaded blob
correctly reporting 0 new and leaving the row count unchanged — idempotency demonstrated on
live data rather than asserted. CI and the OIDC deployment workflow both run green.

The API is built, tested and deployed to Container Apps: 47 tests cover the route surface,
the authorization rules, the matching pipeline, the Semantic Kernel composition and its
response handling. It runs under the same managed identity as the ingest function and needed
no new role assignment and no new database user to do it - which is what the user-assigned
identity was chosen for in the first place.

Verified live against the real ingested data, with a real Entra token:

| Endpoint | Result |
| --- | --- |
| `/health` | 200 in ~0.3s warm, ~25s cold from zero replicas |
| any route without a token | 401 |
| `/api/v1/search-terms` | 286 postings across 3 runs |
| `/api/v1/postings?remote=true` | 39 of 286, correct paging and totals, no description in list rows |
| `/api/v1/postings/facets` | linkedin 161 / indeed 125; London 94; salary coverage 0% |
| `/api/v1/metrics/summary` | served from Cosmos, SQL untouched |
| `/api/v1/match` | 14 skills parsed, 286 candidates ranked, top hit "Backend Software Engineer C# .Net" |

The cold start is the cost of `minReplicas: 0`, and it is the right trade here: the API is
idle most of the day, and an always-warm replica would burn the free grant serving nobody.

Still to come, per the architecture in `model.md`: a Cosmos change-feed function driving
Web PubSub for live metrics (the `leases` container is already provisioned), and a React
dashboard.
