# job-platform

The Azure side of a job-market data pipeline. A scraper
([`job-scrapper`](https://github.com/pa741/job-scrapper)) runs on a NAS and uploads
timestamped CSVs of job postings to Blob Storage; this repository turns each upload into
queryable relational data and a set of market metrics, within seconds of it landing — and then
matches those postings against a candidate's own profile, writing a tailored CV and cover
letter for the ones worth applying to.

Built to run at **zero cost** on Azure free tiers, with **no secrets anywhere** — every
service-to-service hop is authenticated by managed identity, the language models included, and
CI deploys through OIDC federation rather than a stored credential.

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
     postings, metrics, profile, matches, generated documents
              ▲
              │
   Azure Function  (timer, 03:30 UTC)
   score every profile × posting ─┬─> above threshold ──> model ──> verdict
                                  └─> below            ──> stored, unjudged
```

Both model passes run on **Azure OpenAI in Microsoft Foundry**, reached with the same managed
identity as everything else. There is no API key anywhere in this system, and no Key Vault.

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

**The vocabulary** is 222 concepts — skills, the domains above them, and qualifications
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

Everything above is about the corpus. The routes below are about the signed-in person, and
none of them takes an identifier for *whose* data it wants — the API resolves that from the
token's `oid` claim, so there is no way for a client to ask for somebody else's. All of them
require a principal unconditionally: the `Api:AllowAnonymousReads` switch that opens the
posting endpoints during development never reaches them.

| Route | |
| --- | --- |
| `GET /api/v1/profile` | The caller's profile. 404 where they have not created one, which is a real state rather than an error |
| `PUT /api/v1/profile` | Replaces it with the submitted form, and re-reads it for skills when the text actually changed |
| `DELETE /api/v1/profile` | Erases the profile, every match and every generated document |
| `GET /api/v1/matches` | Scored matches, best first. `minScore`, `assessedOnly`, paging |
| `GET /api/v1/matches/{postingId}` | One match with the breakdown behind the number |
| `POST /api/v1/applications/{postingId}` | Writes a tailored CV and cover letter. The only route that spends money on the expensive model |
| `GET /api/v1/applications`, `/{id}` | Generated drafts, as markdown |
| `GET /api/v1/applications/{id}/cv.pdf` | The CV, rendered |
| `GET /api/v1/applications/{id}/cover-letter.pdf` | The cover letter, rendered |

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

**Semantic Kernel is the LLM abstraction, over a first-party connector.** The provider is
Azure OpenAI in Microsoft Foundry, and `AiRegistration.BuildKernel` puts two deployments on one
Kernel — `bulk` and `writing` — which a prompt selects between by service id. That is the only
thing deciding whether a call costs the cheap model's money or the expensive one's.

This replaced a hand-composed connector. There is no official Microsoft SK connector for
Anthropic, so the Kernel used to be assembled from the Anthropic SDK's `IChatClient` handed to
SK's experimental `Microsoft.Extensions.AI` bridge. Two things fell out of moving:

- **The API key is gone, and with it the only secret in the system.** Azure OpenAI
  authenticates with Microsoft Entra, so the same user-assigned managed identity that already
  reaches SQL, Cosmos and Storage reaches the models. The Key Vault, the Container Apps secret
  reference and the out-of-band `az keyvault secret set` were deleted rather than tidied up, and
  the account sets `disableLocalAuth: true` so the keys it still mints in the portal will not
  authenticate anything.
- **Structured output became expressible.** SK's provider-neutral settings could not ask for it;
  the Azure OpenAI connector can, so a response is guaranteed to parse.
  `AiJson.ExtractJsonObject` stays as a net rather than as the normal path — a response format
  is a request to a provider, not a property of the transport — and `AiJsonTests` still pins it.

**Two models, because the two jobs have opposite shapes.**

| | Deployment | Runs on | Why |
| --- | --- | --- | --- |
| Extraction, candidacy assessment | `gpt-5.6-luna` | Every posting; every shortlisted pair | Cheapest of the 5.6 family, 1.05M context. High volume, structured output, judged in aggregate. |
| Tailored CV and cover letter | `gpt-5.6-sol` | Once per application | Roughly 25× the price per token, and the call ratio runs several thousand to one the other way. This is the artefact a human reads. |

Both are deployment *names* rather than model ids, so pointing one at a newer release is a
repository variable, not a code change.

**"In batches" means many documents per request, not the Batch API.** Azure's Global Batch
matrix does not yet carry the GPT-5.6 family — it tops out at `gpt-5.4` — so the 24-hour batch
discount is unavailable for Luna today. That turns out not to matter much, because the saving
that actually counts here is elsewhere: the 222-concept vocabulary has to precede every
extraction as the model's allowed output set, and sent once per posting it dwarfs the adverts
themselves. Ten documents to a call pays for it once instead of ten times.

The cost of batching is a failure mode worth naming. An extraction landing against the wrong
posting would be wrong, internally consistent, and undetectable afterwards — so the model is
asked to echo each document's index, and `KernelDocumentExtractor` drops any answer whose index
is out of range or repeated rather than clamping it. Those postings simply have no extraction
row and are picked up by the backfill. Reordering, duplicates, short responses and out-of-range
indices are all pinned by tests.

## Matching

A candidate fills in a form — experience, education, projects, certifications, declared
skills — rather than uploading a CV. Parsing a PDF back into structure is a lossy guess at
something the person already knows: which employer, which dates, which bullet point is the one
that matters. Asking directly produces a record with fields, which is what makes the generated
CV an *output* rather than a rewrite of an input.

The free text in that form goes through the same extractor a job advert does, with the same
vocabulary, into a `ProfileConcepts` table with the same columns as `PostingConcepts`. **That
is the whole payoff of a shape fixed before there was a profile to put in it: matching is a
join, not a second pipeline.**

**The arithmetic runs on everything; the model runs on what survives it.** `MatchScorer` is pure
and Azure-free, like the metrics calculator, and scores every candidate pair over seven axes:
essential skills, other skills, seniority, experience, working arrangement, salary and location.
It reasons through the concept graph rather than by string equality — holding EKS satisfies a
Kubernetes requirement outright, holding Kubernetes satisfies a containerisation requirement
through a curated `implies` edge, holding Kubernetes against an EKS requirement earns partial
credit, and holding Vue against a React requirement earns a little. Every one of those is
reported back with the relation that produced it, because "you have Vue, they want React" is an
argument rather than a match, and presenting it as one is how somebody ends up in the wrong
interview.

**Silence drops an axis rather than failing it.** Most postings state no salary, many state no
working arrangement, and 18% of titles carry no seniority. An axis the posting cannot answer
contributes nothing to the numerator *and* nothing to the denominator. Scoring it as zero would
rank a posting that says nothing below one that says something incompatible; scoring it as full
marks would make vagueness a competitive advantage.

What clears a threshold is then read by the model, in batches, and the profile travels once for
the whole batch rather than once per posting. It is not asked to score from scratch: it is
handed the number, the matched concepts and the gaps, and asked the one question the arithmetic
cannot answer — whether the gaps matter. A missing Kubernetes on a role that mentions it once in
a nice-to-have list is not the same fact as a missing Kubernetes on a platform role, and no
weighting scheme distinguishes them because the difference is in the prose.

**Both verdicts are stored and neither overwrites the other.** They disagree fairly often, and
the disagreement is the informative part: a 58 the model calls strong is precisely the posting
worth surfacing, and collapsing the two into one column deletes the only signal that says so.

All of it runs at 03:30 UTC, after the ingest and extraction queues have drained — never when
somebody opens the page. A shortlist that costs model calls to look at is one nobody can afford
to browse.

## Generated applications

For a matched posting, the writing deployment produces a tailored CV and cover letter as
markdown, which the API renders to PDF on demand.

The gap list from the match is passed in as **the set of claims the document must not make**.
Tailoring means choosing what to lead with and rewriting real work to foreground the relevant
part of it; it never means adding what is not there. A CV that invents a year of Kubernetes is
not a better CV — it is one that falls apart in the interview, and it is the candidate rather
than this system that pays for that. Generation therefore requires an existing match: a document
written without a gap list has nothing stopping it.

Markdown, and never HTML. `MarkdownPdfRenderer` parses the model's output into an abstract
syntax tree and maps each node type onto a fixed set of document elements, so there is no path
by which a response becomes markup that anything executes or styles. The layout belongs to this
repository; the model supplies words and structure only. A node type with no mapping renders as
its plain text rather than being dropped — silently losing one would take content out of a
document somebody is about to send to an employer.

The markdown is the record and the PDF is a rendering of it, produced per request. Storing the
PDF would mean a layout change could not reach documents already generated, and would put
megabytes of binary into a database billed by the second.

One trap worth writing down: PDFsharp's platform-independent build resolves **no fonts at all**,
and throws on its first call without a resolver — including for its own internal error font.
Roboto is embedded under the SIL Open Font License, and the resolver answers for any family name
because MigraDoc asks for "Courier New" whatever the document says. Installing fonts in the
container was the alternative and was rejected: it renders differently on a developer's machine
and turns a missing apt package into a 500 on somebody's CV download.

## The dashboard

A React SPA on Static Web Apps (Free tier), signing in with MSAL and calling the API with
an Entra bearer token. Four pages: an overview of the market metrics, a filterable postings
browser, the candidate's own matches, and the profile form that feeds them.

The last two behave differently from the first two on purpose. Overview and Postings are about
a slice of the corpus and wait on the search-term bootstrap; Profile and Matches are about the
signed-in person and render even when the platform has ingested nothing at all — which is
exactly the state somebody filling in their profile for the first time is in.

The match view shows the per-axis breakdown behind the score, and **omits every axis the
posting said nothing about** rather than drawing it at zero: the scorer drops those from the
denominator, so rendering them would show a penalty that was never applied. It also names the
relation behind each satisfied requirement, because "you have Vue, they want React" is an
argument rather than a match.

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
src/JobPlatform.Core         Domain, CSV parsing, metrics, the concept vocabulary, the match
                             scorer and every deterministic classifier. No Azure dependencies.
src/JobPlatform.Data         EF Core (SQL) + Cosmos repositories, read and write sides.
src/JobPlatform.Ingestion    The Azure Functions: ingest, extraction, curated export, match sweep.
src/JobPlatform.Ai           Semantic Kernel over Azure OpenAI. Reaches Core and nothing else.
src/JobPlatform.Documents    Markdown to PDF. Knows nothing about postings or profiles, which
                             is what makes it testable by handing it a string.
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
an in-memory metrics source; the Semantic Kernel composition, which resolves both deployments
without a credential because there is no longer one to supply; the extraction batching, driven
by a stub chat service so a reordered or duplicated index can be asserted rather than hoped
for; the scoring rules, against the real 222-concept vocabulary rather than a synthetic one;
and the PDF renderer, over a table of inputs the prompt explicitly asks the model *not* to
produce — because a prompt is a request, not a guarantee, and the cost of being wrong is a
candidate's download failing.

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

```powershell
./scripts/provision.ps1 -ResourceGroup <rg> -LandingStorageAccount <account> -AiProvider azureopenai
```

That is the whole procedure. It provisions a Foundry resource with two deployments and grants
the shared managed identity `Cognitive Services OpenAI User` on it; there is no key to set
afterwards and no follow-up command to run.

Two things can still go wrong, and both fail loudly rather than silently:

- **Quota.** Some subscription tiers have none for the GPT-5.6 family and the deployment is
  rejected outright. Request capacity in the portal, or point `JP_AI_BULK_MODEL` and
  `JP_AI_WRITING_MODEL` at models you do have.
- **Nothing is extracted retroactively.** The daily path only queues postings whose text is
  new or changed, so the existing corpus needs a backfill — see *Backfilling* above. It is
  limited per call on purpose: the first run would otherwise queue everything, and that bill
  should be a decision rather than a side effect.

Without a provider configured the platform still works. Deterministic enrichment runs, matches
are still scored — the scorer is pure arithmetic over the concept graph — and only the
judgement layer and the generated documents are absent.

## Public repository

**There is no secret in this design at all**, and that is now unqualified:

- Azure SQL is **Entra-only** (`azureADOnlyAuthentication`) — no SQL login exists.
- Cosmos DB has **local auth disabled** — the keys are not usable.
- Storage uses **identity-based connections**, with `allowSharedKeyAccess: false` on the
  Functions host account.
- CI authenticates by **OIDC federation** pinned to this repo's `main` — no client secret.
- The API validates **Entra bearer tokens** and reaches both databases with the shared
  managed identity — no inbound key, no outbound connection secret.
- The API image is **public on GHCR**, so pulling it needs no registry credential either.
- **Azure OpenAI has local auth disabled** — the same treatment Cosmos gets. The models are
  reached with the shared managed identity, so a key leaking would be a key that authenticates
  nothing.

There used to be exactly one exception: an Anthropic API key, in a Key Vault, set out of band
so it appeared in no template or deployment history. Moving to Azure OpenAI removed the need
for it, so the vault module was deleted rather than kept well-managed — which is a better
outcome than the careful handling it replaced.

What still needs care, and how it is handled:

- **Identifiers.** `infra/main.bicepparam` reads subscription, tenant and resource names
  from environment variables. Nothing identifying is committed.
- **Scraped PII.** Real exports contain populated `emails` and descriptions carrying
  recruiter contact details. No scraped output is committed; the test fixture is synthetic.
- **Candidate PII.** The profile tables hold somebody's employment history, contact details
  and salary expectations. Every read and write is scoped to the caller's own `oid`, and the
  repositories take a subject id rather than a profile id so an endpoint cannot be written
  that reads a stranger's record by mistake. `DELETE /api/v1/profile` erases the profile,
  every match and every generated document — a system that stores an employment history
  without offering a way to remove it is not one anybody should hand a CV to.
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

The API is built, tested and deployed to Container Apps: its tests cover the route surface,
the authorization rules, the Semantic Kernel composition and its response handling. It
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

The candidate profile, matching and generated applications are built on top of that: a
form-filled profile extracted into the same concept vocabulary as a posting, a pure scorer
that runs over every pair nightly, a model pass over what clears the threshold, and a tailored
CV and cover letter rendered to PDF. 365 tests cover them, and moving the provider to Azure
OpenAI removed the last secret in the system on the way through.

Still to come, per the architecture in `model.md`: a Cosmos change-feed function driving
Web PubSub for live metrics (the `leases` container is already provisioned). And when Azure's
Global Batch matrix picks up the GPT-5.6 family, the extraction pass becomes a candidate for
the 24-hour batch discount on top of the per-call packing it already does.
