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
                     parse → enrich → upsert
                          │
              ┌───────────┼────────────┬───────────────────────┐
              ▼           │            ▼                       ▼
      Azure SQL           │      Cosmos DB (free tier)   queue: posting-extraction
      postings, concepts  │      run digests + rollups    │  (only when a provider
      assertions, mentions│            │                  │   is configured)
              │           │            └─ change feed →   ▼
              │           │               realtime (next) model pass ──> SQL
              │           ▼
              │    Blob: jobs-curated/curated/**  (daily timer, rebuilt from SQL)
              │    postings/…/postings.parquet    gold rows
              │    pairs/…/pairs.parquet          title ↔ concept, for training
              ▼
     API  (Container Apps, scale-to-zero)
     postings, metrics
```

## What it does

On every uploaded CSV the function:

1. **Parses** the JobSpy export, tolerating what that data actually contains —
   descriptions with embedded newlines and quotes, Python `True`/`False`, columns that are
   empty in every row, multi-valued `job_type`. A row it cannot parse is counted, not fatal.
2. **Enriches** each posting in memory — seniority, role family, work arrangement, salary
   recovered from prose, years of experience, company key, job types, tags, and the concepts
   it asks for. All of it pure CPU work, so it adds no round trip.
3. **Reconciles** postings against Azure SQL, keyed by `site:external_id`, tracking
   first-seen and last-seen so *new* postings can be distinguished from re-listings.
4. **Computes metrics** and writes them to Cosmos DB.

Ingestion is idempotent: run identity is the blob path (unique in SQL) and metric document
ids are derived from it, so a redelivered event or a manual replay converges instead of
duplicating. The derived rows follow the same rule — assertions are rewritten only for
postings whose content actually changed.

## Structured extraction

Most of what a job advert says is in its description, and a description is not something you
can `GROUP BY`. Three passes turn it into something you can.

**The vocabulary** is 213 concepts — skills, the domains above them, and qualifications
including UK security clearances — arranged as a **DAG rather than a tree**. Two parent axes:
`type.*` says what kind of thing a concept is, `area.*` says where it is used. That is not
tidiness; the data needs it. Python is a language *and* is used in backend, data and ML, and
the flat category field this replaced could record only one of those.

The concept **key** is the identity (`skill.kubernetes`), never the label. `k8s`, `K8S` and
`Kubernetes` land on one row, and renaming a label stays an edit rather than a migration.

**What cannot be resolved is recorded, not dropped.** "Go", "R", "C" and "Julia" are ordinary
words; matching them on sight would manufacture demand that does not exist, and silently
skipping them — which is what the previous vocabulary did — made the data wrong with no way
to measure by how much. They land in `PostingMentions` instead, which doubles as the list of
concepts worth adding next.

**A model pass is optional and skipped entirely when no provider is configured.** It runs on
a queue rather than inside the ingest, because the ingest throws to force Event Grid
redelivery and anything expensive in it would replay on every retry. It is asked only for
what a regex genuinely cannot do — required versus nice-to-have, years tied to one skill,
seniority when the title is uninformative — is handed the vocabulary as its allowed output
set, and has every key it returns re-checked against the graph. An invented key would be
indistinguishable from a real one in SQL.

## The curated zone

A daily timer rebuilds `jobs-curated` from SQL as partitioned Parquet — readable by DuckDB,
pandas, Fabric or Synapse serverless with nothing running:

```
curated/postings/searchTerm=<slug>/date=<yyyy-MM-dd>/postings.parquet
curated/pairs/searchTerm=<slug>/date=<yyyy-MM-dd>/pairs.parquet
```

The first is the denormalised gold row, with each posting's concepts already rolled up to
their domains so a query never needs the closure table. The second is the interesting one:
`(title, seniority, concept_key, polarity, years, source, evidence)` — around 1.6M rows a
year, in the shape published job-domain encoders are fine-tuned on, and a plain edge list for
`node2vec` or PyTorch Geometric. It is why building the concept graph properly was worth
doing, and it needs no graph database to be useful.

Partitions are **recomputed whole, never appended**, so a re-run converges and a failed one
needs no cleanup.

```sql
-- DuckDB, straight off the container
SELECT seniority_name, count(*) FROM 'curated/postings/**/*.parquet' GROUP BY 1;
```

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
| `GET /api/v1/postings` | Search: free text, site, company, job type, location, remote, salary, date ranges; five sort orders. Plus the structured axes below |
| `GET /api/v1/postings/{id}` | One posting in full |
| `GET /api/v1/postings/facets` | Filter vocabulary and totals, for building a filter UI in one round trip |
| `GET /api/v1/search-terms` | The axis everything partitions on |
| `GET /api/v1/runs`, `/runs/{id}` | Ingestion history — did the pipeline run, did it get anything |
| `GET /api/v1/metrics/latest` | The most recent run digest |
| `GET /api/v1/metrics/digests` | Run history over a time range |
| `GET /api/v1/metrics/rollups` | Daily rollups, oldest first — the dashboard time series |
| `GET /api/v1/metrics/summary` | Headline numbers and a day-over-day delta |
| `GET /api/v1/metrics/scraper-health` | Which columns have silently gone empty |
| `GET /health`, `/health/ready` | Liveness (touches nothing), readiness (Cosmos only) |

OpenAPI at `/openapi/v1.json`, with a Scalar UI at `/scalar/v1`.

### Searching the structured axes

```bash
# Everything under a domain, without knowing what is under it. The match runs through the
# closure, so this returns postings that never use the words "backend development".
GET /api/v1/postings?concept=area.backend

# Or one concept exactly. Same query shape, because a concept is its own ancestor at depth 0.
GET /api/v1/postings?concept=skill.kubernetes

GET /api/v1/postings?minSeniority=Senior&workArrangement=Hybrid
GET /api/v1/postings?roleFamily=Data&ir35=outside
GET /api/v1/postings?securityClearance=true
```

`minAnnualSalary` filters the **annualised** figure, not the board's raw column. Two reasons.
It covers more postings — the enricher reads salaries out of descriptions that no board ever
put in a salary field. And it puts day rates on the same scale, which is what lets a contract
at £550/day be compared against a £110,000 salary at all; `salaryStatedInterval` is what stops
the two being confused afterwards. Pass `includeTextSalary=false` to see only what an employer
typed into a salary field.

An unrecognised value for any of these is a `400`, not a silently dropped filter: dropping it
returns a plausible page of the wrong postings with nothing in the response to say so, and
that gets believed.

### What shapes it

**Metrics never come from SQL.** Every dashboard number already exists in Cosmos as a run
digest or daily rollup. That is not a shortcut — see the cost section below: SQL here is
billed on wall-clock time *online*, and a dashboard polling it would exhaust the monthly
grant and take the database offline until the following month. SQL serves posting search and
detail only, behind output caching and a rate limiter. For the same reason the
readiness probe checks Cosmos and never SQL: a probe alone would hold the database awake
around the clock.

**Semantic Kernel is the LLM abstraction, with a composed connector.** There is no official
Microsoft SK connector for Anthropic — the only NuGet packages are third-party alphas, which
is not a dependency worth carrying. So the Kernel is assembled from supported parts: the
official Anthropic SDK exposes an `IChatClient` through `Microsoft.Extensions.AI`, and SK
consumes any `IChatClient` as an `IChatCompletionService`. SK owns the prompt templates and
the chat abstraction; the SDK owns the wire. Swapping provider is a change to one method,
`AiRegistration.BuildKernel`.

That choice has a visible cost, which is the honest part: SK's execution settings are
provider-neutral, so they cannot express Anthropic's structured-output constraint. A model
may wrap its JSON in a code fence or a sentence of preamble, so a caller has to tolerate that
rather than being guaranteed a bare body. `AiJson.ExtractJsonObject` absorbs it and
`AiJsonTests` pins the behaviour.

**Nothing calls the model yet.** The API's first AI-backed feature — CV-to-posting matching —
was removed: it is being rebuilt with a different structure and flow. What was kept is the
provider layer and its credential path, because that part is orthogonal to whatever consumes
it. `AddAiProvider` registers a `Kernel` only when `Ai:Provider` is `anthropic` *and* a key is
present; anything else registers nothing rather than throwing, so an absent environment
variable cannot take down endpoints that have nothing to do with AI. `AiRegistrationTests`
resolves the service rather than merely registering it, since a Kernel that cannot be built is
otherwise silent until something asks for one.

## The dashboard

A React SPA on Static Web Apps (Free tier), signing in with MSAL and calling the API with
an Entra bearer token. Two pages: an overview of the market metrics, and a filterable
postings browser.

**The overview shows two salary numbers, and the gap between them is the point.** "Salary in
the columns" is what the scraper delivered; "salary known" is what is there once descriptions
have been read.

That gap has moved, and the movement is the interesting part. Before the fork ungated its own
salary extractor outside the US, the columns carried 2.5% and reading descriptions found
25.6% — a tenfold difference. With the fix shipped the scraper now extracts at source, so a
recent run reads 14.8% against 18.3%. The right response to a narrowing gap is not to remove
the second number: it is still finding salaries the boards do not publish, and the pair is
what shows whether the scraper is doing its job.

Alongside them: seniority mix, the three-way work-arrangement split a remote flag cannot
express, and demand shown twice — as individual concepts and **rolled up through the closure**
into areas. Those two answer different questions rather than one summarising the other:
individual tools scatter across a dozen ways of saying the same thing, and the rollup is what
shows whether the market wants backend or data people. It is the one number on the page that
could not exist without the concept graph.

The last card is **what the vocabulary could not place** — surface forms the resolver saw and
declined to guess at. That number is only knowable because unresolved forms are recorded
rather than dropped, and the most frequent of them are the list of what to learn next.

The postings browser filters on skill or area, seniority floor, working pattern and minimum
salary. The salary column shows the **annualised** figure, which is populated for materially
more postings than the scraper's raw column and puts day rates and salaries on one scale so
they can be compared at all. A day rate is marked as one, because annualised it is comparable
and is still not a salary.

**Charts follow one rule set rather than taste.** The categorical palette is validated for
colour-vision deficiency rather than eyeballed — every adjacent pair clears a ΔE separation
floor under deuteranopia, protanopia and tritanopia, in both light and dark. Dark mode is a
*selected* set of steps for the dark surface, not an automatic inversion. Beyond colour:

- Headline numbers are **stat tiles and meters**, not one-bar charts — for a single current
  value the number is the visualisation.
- **No dual-axis charts anywhere.** New postings per day and cumulative postings differ by
  an order of magnitude, so cumulative is a tile and the trend line stands alone.
- Rankings (companies, keywords) are **horizontal bars on a single-hue ramp** — one measure
  at different magnitudes is not several series, and horizontal keeps long labels readable.
- Board share is a **stacked bar with a table beside it**, never a pie.
- Status is never colour alone: the scraper-health pills carry an icon and a label, because
  two of the four status steps sit below 3:1 contrast on the light surface.

**Nothing the dashboard needs to start touches SQL.** The search-term list, the summary, the
trend and the scraper health all come from Cosmos, so opening the dashboard cold does not
wake the serverless database. Only the postings browser reads SQL, and those requests carry
a deadline: if the database is mid-wake they fail with an explanation and a
retry rather than a spinner. This is the property the whole read design exists for, and it
is easy to lose — an earlier version resolved the search term from SQL, which quietly put
every page, Cosmos-backed ones included, behind a database wake-up.

**Live metrics are polled, behind an abstraction built for push.** `MetricsFeed` is an
interface; `PollingMetricsFeed` implements it today. The underlying data changes once a day
when the scraper runs, so polling reflects the freshness the data actually has — and when the
Realtime row of `model.md` lands, a `PushMetricsFeed` over Web PubSub replaces it without a
component changing. The UI shows which feed is live so freshness is never a guess.

## Repository layout

```
src/JobPlatform.Core         Domain, CSV parsing, metrics, the concept vocabulary and every
                             deterministic classifier. No Azure dependencies.
src/JobPlatform.Data         EF Core (SQL) + Cosmos repositories, read and write sides.
src/JobPlatform.Ingestion    The Azure Function.
src/JobPlatform.Ai           Semantic Kernel + Claude provider. Standalone, so the SDK reaches nothing else.
src/JobPlatform.Api          The API. Vertical slices under Features/.
web/                         React dashboard. Vite, MSAL, Recharts.
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
an in-memory metrics source, and the Semantic Kernel composition, which is built with a
throwaway string in place of a key — enough to prove every link in the graph resolves without
reaching the network.

The fixture is **synthetic** — hand-built to reproduce the shape of real scraper output
(empty salary columns, 40% date coverage, a description full of newlines and quotes)
without committing any scraped data. See "Public repository" below.

## Deploy your own

Requires the Azure CLI, the GitHub CLI, and the .NET 9 SDK.

```powershell
az login
./scripts/provision.ps1 -ResourceGroup <rg> -LandingStorageAccount <existing-storage-account>

# Creates both Entra registrations - the API as a protected resource, the dashboard as a
# public client - and sets the repository variables CI needs. Pass the Static Web App URL
# that provision.ps1 printed so the dashboard can sign in from it.
./scripts/setup-api-app.ps1 -Repository <owner>/<repo> `
    -WebRedirectUris @('http://localhost:5173', '<static-web-app-url>')
```

> **Region note, again.** Static Web Apps is offered in only a few regions, and a region can
> separately stop accepting new customers: on this subscription `westeurope` refuses creation
> outright, while `eastus2`, `centralus`, `westus2` and `eastasia` all work. `webLocation` is
> its own parameter for the same reason `sqlLocation` is. The region is a control-plane
> choice — content is served from Microsoft's global edge — so being outside Europe costs
> nothing at request time.

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

Two more admin endpoints, for the pieces that do not run on the daily path:

```bash
# Rebuild curated Parquet for a range of days. Recomputed, so re-running is free.
curl -X POST ".../api/export-curated?code=<key>"      -H 'Content-Type: application/json' -d '{"date":"2026-08-22","days":7}'

# Queue everything without a current model extraction. Returns queued: 0 with a reason
# when no AI provider is configured, which is how this ships.
curl -X POST ".../api/backfill-extraction?code=<key>"      -H 'Content-Type: application/json' -d '{"limit":500}'
```

The extraction backfill is limited per call on purpose: the first run after configuring a
provider would otherwise queue the whole corpus, and that bill should be a decision rather
than a side effect.

## Cost

Designed to sit inside the free tiers, not merely to be cheap:

| Service | Free allowance | How this stays inside it |
| --- | --- | --- |
| Cosmos DB | 1,000 RU/s + 25 GB, lifetime | Database-level shared autoscale capped at exactly 1,000 RU/s |
| Azure SQL | 100,000 vCore-seconds/month | Serverless, `minCapacity 0.5`, 60-minute auto-pause — **the default; this deployment opts out, see below** |
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

### The one place this deployment spends money

`sqlSku` defaults to `free-serverless`, so cloning this repository still deploys at zero
cost. This particular deployment sets it to `basic`, and the reason is the cold start rather
than the money.

The free offer bills wall-clock seconds *online*, which makes always-on and free mutually
exclusive: the 100,000 vCore-second grant buys about **55 hours a month** at `minCapacity
0.5`, against 730 hours in a month. So the database must pause, and every wake costs a
~1 minute resume before the first query returns — fine for a daily batch, unacceptable when
someone opens the dashboard to look at it.

Basic is the DTU purchasing model, which has no serverless option at all, so it simply never
pauses. At 5 DTU and a 2 GB ceiling — against single-digit megabytes stored — it is the
cheapest always-on tier Azure sells: **€5.37/month** in France Central (€0.1766/day, retail,
verified against the Azure Retail Prices API). For comparison, the same database kept online
under the serverless meter would be roughly €209/month, and provisioned General Purpose
about €107.

Two things worth knowing before copying this:

- **It is one way.** Microsoft's docs: *"Once you convert a free offer database to a paid
  service tier, you can't revert to the free offer."* Going back means a new database — cheap
  here, because ingestion is idempotent and every source CSV is still in the landing
  container, so a replay rebuilds it.
- **`JP_SQL_SKU` must be set before the change is deployed.** CI redeploys the template on
  every push; with the variable unset the parameter defaults back to `free-serverless` and
  the pipeline would try to revert a database that cannot return to the free offer.

That number is also what dictates the API's shape rather than being a footnote to it. An API
serving dashboard reads from SQL would keep the database awake for as long as anyone had a
tab open, and the remaining ~46k vCore-seconds is a few days of that. So metrics are served
from Cosmos, which is always on and RU-billed inside its own free ceiling; SQL is reached
only for posting search and detail, behind output caching; and no health probe touches it at
all.

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
```

`/health` needs no token, by design — the platform's probe does not carry one.

Setting `Api:AllowAnonymousReads` opens the read endpoints so a frontend can be built against
real data before app registrations exist. It never opens `/me`, which is meaningless without
a principal, and it is keyed on the flag alone rather than on whether an identity provider
happens to be configured — otherwise a mistyped config section would silently publish the
whole dataset.

### Enabling the AI provider

Nothing calls the model yet — this provisions the credential path, not a feature.

```powershell
./scripts/provision.ps1 -ResourceGroup <rg> -LandingStorageAccount <account> -AiProvider anthropic
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

The single exception is the **Anthropic API key**, and only when the AI provider is enabled.
It lives in Key Vault, read by the managed identity through a Container Apps secret reference;
its value is set with `az keyvault secret set` and appears in no template, parameter file,
output or deployment history. The default is `none`, which needs no key and provisions no
vault, so a fresh clone of this repository still deploys with nothing to leak.

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
dotnet run --project tools/JobPlatform.DbAdmin -- grant-migrator "$CONN" job-platform-deploy
dotnet run --project tools/JobPlatform.DbAdmin -- metrics "https://<cosmos-account>.documents.azure.com:443/"
```

Remember to delete the rule when you are done.

## Status

Ingestion is complete, deployed, and verified end to end against real scraper output:
three uploads totalling 455 rows reduced to 286 distinct postings, with a re-uploaded blob
correctly reporting 0 new and leaving the row count unchanged — idempotency demonstrated on
live data rather than asserted. CI and the OIDC deployment workflow both run green.

The API is built, tested and deployed to Container Apps: 36 tests cover the route surface,
the authorization rules, and the Semantic Kernel composition and its response handling. It
runs under the same managed identity as the ingest function and needed
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

The cold start is the cost of `minReplicas: 0`, and it is the right trade here: the API is
idle most of the day, and an always-warm replica would burn the free grant serving nobody.

Still to come, per the architecture in `model.md`: a Cosmos change-feed function driving
Web PubSub for live metrics (the `leases` container is already provisioned), and CV matching,
which is being rebuilt with a different structure and flow — the AI provider layer it will run
on is already in place.
