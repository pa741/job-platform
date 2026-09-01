# MCP server

Exposing this system over MCP, for whoever picks it up next.

Conventions and the standing rules live in [`CLAUDE.md`](CLAUDE.md); the matching work's state
lives in [`HANDOFF.md`](HANDOFF.md). This file is one feature, and **the open work comes first**
because that is what someone picking this up needs. What was built, and what was verified, is
below it.

**Built on 2026-08-31: the submission aggregate, its dashboard page, and the read-only tool
surface.** The write tools and the questions channel are deliberately not built - see section 2
for what each is waiting on. Nothing here has been exercised against a real client yet; section 4
is the sequence for doing that.

**The one measurement this plan turned on came back saying the signal is broken.** Section 0.3 of
the original plan asked what share of the corpus is board-hosted, and said not to guess it. The
answer is that **100.0% of 4,470 LinkedIn postings read as board-hosted and the scraper's apply-URL
selector has stopped matching** - so the question cannot be answered until that is fixed, and
nothing here infers `Board` any more. Section 1.2 has the evidence and what changed because of it.

**The MCP layer was the small part, as predicted.** `MapMcp()` over the existing repositories is
about forty lines. What did not exist was any record that an application was *sent*, and that was
most of the work.

---

## 1. Open work, in the order worth doing it

### 1.1 Run it against a real client

Nothing here has met an MCP client. The sequence is in section 4 and it needs a deployed API, a
migration dispatched by hand, and a token. **Do this before writing any more tools.**

The measurement below is done, and it moved the design rather than confirming it.

### 1.2 The apply link: LinkedIn stopped publishing it, and what replaced it

**Measured 2026-09-01 with `dbadmin apply-links`**, over postings last seen in seven days:

| site | postings | no direct link | share | detail page read | link is a board's own |
| --- | --- | --- | --- | --- | --- |
| linkedin | 4,470 | **4,470** | **100.0%** | 98.4% | - |
| freehire | 964 | 0 | 0.0% | 100% | 0.0% |
| indeed | 798 | 0 | 0.0% | 100% | 7.4% |

**100.0% of 4,470 is not a hiring market**, and the detail-page column says which of the two
causes it is. Section 0.2 named them - a broken selector, and `linkedin_fetch_description` off so
nobody looked - and called them indistinguishable. They are not: the page was **read** on 98.4%
of those postings, so the scraper looked and the URL was not there.

Confirmed directly against the live guest pages: `<code id="applyUrl">` is gone, every apply
redirect endpoint answers 404, there is no JSON-LD, and **a LinkedIn guest job page now contains
no non-LinkedIn URL anywhere on it**. The apply URL is not recoverable without authenticating.

**Indeed and freehire are fine** and always were: 92.6% of Indeed's stored links point at a real
external ATS rather than back at a board. Only LinkedIn broke.

#### What was built instead

LinkedIn still says *whether* a job is offsite, in two independent places - the apply button's
`offsite-apply` icon and the sign-in modal's `apply-link-offsite` impression id. So the fork now
emits **`offsite_apply`**, a three-state column, and the platform stores and uses it:

| layer | change |
| --- | --- |
| `JobSpy` fork | `JobPost.offsite_apply`, `LinkedIn._parse_offsite_apply`, added to `desired_order`, 8 tests on synthetic markup |
| `JobCsvParser` | reads `offsite_apply`; absent stays null |
| `JobPostings` | `OffsiteApply bit NULL`, migration `AddOffsiteApply`, part of `HasMaterialChange` |
| `SubmissionChannel` | flag first, URL as fallback: `true`/URL -> `Ats`, `false` -> `Board`, neither -> `Unknown` |

Verified end to end against live LinkedIn: 6/6 unfiltered postings came back `offsite_apply=True`
and 6/6 under LinkedIn's own `f_AL=true` Easy Apply filter came back `False`.

**Null is a third state and must stay one.** It means nothing was established - the detail page
was not fetched, the board does not say, or the posting predates the column. Reading it as "the
board hosts it" is the exact fault this replaced.

#### Still to do, and it is not in this repository

1. Tag the fork and ship it - see section 4.3. **Until that lands, `offsite_apply` is null for
   every posting** and `list_applyable` reports `Unknown` where it will later report `Ats`.
2. **Do not add `offsite_apply` to `JobCsvParser.TrackedColumns` until the deployed scraper emits
   it.** `JobDigestFunction` warns on any tracked column at 0% fill, and "not shipped yet" is not
   the regression that warning exists to catch.
3. Re-run `dbadmin apply-links` after the first scrape on the new image. The board-hosted share
   becomes measurable for the first time, and only then is "does the board path matter" answerable.

### 1.2b Could an authenticated session get the URL back? Researched 2026-09-01

Asked because the obvious next move is dedicated accounts used only for the job detail fetch.
Short answer: **technically yes, and it is the wrong trade at this scale.** The evidence, so the
next person does not re-derive it:

**It is not a proxy problem.** The 4,470 postings above were scraped *through DataImpulse
residential IPs* - `config.yaml` routes linkedin and indeed through `PROXIES` - and still got
zero. Rotating residential exit nodes does not restore the link, so nothing short of an
authenticated session changes the answer.

**The authenticated endpoint exists and still carries the field.** `/voyager/api/jobs/jobPostings/{id}`
answers **403 "CSRF check failed"**, not 404 - it is gated, not gone. Its `applyMethod` is
documented in the wild as `{"companyApplyUrl": "...", "type": "OffsiteApply"}`, which is exactly
the value that used to appear in `<code id="applyUrl">`. A session cookie plus a matching
`csrf-token` header would very likely return it.

**What it would cost:**

| | |
| --- | --- |
| Terms | Authenticated automation breaches LinkedIn's User Agreement outright, unlike scraping the signed-out pages |
| Accounts | The consistent reporting is permanent bans on detection, via fingerprinting, rate heuristics and IP reputation |
| Throughput | ~100-200 detail views per account per day against ~640 LinkedIn postings a day here, so 3-6 accounts running continuously just to keep pace |
| Upkeep | `li_at` expires in weeks and cannot be refreshed unattended, so the pipeline acquires a manual step whose failure mode is silent - the exact shape of bug this section exists because of |

**What is already free, measured rather than assumed:**

- **`offsite_apply` gives the route for every posting at no risk**, which is most of what the
  agent surface needs: "the employer takes this one, the posting says where."
- **6.4% of the missing links (285 of 4,470) are already in the database**, on a sibling posting
  from another board, reachable by matching title and employer.
- **freehire and Indeed already carry real links for 1,762 postings**, 92.6% of Indeed's pointing
  at a genuine external ATS.

**And a latent bug found while measuring it, now fixed.** `JobFingerprint.ContentHash` folds
location into the hash, and boards write locations differently - "London, England, United
Kingdom" against "London, UK". Corpus-wide it matched across boards **zero** times in 30 days,
so `RunCounts.CrossSiteDuplicates` had reported 0 since it was written. `CrossBoardKey` now
parses the city out first; `ContentHash` is untouched because embedding staleness compares it.

**Both quick wins are built.** The recovery is live in `ListApplyableAsync`: where a posting has
no link and the same job on another board does - matched on title, employer **and city** - the
shortlist serves that link and marks it `MatchedOnAnotherBoard`. Measured on the live corpus that
is 211 of 4,470, and the city qualifier is what keeps it honest: title and employer alone matched
285, so 74 were one employer advertising one title in several cities.

**If it is built anyway**, the shape that limits the damage: opt-in and off by default
(`LINKEDIN_COOKIES` unset changes nothing), used only on the detail fetch and never on search,
one account per proxy exit, and **falling back to `offsite_apply` rather than failing** - so a
banned account degrades to today's behaviour instead of stopping the run.

### 1.3 The write tools

`create_submission` and `record_event`. **Add only once section 1.1 has actually happened** - the
handoff's original ordering, and the reason has not changed: nothing on the read surface can be
damaged by a client that misbehaves, and these can.

Most of the work is already done. The schema carries both idempotency guarantees
(`UNIQUE (ProfileId, PostingId)` and `UNIQUE (SubmissionId, IdempotencyKey)`),
`SubmissionRepository.CreateAsync` and `AddEventAsync` already converge rather than duplicating,
and `POST /api/v1/submissions` and `.../events` already exercise both. A write tool is a thin call
onto those.

**What is not built and must be, at the same time: a daily cap on `Submitted` events.** A client
that loops applies to four hundred jobs under somebody's name, and the recovery is four hundred
emails. Bound it **in `SubmissionRepository`, not at the call sites**, for the reason
`AiCallRecord.Create` is the only constructor - a rule enforced at two call sites survives until
somebody adds a third. Note the cap belongs on the event type, not on submissions: recording that
a hundred applications exist is fine, and claiming a hundred were sent today is not.

### 1.4 The questions channel, and the defect in the original plan for it

`ask_candidate` persists and returns immediately; the dashboard renders the question;
`get_answers(since)` collects replies. A tool that blocks until a human answers holds a client
session open for hours and loses the question when that session dies. MCP's multi-round-trip
requests would technically permit the blocking shape and should still be refused.

**The original plan said to reuse the Realtime path, and that plan is wrong as written.**
`IRealtimeFeed.PublishAsync` **broadcasts to every connected client** - its own remarks say so:
*"the current feed broadcasts, because every message on it is about the system rather than about a
person."* An AI failure is about the system. A question addressed to one candidate is not, and
sending it down the existing channel would deliver one person's question to every signed-in
dashboard.

`NegotiateAsync` already takes a `subjectId` and passes it through unused, so the fix is a
per-user send rather than a redesign - Azure SignalR's Management SDK addresses users directly.
But **it is work this plan did not cost**, and section 1.4 must not be started as though the
transport were free.

---

## 2. What is built

### 2.1 The submission aggregate

**A submission, not an application.** `ApplicationDocuments` already means generated drafts here,
and `Candidacy` is taken by `CandidacyAssessment` and `ICandidacyAssessor`.

```
Submissions       Id, ProfileId, PostingId, Channel, ApplyUrl, CreatedAtUtc
                  UNIQUE (ProfileId, PostingId)

SubmissionEvents  Id, SubmissionId, AtUtc, Type, Stage, Source, Note, IdempotencyKey
                  UNIQUE (SubmissionId, IdempotencyKey)
```

Migration `20260831170039_AddSubmissions`. Profile side cascades, posting side is `Restrict` -
the same asymmetry `JobMatchEntity` has, and SQL Server refuses two cascade paths anyway.

**The event log is the record and the status is a fold over it.** `SubmissionState.Fold` is pure
and Azure-free like `MatchScorer`, which is what makes its answers assertable exactly. Three rules,
each with a test written against the version of it that is wrong:

- **A terminal event wins outright**, and between two terminal events the later one takes it. Not
  "the latest wins": a rejection followed by an automated "thanks for applying" stays a rejection.
- **Otherwise the furthest-advanced phase wins, not the most recent.** These events arrive from a
  client reading an inbox, so they are late and out of order routinely, and a late `Acknowledged`
  must not walk an `OfferReceived` backwards.
- **`Stale` is derived, never stored**, and a closed application is never stale.

**`Type` is the phase; `Stage` is a label inside it.** "Tech round 2" is text on an
`InterviewScheduled` event. `SubmissionStateTests.The_phase_ordering_the_fold_depends_on_is_the_process_order`
pins the numbering the fold leans on.

**`Source` says who wrote the event**: `Candidate`, `Client`, `Email`.

**No deletes and no status column.** Withdrawing is a `Withdrawn` event.

### 2.2 The dashboard page

`web/src/pages/Submissions.tsx`, grouped by phase, with the event log expandable per row and a
form that appends to it. Built before the tools, deliberately: if the pipeline is not legible to a
person, an agent writing to it is writing somewhere nobody is looking.

### 2.3 The read-only tool surface

`ModelContextProtocol.AspNetCore` 2.2.0, in `JobPlatform.Api` as `Features/Mcp/` - one
`IEndpointGroup`, one line in `EndpointGroupExtensions`, one `AddMcpFeature()` in `Program.cs`
beside `AddRealtimeFeed` and `AddAiProvider`. A separate service would have needed its own SQL
user and its own role assignments.

Stateless sessions, set explicitly. `WithTools<SubmissionTools>()` rather than
`WithToolsFromAssembly()`, so a class gaining an attribute is not a new public tool nobody
reviewed - `McpEndpointTests` asserts the surface is **exactly** four tools, which is an equality
rather than a superset precisely so that an added one turns the build red.

| Tool | Notes |
| --- | --- |
| `list_applyable(channel?, limit)` | Gated on `Verdict >= Possible`, no submission yet, ordered by `RankScore`. Projects `JobUrlDirect` into `Channel`. |
| `get_submission_pack(postingId)` | Advert text, apply URL, and the latest CV and cover letter as markdown. |
| `get_form_field(name)` | **One** answer, from `FormFieldCatalog`. Call with no name to list what is allowed. |
| `list_submissions(phase?, since?)` | Folded status per application. |

**`list_applyable`'s threshold is its own constant and gates on the verdict, not on a score cut.**
`MatchRanker.FusionFloor` is where the embedding earns its weight and
`MatchSweepFunction.AssessmentThreshold` is where a model judgement is worth buying; "worth
applying to" is a third question, and `CLAUDE.md` records that collapsing the first two into one
was already a mistake. The reason it is the verdict is the whole finding behind `MatchRanker`:
**the score is a good filter and a bad final sort** - flat inside its own top band, on fresh
labels. `Unknown` is excluded and so is unassessed; they are not the same thing.

**Its own rate-limiting policy**, `RateLimitSetup.McpPolicy`, at 20 requests a minute against the
dashboard's 120. These tools read SQL, and `CLAUDE.md` is explicit that a polling path against it
exhausts the monthly grant.

### 2.4 The disclosure log

`get_form_field` and `get_submission_pack` both record what was asked for - **never the value** -
to the `mcpDisclosures` Cosmos container, day-partitioned with a ninety-day TTL, through
`IDisclosureLog` following `IAiCallLog`. `DisclosureRecord.Create` is the only constructor, so the
bounds cannot be skipped at a call site.

Its own container rather than a discriminator inside `aiCalls`: the two answer different questions
- "did the nightly passes lose anything" against "what of mine has left the system" - and their
retention is not one decision.

Not App Insights. Sampling is on with `excludedTypes: "Request;Exception"` and none of these calls
throws, so traces are sampled exactly where the record matters.

---

## 3. Deliberately absent

Worth stating, because each will look like an omission to whoever reads the tool list next.

- **No `submit_application`.** The server records that something was submitted; it never submits.
  Applying is irreversible and outward-facing, and keeping it outside means no bug in this
  repository can send anything to an employer. `McpEndpointTests` fails if one appears.
- **No `get_profile`.** Tool results are transcript content wherever the client runs, and may be
  retained there. The profile is employment history, contact details and salary expectations - the
  same data `AiLedger:RecordPrompts` is off by default for, and the same data the OpenAI batch path
  is fenced away from.
  **`get_submission_pack` is the honest exception and is treated as one.** A tailored CV is the
  profile rewritten in prose. It is returned because an agent filling a form needs it, and it is
  logged on the same terms as `get_form_field` rather than treated as a public-text read.
  `FormFieldCatalog` carries no date of birth, nationality, address, salary expectation or
  referee. A form will ask for some of those; a person types them, because a field an agent
  cannot fill is a field an agent cannot get wrong on somebody's behalf.
- **No deletes.** An append-only log with no eraser is the only version worth auditing.

---

## 4. How a client connects, and how to verify it

**Question 5.1 is answered for Claude Code, and the answer was already three-quarters inside this
repository.** Entra supports no Dynamic Client Registration, so pre-registration is the only
official route - and `scripts/setup-api-app.ps1:106-123` already writes
`preAuthorizedApplications` for the Azure CLI's fixed first-party app id against the `Jobs.Read`
scope. So a token from the CLI is a valid token for this API, and no OAuth code is needed at all:

```bash
# the appId is printed by scripts/setup-api-app.ps1
export JOB_PLATFORM_TOKEN=$(az account get-access-token \
  --scope "api://<appId>/Jobs.Read" --query accessToken -o tsv)

claude mcp add --transport http job-platform https://<api-fqdn>/api/v1/mcp \
  --header "Authorization: Bearer \${JOB_PLATFORM_TOKEN}"
```

The `${VAR}` form is expanded at request time, so no token is written into a config file.
**The token expires hourly and must be re-minted** - the cost of taking the pre-authorised path,
and the right trade while the surface is read-only.

**A browser-based client is the upgrade, and it is more work**: a 401 carrying
`WWW-Authenticate`, a Protected Resource Metadata document, and that client's fixed app id added
to `preAuthorizedApplications`. The SDK ships `AddMcp()` on the authentication builder and
`McpAuthenticationOptions.ResourceMetadata` for exactly this. It is not built.

**Deploying this is two steps and the second is not optional**, per `CLAUDE.md`:

```bash
git push                                           # deploys the code
gh workflow run deploy.yml -f run_migrations=true  # then the schema
```

Dispatch the migration **after** the last push - the concurrency group cancels a *pending* run
when a newer commit queues behind it. Between the two, `/api/v1/submissions` answers 500 and the
smoke test will not catch it.

Then, in order:

1. `az containerapp list --query "[0].properties.template.containers[0].image" -o tsv` - confirm
   the deploy landed before trusting any behaviour change.
2. `dbadmin apply-links` - record the number in section 1.2.
3. Create a submission by hand on the dashboard and add two events. The page is the proof the
   pipeline is legible to a person.
4. Add the server to Claude Code and run `list_applyable` → `get_submission_pack` →
   `get_form_field` → `list_submissions`.
5. Read the `mcpDisclosures` container back. Confirm the field reads are there and **no value is**.
6. Confirm `/api/v1/mcp` answers 401 with no header against the live API.

---

### 4.3 Shipping the scraper fix

The fork is pinned by tag, so the fix reaches production through a release rather than a push.
Three repositories, in this order - **the tag must exist before the pin moves, or the image build
fails on a 404**:

```bash
# 1. the fork: commit on `patches`, tag, push
cd JobSpy
git add -A && git commit -m "feat(linkedin): record whether an application is offsite"
git tag v1.1.82-fh6
git push origin patches --tags

# 2. the scraper: move the pin, which rebuilds the image on the tag push
cd ../job-scrapper
#    edit requirements.txt: v1.1.82-fh5.tar.gz -> v1.1.82-fh6.tar.gz
git commit -am "chore: pin the fork to v1.1.82-fh6" && git push
git tag v1.6 && git push --tags     # the tag is what publishes to GHCR

# 3. the platform: code, then schema, then check
cd ../job-platform
git push
gh workflow run deploy.yml -f run_migrations=true
```

Watchtower pulls the new scraper image on the NAS; the next scheduled run is the first to emit
`offsite_apply`. Only postings scraped after that carry it - the column is null for the existing
corpus and will fill in as postings are re-listed.

## 5. Constraints that will bite

- **Every tool resolves the profile from the caller's token and never takes a profile id.**
  `SubmissionRepository` and `CandidateProfileRepository` both take a subject id and have no
  overload that does otherwise - the authorisation boundary expressed as a type. It matters more
  here than on a route, because a tool's arguments are named by a model rather than by a router:
  an unused `profileId` parameter is exactly what a model would helpfully fill in.
- **The caller comes from `RequestContext.JsonRpcRequest.Context.User`, not from
  `IHttpContextAccessor`.** The SDK populates the principal per message, which survives whatever
  async boundaries the transport introduces where an `AsyncLocal` may not. Read `oid` through
  `CallerIdentity.SubjectId`, never `ClaimTypes.NameIdentifier`, which resolves to `sub` and is
  pairwise per application.
- **No output cache on any of it.** Every tool is per-principal, and a shared cache keyed on a URL
  with no user in it is how one person is served another's pipeline.
- **`Api:AllowAnonymousReads` must not reach these routes.** Pinned as endpoint *metadata* in
  `McpEndpointTests` and `AuthorizationTests`, not only as a 401 - **the behavioural test alone was
  found not to pin what it claimed.** Every handler also calls `TryGetSubjectId`, which answers 401
  when the token carries no `oid`, so swapping the policy for `PublicReadPolicy` left the
  behavioural cases green. Defence in depth working, and a test measuring the second layer while
  describing the first.
- **`../model.md` is amended**, with a `Submissions` row and an `Agent surface (MCP)` row. It is
  binding; it was amended before the code was written, not after.
