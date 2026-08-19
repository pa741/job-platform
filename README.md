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
                                     └─ change feed → realtime dashboard (next)
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

## Repository layout

```
src/JobPlatform.Core         Domain, CSV parsing, metrics. No Azure dependencies.
src/JobPlatform.Data         EF Core (SQL) + Cosmos repositories.
src/JobPlatform.Ingestion    The Azure Function.
tools/JobPlatform.DbAdmin    Schema migration and identity grants.
tests/JobPlatform.Core.Tests Unit tests. No credentials required.
infra/                       Bicep for the whole stack.
scripts/                     Provisioning and OIDC setup.
```

`Core` and `Data` are separate from the function because the planned API will reuse the
same model rather than redefining it.

## Running the tests

No Azure account, no credentials, no network:

```bash
dotnet test
```

The fixture is **synthetic** — hand-built to reproduce the shape of real scraper output
(empty salary columns, 40% date coverage, a description full of newlines and quotes)
without committing any scraped data. See "Public repository" below.

## Deploy your own

Requires the Azure CLI, the GitHub CLI, and the .NET 9 SDK.

```powershell
az login
./scripts/provision.ps1 -ResourceGroup <rg> -LandingStorageAccount <existing-storage-account>
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

## Public repository

There are no secrets in this design to begin with:

- Azure SQL is **Entra-only** (`azureADOnlyAuthentication`) — no SQL login exists.
- Cosmos DB has **local auth disabled** — the keys are not usable.
- Storage uses **identity-based connections**, with `allowSharedKeyAccess: false` on the
  Functions host account.
- CI authenticates by **OIDC federation** pinned to this repo's `main` — no client secret.

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

Still to come, per the architecture in `model.md`: a Cosmos change-feed function driving
Web PubSub for live metrics (the `leases` container is already provisioned), an API on
Container Apps, and a React dashboard.
