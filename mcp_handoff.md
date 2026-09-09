# MCP server

Exposing this system over MCP, for whoever picks it up next.

Conventions and the standing rules live in [`CLAUDE.md`](CLAUDE.md); the matching work's state
lives in [`HANDOFF.md`](HANDOFF.md). This file is one feature, and **the open work comes first**
because that is what someone picking this up needs. What was built, and what was verified, is
below it.

**Built and deployed: fifteen tools over an answer store, a server-side resolver, parking, cross-board
clustering, a candidate-authored CV library and stored document packs.** The surface was six tools
and read-mostly. Section 2 says what each piece is; sections 2.9 and 2.10 are the two features added
after the apply loop itself.

**Deployed on `main`, and the schema went first every time.** Four migrations have been applied by
hand - `AddApplyLoop`, `AwaitingQuestion`, `CvLibrary` and, when it lands, `ApplyLinkRecovery`. The
order matters and is not interchangeable: `deploy.yml` runs its migrate job *after* the container
swap, so a dispatch would start the new image against a schema without the columns it selects. Both
blob containers exist - `application-packs` and `profile-cvs`.

**Nothing here has still been driven by a real MCP client.** The first version of this file said that
of six tools; it was true of fourteen and it is true of fifteen. Every one has been called by tests
and by hand over curl, and by nothing that retries, crashes, resumes or gets an argument wrong.
Section 1.3 is what to watch the first time one does.

**What was the blocking measurement is no longer blocking.** `ApplicationDocuments` held one row for
the entire system, which is why the nightly generation pass exists; it holds ten now and fills on its
own. The constraint moved: of 382 postings worth applying to, **73 carry an employer apply link and
all 309 that do not are LinkedIn**. Eight applications are complete and unsent. Section 3 has the
numbers, 3.2a is what is being done about the link, and the only thing between eight ready packs and
eight sent applications is a browser client that lives outside this repository on purpose.

---

## 1. Open work, in the order worth doing it

### 1.1 Deploy it, and the three steps are not interchangeable

Nothing on `feat/apply-loop` has reached the subscription. `deploy.yml` triggers on `main`, so
merging is the first step and the other two follow it:

```bash
git push                                           # deploys the code
gh workflow run deploy.yml -f run_migrations=true  # then the schema
dotnet run --project tools/JobPlatform.DbAdmin -- backfill-crossboard "<conn>" --confirm
```

**Dispatch the migration after the last push, not before it** - the concurrency group cancels a
*pending* run when a newer commit queues behind it, and a dispatched migration run is pending
while the push ahead of it deploys. Between the code landing and the schema landing, every read
that touches `Submissions` answers 500: the projection selects columns the database does not have
yet, and the smoke test does not go near them.

**`backfill-crossboard` is not optional and its failure mode is silent.** `CrossBoardKey` is null
for the whole existing corpus until it runs, and a null key deliberately clusters with nothing -
so the queue looks entirely correct and simply never collapses a duplicate. It is a console
command rather than a step in the migration because the key is C#: `JobFingerprint.CrossBoardKey`
parses a city out of a free-text location and folds case and punctuation, and a second
implementation in T-SQL would drift from the one ingest uses on the first posting either spelled
differently. Dry run unless `--confirm`, idempotent, and re-running it is also how the corpus is
re-keyed after a change to the normalisation.

**The infrastructure deploy is what creates `application-packs`** - its own container with its own
scoped role assignment, never a widened account-wide grant - and sets
`ApplicationPacks__serviceUri` on the container app. Until it lands, `get_submission_pack` returns
the markdown, no links, and a `note` saying which of the two reasons applies. That is a capability
the deployment does not have rather than a dependency it is missing, and it is the same shape
`AddAiProvider` and `AddRealtimeFeed` take.

**An unattended client authenticates app-only, and needs one configuration entry to mean
anything.** `Mcp__AppPrincipals__<service principal object id>` set to the candidate's object id
is what says whose pipeline that principal acts on; both halves are directory object ids, neither
is a secret, and both are tenant identifiers, so they are deployment configuration and not
committed. An unmapped app-only token is told so specifically, because "the server is not finished
deploying" and "this candidate has not filled the form in" otherwise produce the same empty answer
and want opposite fixes.

### 1.2 Generation, which is what the loop is actually waiting on

The queue is not the constraint. Section 3 has the measurement: **one posting in the entire
database has generated documents**, and the gate a careful run would compose returns nothing at
all. A loop pointed at this surface today either applies without a tailored CV or stops.

**What exists.** Documents are written one posting at a time by
`POST /api/v1/applications/{postingId}`, synchronously, from the dashboard - and generation now
renders the CV to PDF and DOCX and the cover letter to PDF, stores them under `application-packs`,
and records the paths and `CvSha256` on the row. So a document written from now on arrives with
files; the single row that predates this change has none, and the pack says so rather than
failing.

**What does not exist is a pass that generates without a person clicking.** `model.md` names no
scheduled generation, so adding one is an amendment first and a function second. Two things to
settle before writing it, because neither is a detail:

- **It costs the expensive deployment.** `gpt-5.6-sol` is roughly 25x the bulk model per token and
  this is the one artefact a human reads. 245 postings are applyable for the one profile today, so
  a corpus-wide pass is a decision about money rather than a batch size. Bound it, order it by the
  queue's own ordering, and put the bound where the daily cap already is - in the repository, not
  at the call site, for the reason `AiCallRecord.Create` is the only constructor.
- **Silence is the failure mode this system already has.** Every AI path here degrades quietly,
  which has cost real work three times; a generation pass reports to `IAiCallLog` the way
  `KernelCandidacyAssessor` does, or it fails as a count nobody is comparing to anything.

**The drafted free-text answers are now written, and `resolve_form_field` can finally see them.**
The column (`DraftedAnswersJson`), `DraftedAnswerCatalog`, the repository overload and the pack's
`draftedAnswers[]` projection were built first and `KernelApplicationWriter` produced none; it now
drafts the catalogue's `PerPosting` questions alongside the documents, and the generation pass adds
the one `StableAnswers` entry that needs no model.

**What stayed broken after that, and was found by a model driving the surface rather than by a
test:** nothing passed those answers to the resolver. `DraftedAnswer` states that its question text
is the catalogue's wording and not the form's - *"What draws you to us?"* and *"Why do you want to
work at this company?"* are one question sharing no words - and that matching a live field to one
of them is `resolve_form_field`'s job, but `FormFieldRequest` had no field for them. So the pack
carried the answers, the resolver reported `stage: "None"`, and a run resolving its fields parked
`MissingAnswer` over the five questions the expensive deployment had already answered for that
posting. It is a fold match ahead of the cache, plus a place in the model's shortlist for the
wordings a fold cannot catch, and a drafted answer is never cached because that table is keyed on
the question rather than on the posting.

**Until generation catches up, the honest instruction to a run is to relax the gate rather than to
read an empty queue as a fact about the market.** `documentsReady: true` returns one posting;
`list_applyable` with no filters returns 245.

### 1.3 Connect a real client, and what to watch when you do

The sequence is section 6: a token, `claude mcp add`, and the checks at the end of it. **Do this
before writing a fifteenth tool.** None of the following has been exercised outside a test host,
and each is a place where a first real session says something a test cannot:

- **`create_submission` with `sent: true`** is the call the whole write path was reshaped around -
  the submission and its `Submitted` event in one write, because a submission created by one call
  and evidenced by a second has a window in which the application exists in the world and not in
  the log. Read `result`: `DailyLimitReached` means **nothing was written at all, not even the
  submission**.
- **`create_submission` on a posting already submitted** answers `created: false` and leaves the
  original channel and URL alone rather than overwriting where the first said the application
  went.
- **`record_event` retried under one key** answers `AlreadyRecorded` and appends nothing. The
  idempotency check runs *before* the cap deliberately: a client retrying a write it is unsure
  landed must not be refused for a quota that very event already spent.
- **The daily cap** is `SubmissionLimits.MaxSubmittedPerDay` (25), counted by the event's own
  `AtUtc` across every submission, so it cannot be sidestepped by spreading writes over postings
  or by backdating them. The burn-down is now reported by `list_applyable`, by `record_event`, and
  by `create_submission` on the arm that spends it - watch it fall rather than discovering the cap
  by being refused, which by the loop's design happens after the form has already gone.
- **`Source` on every event and every answer written by a tool is `Client`**, never `Candidate`.
  If the dashboard starts showing agent-written events as "you", that is the bug.
- **A park must come back on the right terms**, and one of the three cases is looser than the tool
  says. `Expired` and `Duplicate` never return; `MissingAnswer` is held while its `OpenQuestion` is
  unanswered; and **a retryable park excludes nothing at all** - `Captcha`, `LoginRequired`,
  `AccountRequired`, `FormError` and `OutOfQuota` are not in the predicate, so a second
  `list_applyable` *inside the same run* hands the posting straight back, while `park_application`
  answers "it returns to list_applyable on the next run, so there is nothing to retry now". A pass
  that lists once and works the page is unaffected; a pass that re-lists after each application
  meets the same blocked posting every time, which is the loop parking exists to stop. Watch for it
  on the first real run: either the queue has to know what a run is, or the note does. (A park
  landing on a submission that already carries events is refused outright - that application was
  made rather than blocked.)
- **Applying to one member of a cluster must suppress the whole cluster.** Three duplicate pairs
  sat in the top twenty rows on 2026-09-02, so this is not a rare path. It is what stops a second
  application to the same vacancy, and the recruiter sees both.
- **`resolve_form_field` abstains often, and that is the design working.** What is worth measuring
  on a real form is the *second* occurrence of the same question: it should resolve out of
  `FormAnswerResolutions` with no model call at all.
- **The pack's document links expire** - fifteen minutes by default. A client that stores one will
  retry with it later and report the failure as a missing document.
- **The rate limit is `RateLimitSetup.McpPolicy`** - a burst of 40 calls over a sustained 20 a
  minute, an order of magnitude below the dashboard's, because these tools read SQL and SQL is
  billed on wall-clock time against a monthly grant. A token bucket rather than a fixed window,
  because one application *is* a burst - list, pack, resolve a dozen fields, create, record - and a
  window refuses the tail of it while a bucket lets the burst through and still bounds the day. It
  refuses rather than queues: a queued tool call is a client that has stopped and cannot say why,
  where a 429 is something an agent can read, wait on, and retry under the same idempotency key.
- **Read `mcpDisclosures` back afterwards.** The field reads should be there, one record per field
  for the batch tool exactly as for the singular one, and **no value should be**.

### 1.4 The candidate can answer now, but has to go and look

**Built on 2026-09-04.** `GET /api/v1/questions` and `POST /api/v1/questions/{id}/answer` exist, the
dashboard has a Questions page, and the answer route is the one write in this system that may stamp
an answer as the candidate's own - the source comes from the token and there is deliberately no
field for it in the body. `Applications.tsx` was taught about parking in the same change; it had
been counting any row with a non-null phase as sent, which would have counted a parked application
as one that went out and then rendered it nowhere.

**What is still missing is the push, and it is what "unattended" turns on.** A parked application
waits until somebody opens the page. The realtime feed cannot be reused as it stands: `PublishAsync`
broadcasts, which is right for an AI-failure notice and wrong for one candidate's question, so
sending one down it would show that question to every signed-in dashboard. `NegotiateAsync` already
takes a `subjectId` and passes it through unused, so a per-user send is an addition rather than a
redesign - but it is an addition, and until it exists the loop's escalation channel is a page
somebody has to remember to visit.

**Two things about answering are worth knowing before the first real question arrives.** An answer
is superseded rather than overwritten, so the store can still say what was submitted last year; and
a sensitive answer - salary, right to work, every EEO question - can only ever exist because a
person typed it, because nothing derived from the profile is allowed into that namespace at all.
Section 2.4 is the split.

## 2. What is built

### 2.1 The schema, in one migration

`20260902210436_AddApplyLoop`. One migration and not nine, because migrations here are dispatched
by hand and every dispatch is a step somebody has to remember.

```
FormAnswers            what the candidate has actually said, superseded rather than updated
FormAnswerResolutions  the resolver's cache: a hit here means no model call
OpenQuestions          one wording asked once, however many adverts asked it
Runs                   an unattended pass, and its account of itself
```

Columns: `Submissions` gains the park attributes, `ApprovedAtUtc`/`ApprovedBy`, `DocumentRevision`
and `RunId`; `SubmissionEvents` gains `ConfirmationRef`, `FinalUrl`, `ScreenshotRef` and
`SubmittedFieldsJson` - the **names** of the fields a run filled in and never the answers given to
them; `JobPostings` gains `CrossBoardKey` and its index; `ApplicationDocuments` gains
`DraftedAnswersJson`, the three blob paths and `CvSha256`.

**Answers supersede rather than update**, for the same reason the event log has no status column: a
store that overwrites cannot say what was submitted last year, and an application is exactly the
kind of claim somebody has to be able to reconstruct afterwards. The unique index is partial - one
live answer per question per scope - so the history is unbounded and the lookup is not.

**Every new string column's bound lives beside the validation** in `FormAnswerLimits` and
`SubmissionLimits`, so the schema and the refusal cannot drift apart.

### 2.2 The queue predicate, which is the single most important change

`ListApplyableAsync` used to exclude a posting when *any* submission row existed for the pair. That
is what made parking impossible - a park would have removed the posting permanently, which is the
opposite of what parking is for. What the predicate asks now, any one of which keeps the posting
out:

- **Has the candidate dismissed the match?** `DismissedAtUtc` was ignored here - a defect the plan
  did not mention and nothing had noticed. Every other match query honoured it; this one did not,
  so a posting the candidate had said no to came back to the agent's queue.
- **Is there a live application** - a submission that is not parked, or that has been unparked?
- **Is it under a permanent block** - `ParkReasonPolicy.Permanent`, which is `Expired` and
  `Duplicate`?
- **Is it parked on an answer**, which holds it only while its `OpenQuestion` is unanswered?
- **Has any member of its cross-board cluster been applied to?**

`ParkReasonPolicy` is the whole retry policy and it is one pure function in Core:
`Expired`/`Duplicate` never return; `Captcha`, `LoginRequired`, `AccountRequired`, `FormError` and
`OutOfQuota` return next run; `MissingAnswer` returns when the answer exists. `Permanent` and
`AwaitingAnswer` are *derived* from that function rather than written out a second time - they
exist only because a static call on a column does not become SQL, and a second spelling of the rule
is a second thing to go stale.

**Clustering is on the persisted `CrossBoardKey`** - title, employer and city, folded - and the
primary is chosen by apply-URL strength first and assessment score second. The live duplicates
prove both tie-breaks are needed: posting 3020 carries the only direct link and the *lower*
assessment, 85 against 3030's 92. The existing cross-board machinery could not be reused as-is,
because it requires `Site != Site` while two of the three live pairs are same-board, and it
requires equal `LocationCity` while "London" and "Greater London" are not equal.

**`AtsVendorDetector` reads query parameters as well as hosts**, because `?gh_jid=` is Greenhouse
behind an employer's own domain and a bare host list misses most of the corpus. `Aggregator` is a
distinct value from `Other` and the loop should skip it: a "direct" link into another job board is
another job board, and following it spends a day's cap finding that out by hand.

### 2.3 The tool surface

`WithTools<SubmissionTools>()` rather than `WithToolsFromAssembly()`, so a class gaining an
attribute is not a new public tool nobody reviewed. `McpEndpointTests` asserts the surface is
**exactly** these fourteen names - an equality rather than a superset - so a fifteenth turns the
build red. The `[Description]` on each tool and each parameter is the interface a model reads, so
it is documentation that changes behaviour.

| Read | |
| --- | --- |
| `list_applyable` | The queue. Gated on the model's verdict; filtered by `since`, `postedWithinDays`, `assessedSince`, `documentsReady`, `minAssessmentScore`, `applyUrlSource` and `channel`; ordered by `Rank`, `Score` or `AssessmentScore`. Collapses duplicate listings into one row with the rest in `alternatePostings`, and carries the quota block. |
| `get_submission_pack` | Advert text, apply URL and its provenance, the CV and cover letter as markdown, short-lived links to the rendered PDF and DOCX, the drafted answers, and the allowlisted profile entries. |
| `get_form_field` | One allowlisted answer. Call with no name to list what may be asked for. |
| `get_form_fields` | Several named ones in a round trip, refused name by name. One disclosure per field, exactly as if each had been asked for alone. |
| `resolve_form_field` | What to type into one field, or the reason a person has to. |
| `list_submissions` | The pipeline, folded from the event log, with the park attributes beside it. |
| `list_open_questions` | What the loop is waiting on a person for. |

| Write | |
| --- | --- |
| `record_form_answer` | Stores what the candidate answers, superseding the previous one, and closes a matching open question. |
| `create_submission` | Records that an application exists and - with `sent: true` - that it was sent, in one write. |
| `record_event` | Appends one event, with the evidence a browser can produce. |
| `park_application` | Puts a posting down and says why, and opens a question for `MissingAnswer`. |
| `start_run`, `finish_run` | Attribution for an unattended pass, and its account of itself. |
| `match_email_to_submission` | Which application a recruiter message is about. **It stores nothing** - it sits with the writes because it is the step before one and its failure mode is a write's. |

**`since` and `postedWithinDays` are different questions and a daily run wants the second.** The
first is when this system first read the posting - "what has arrived since my last run". The second
is when the employer posted it, read from the board's stated date where there is one and from
first-seen where there is not. They agree for most of the corpus and disagree exactly where it
matters: a search term added this week delivers hundreds of postings that are new here and three
weeks old to the market, and only the second rejects them. A negative window is refused rather than
ignored, for the reason the assessment floor is.

**`minAssessmentScore` is enforced server-side**, so a prompt-level bug cannot fire applications at
bad matches. **The quota block is planning and never a reservation** - nothing is held back, and
two clients sharing a candidate can each be told six.

**`list_applyable`'s threshold is still its own constant and still gates on the verdict rather than
on a score cut.** `MatchRanker.FusionFloor` is where the embedding earns its weight and
`MatchSweepFunction.AssessmentThreshold` is where a model judgement is worth buying; "worth
applying to" is a third question, and collapsing the first two into one was already a mistake once.

### 2.4 The answer store and the resolver

**Two namespaces that never mix.** `FormFieldCatalog` is *derived* - read from the profile,
allowlisted, and nothing sensitive is ever added to it. `FormAnswers` is *declared* - only what the
candidate typed, never anything read or inferred from the profile. That is a stronger guarantee
than marking fields `sensitive: true`, because it does not depend on a flag being set correctly: a
sensitive value can only exist here because a person wrote it.

**Resolution runs on this side of the tool call.** `IFormFieldResolver` has four stages and stops
at the first that decides: the exact allowlist name, the candidate's own stored answer by
normalised question, the cache on `(QuestionHash, OptionsHash)`, and only then a model - which is
asked to choose between the candidate's own answers and never to compose one. Three of the four
need no provider at all, so a deployment with no AI still answers from the allowlist, from what the
candidate has typed, and from what the same question resolved to before; the fourth abstains rather
than failing. Handing the answer store to the client to choose from would have been the
whole-profile disclosure this surface exists instead of, with an extra hop and a bill attached.

**Abstention is a first-class answer.** Below the confidence floor, for any sensitive question
without an exact stored answer, and wherever an option set cannot be mapped cleanly, the answer is
`needsUser: true` with no value. A sensitive answer is returned verbatim or not at all - never
mapped onto the nearest option. "Prefer not to say" is a stored value like any other.

**There is deliberately no `source` parameter** on `record_form_answer`. It is derived from the
token, because a tool argument naming the source is exactly what would let a model stamp its own
inference as the candidate's own words.

### 2.5 Parking and runs

**Parking is an attribute on `Submissions` and never an event**, and `SubmissionEventType` did not
grow. The status is a fold that takes the furthest-advanced phase, and there is no numbering under
which "a captcha stopped us" either advances or fails to advance an application without lying about
one of the two cases. So a park writes columns the fold never reads, the queue predicate reads those
columns, and `list_submissions` projects them. **A parked row is not a sent one**, and every reader
that counts sent applications has to know it - the dashboard included, which is 1.4's second bullet.

**A run is attribution, not a lock.** Starting a second does not close the first, nothing is
reserved, and the daily cap is counted across the day rather than per run. Its value is the
idempotency convention it hands out - `<runId>:<postingId>:<event type>` - so a client that crashes
and resumes converges on the same writes instead of duplicating them. `finish_run` tallies the
parks itself, so a client's own arithmetic cannot drift from the parks it actually made, and it
reports `unaccounted` - considered minus sent minus parked - which is the number that catches a run
that dropped postings somewhere it did not report. The first finish stands; a second answers
`AlreadyFinished` and rewrites nothing.

### 2.6 Documents

`MarkdownPdfRenderer` was not replaced. **DOCX is a second backend over the same parsed AST**
rather than a second template or a converter - several ATS vendors parse the upload, and a PDF says
where the ink goes while a DOCX still says what a heading is. `DeterministicOpenXmlPackage` is what
keeps the bytes reproducible.

Files are rendered at generation time, stored under `application-packs`, and handed out as
**short-lived user-delegation SAS URLs** minted with the API's managed identity - no account key is
stored, so the no-secrets property is intact. Each file is named after the candidate rather than
`cv.pdf`, because that name lands in a recruiter's file list. `CvSha256` is recorded over the
rendered bytes, so a file can be checked against the row afterwards: a path alone cannot say
whether what is at the end of it is still what was sent.

### 2.7 The disclosure log

Unchanged in shape and wider in coverage. `get_form_field`, `get_form_fields`,
`get_submission_pack` and `resolve_form_field` all record **what was asked for and never the
value**, to the `mcpDisclosures` Cosmos container, day-partitioned with a ninety-day TTL.
`DisclosureRecord.Create` is the only constructor, so the bounds cannot be skipped at a call site.
The batch read writes one record per field, so a review of what left this system does not have to
know which shape the caller happened to use. For an app-only caller the candidate and the principal
are recorded separately, because "whose data left" and "what took it" stop being the same answer.

### 2.8 What was verified

**1,259 tests green, against 739 at the branch point.** The ones worth knowing about, because they
are what a change here turns red:

| Test | What it pins |
| --- | --- |
| `McpEndpointTests` | The surface is exactly fourteen names; the authorization policy as endpoint *metadata* and not only as a 401 |
| `McpToolRefusalTests`, `McpToolPayloadTests` | Every refusal answers `{refused, reason}` rather than throwing, and the payloads carry what the descriptions promise |
| `McpAnswerSourceTests` | Nothing written through a tool can be stamped `Candidate` |
| `ApplyQueueTests` | The queue predicate and the projection agree - they are written out twice and must |
| `ParkReasonPolicyTests` | Every declared reason has a decision, so a new one is a red build rather than a silent `NextRun` |
| `PostingClusterTests`, `AtsVendorDetectorTests` | Both cluster tie-breaks; the query-parameter and aggregator cases |
| `FormAnswerStoreTests`, `OpenQuestionQueueTests`, `ApplyRunPersistenceTests` | Superseding, the partial unique indexes, and a run finished twice |
| `ApplicationPackStoreTests`, `MarkdownDocxRendererTests` | No storage configured registers no store; both of the container app's key spellings reach it; a link's lifetime is clamped at both ends. And the DOCX walk over the hostile-input table the PDF renderer already had |

**What no test verifies is section 1.3.** All of this runs against SQLite and a test host: no Entra
token, no MCP client, no browser, no employer.

---

### 2.9 The CV library, which took the model out of the CV

**Why it exists is a sentence that shipped.** Asked what else an employer should know, the writer
gave the candidate's citizenship - correctly, from their own summary - and then added *"I am an AI
and they should have seen this."* It was stored, served through the pack, and would have been typed
into Cloudflare's form under a person's name. A guard drops that class of sentence now, but a guard
is a net under a trapeze: a curated CV takes the model out of the document that matters most,
because it cannot invent a claim about work it is not writing about.

The candidate authors up to **six** markdown variants; the writer no longer produces a CV at all,
and keeps the cover letter and the posting-specific free text, which are short, genuinely
per-posting and cheap where the advert is already in hand. `get_submission_pack` **chooses**:
scoring every sendable variant over the shared concept graph, taking a winner only where it clears a
floor of 50 and beats the runner-up by 10, handing a genuine tie to the model among the top few, and
otherwise abstaining. Both constants are argued from `MatchScorer`'s own credit table rather than
picked - every partial-credit relation is worth under half, so a CV answering every requirement by
transferable ground alone tops out at 45 and cannot be sent on resemblance.

**An abstention carries the gap rather than a refusal.** The posting parks as `NoCvVariant` with the
concepts it wanted, `list_cv_gaps` and `/api/v1/cv-variants/gaps` pool those across everything
blocked and rank them by how many applications each would release, and writing that CV brings them
all back. Three things about it are easy to get wrong and are written down in the code: the park
needs its **own** conditional retry class rather than joining the one that waits on an answer,
because `ParkAsync` writes `AwaitingQuestionId` only for that class and a park with a null id falls
through to the fallback that holds a posting until every question is answered; the release covers
**any** recorded gap rather than all of them, because releasing sends nothing - selection re-runs and
re-parks - while requiring all of them strands a posting whose candidate wrote a CV covering most of
what was missing; and a `NoFit` carrying an empty gap set parks a posting **for ever**, so the
selector names the whole demand set when the difference is empty and `ParkAsync` refuses the state
outright.

A fourth, found by a model driving the surface: **the park is only written for a posting the queue
would offer.** The pack assembles for any matched posting - the dashboard opens one for a posting
the nightly assessment has not reached, and a client may name any id it holds - while both
`list_applyable` and the gap brief count only what was judged at least `Possible` and not dismissed.
Parked outside that set, the posting stood in `list_submissions` as blocked for want of a CV while
`list_cv_gaps` reported nothing blocked: the two reads a run summarises itself from, disagreeing
about the same row. `get_submission_pack` now declines the write and says which of the two it was,
and nothing is lost - a posting later judged applyable arrives through the queue, where the same
selection runs and parks it then.

Every application uploads `Firstname_Surname_Curriculum_Vitae.pdf` whichever variant was sent. The
variant's own name would tell an employer that a different CV is kept for other roles, and it
arrives in the file list before anybody opens the document - so the stable name is the last path
segment of the blob as well as the download header, because a client that saves a signed URL by its
path and ignores the header would otherwise leak it.

Variants live in their own container. A document id and a variant id are independent identity spaces
that both start at one, so under one container document 34 and variant 34 for one candidate address
the same directory - and since a chosen CV and a generated one now spell the filename identically,
the same blob.

### 2.10 Apply-link recovery, for the postings a board will not say

Of 382 postings worth applying to, 73 carry an employer apply link and **all 309 that do not are
LinkedIn**. Indeed and freehire publish one every time. Section 3.2a has the measurement and the
decision behind this; the short version is that no account is used anywhere, and the employer's own
applicant tracking system publishes the link on a documented, unauthenticated endpoint.

A nightly pass **learns** a board token from any direct link an employer already published,
**probes** a candidate from their name where none is held and **confirms** it before trusting it,
then fetches each known board once and matches its listings to that employer's link-less postings by
title and place. Greenhouse, Ashby, Lever and SmartRecruiters are verified live against real tokens
from this corpus. The learn step is free - no network at all - and it is where 122 of the 309 are.

**Three details carry the design.** Trust is `ConfirmedAtUtc`, a timestamp rather than a flag, so no
value nobody set can read as permission - and a learned board is stamped as it is learned, because
the link the employer published *is* the confirmation. The board's identity is vendor, token,
**region** and employer, because Lever serves European tenants from a separate host and API: fold
`jobs.eu.lever.co` into `jobs.lever.co` and the loser is asked of an endpoint that answers nothing,
which reads as "not on Lever" and is indistinguishable from being absent. And a posting is stamped
as checked even when nothing matched, or "no recovered link" means both *asked, nothing there* and
*nobody has asked* - the same two-nulls-one-column fault `OffsiteApply` exists to undo.

The recovered link sits beside `JobUrlDirect` and never over it, and reaches a client as a fourth
provenance, `MatchedOnEmployerAts` - ranked between a link the board published and one borrowed from
a stranger's listing. The link is real and the form opens either way, so nothing a browser sees
separates a good recovery from a bad one; only the provenance can.

**Workable is the one to expect least from.** Every Workable link in this corpus is
`apply.workable.com/j/{code}`, which names no board, so those employers are reachable only through a
probe - the weaker half of the feature.

## 3. Measured on the live database, 2026-09-02

This section is the reusable part of the file. The numbers are what moved the design.

| | |
| --- | --- |
| Postings in the corpus | **7,368** |
| Applyable for the one profile, no filters | **245** |
| Submissions ever recorded | **0** |
| Submission events ever recorded | **0** |
| Rows in `ApplicationDocuments` | **1** |
| `documentsReady` + `applyUrlSource: 'Posting'` + `minAssessmentScore: 80` | **0** |

**Zero submissions and zero events means every fold, every cap and every staleness rule in this
feature has only ever run against test data.** The dashboard page exists, the API route exists, and
nobody has used either. That is not an argument against them; it is why 1.3 is a list of specific
things to watch rather than a suggestion to try it out.

**The top twenty rows of the queue were seventeen jobs.** Three duplicate pairs - 3020/3030,
968/379 and 551/4961 - so roughly 15% of a page was one vacancy listed twice. That is what the
clustering in 2.2 is for, and why it was not left to a later phase.

**Sixteen of those twenty rows were `Unknown`/`BoardPosting`, and only four were directly
applyable.** A run that insists on an employer's own link is therefore working a queue a fifth the
size of the one it can see - and it should insist, because the alternative is spending the day's cap
discovering board pages by hand.

**And the composed gate returned nothing at all.** One posting in the corpus has documents, so
adding `documentsReady: true` to anything else empties the queue. **The bottleneck is generation,
not the tool surface**, which is why 1.2 sits above 1.3 - and it is why the tools were built to say
so out loud: `list_applyable` reports `hasDocuments` per row and a quota block beside it, so a run
can tell an empty queue from a spent one.

### 3.1 The apply link: answered, and settled

**The scraper fix landed.** On the first run with `v1.1.82-fh6` - 2026-09-01, 525 LinkedIn
postings, 100% of them classified:

| site | postings | direct url | offsite | easy apply | route unknown |
| --- | --- | --- | --- | --- | --- |
| linkedin | 525 | 0 | **317 (60%)** | **208 (40%)** | **0** |
| freehire | 136 | 136 | - | - | 0 |
| indeed | 32 | 32 | - | - | 0 |

**The board hosts about 40% of LinkedIn applications** - material, not negligible, and nothing like
the 100% the broken selector reported. An agent cannot drive Easy Apply from here either way;
`list_applyable` says which is which, which is the part that was missing.

**What the earlier 100% was**, kept because the diagnosis is the reusable half. All 4,470 LinkedIn
postings read as board-hosted while the job detail page had been fetched for 98.4% of them - so the
scraper looked and the URL was not there. Confirmed against the live pages: `<code id="applyUrl">`
gone, every apply-redirect endpoint 404, no JSON-LD, and no non-LinkedIn URL anywhere on a guest
job page. LinkedIn had stopped publishing apply URLs to signed-out clients; `offsite_apply` reads
its offsite markers instead, so the route survives where the destination does not. Indeed and
freehire were never affected.

Postings last seen by the 2026-08-31 run still read `route unknown`, and are reclassified when a
later search turns them up again because `OffsiteApply` is part of `HasMaterialChange`. **Read
`dbadmin apply-links` over one day, not seven** - a multi-day window mixes runs from both images,
and the route-unknown share looks alarming when it is only history.

### 3.2 Authenticated LinkedIn: researched, and decided against

The obvious next move is dedicated accounts used only for the job detail fetch. **The answer is
no**, and this is kept as a decision rather than as an open question.

**It is not a proxy problem.** Those 4,470 postings were scraped *through* DataImpulse residential
IPs and still got zero. **It would work**: `/voyager/api/jobs/jobPostings/{id}` answers 403 "CSRF
check failed" rather than 404 - gated, not gone - and its `applyMethod` carries exactly the value
that used to appear in `<code id="applyUrl">`.

| | |
| --- | --- |
| Terms | Authenticated automation breaches LinkedIn's User Agreement outright, unlike reading the signed-out pages |
| Accounts | The consistent reporting is permanent bans on detection |
| Throughput | ~100-200 detail views per account per day against ~640 LinkedIn postings a day, so 3-6 accounts running continuously just to keep pace |
| Upkeep | `li_at` expires in weeks and cannot be refreshed unattended, so the pipeline gains a manual step whose failure mode is silent - the exact shape of bug this file exists because of |

The cheap wins were taken instead and both shipped: `offsite_apply` gives the route for every
posting at no risk, and cross-board recovery returns ~5% of the missing links outright. **What
would reopen it** is a measured demand for the URL specifically rather than for the route: if
`list_applyable` is in daily use and the rows with a vendor but no employer link are the ones that
stall, that is evidence.

### 3.2a That condition fired, and the answer was a third option

**Measured 2026-09-07.** Of 382 applyable postings, 73 carry an employer link. All **309** that do
not are LinkedIn - Indeed and freehire publish one every time. So the demand is real and it is
entirely one board, which is exactly the evidence 3.2 asked for.

**The decision above still stands, and the risk has moved against it since it was written.** hiQ
ended with LinkedIn winning on *contract* rather than on the CFAA - an injunction, a $500,000
judgment and destruction of the scraped corpus - so a terms breach is actionable even over public
data. The 2026 enforcement wave targets *browser automation* specifically, which is the shape an
authenticated `li_at` fetch takes. And there is no sanctioned way round it: LinkedIn's Job Posting
API is write-only, for applicant tracking systems publishing *into* LinkedIn, and new partnerships
are closed. One line in 3.2 is now too generous, though: it says authenticated automation breaches
the User Agreement "unlike reading the signed-out pages". After hiQ, reading the signed-out pages is
also a breach - materially lower risk, not a different kind of thing.

**What 3.2 did not consider is that the employer will tell you.** It framed the choice as
authenticated LinkedIn or the 5% cross-board recovery, and there is a third route: ask the
employer's own applicant tracking system. Greenhouse, Ashby, Lever, Workable and SmartRecruiters all
serve public, documented, unauthenticated board listings that exist to be read by job seekers.
Verified live on 2026-09-07: `boards-api.greenhouse.io/v1/boards/cloudflare/jobs` answers 200 with
333 jobs, and carries the exact posting held here as 3020 - *VoidZero Engineer* - with the
`absolute_url` LinkedIn withheld.

| | |
| --- | --- |
| Link-less applyable postings | **309**, every one LinkedIn |
| At an employer whose ATS is already known from another posting | **122 (39%)** |
| Further employers resolved by probing a slug from their name | **21 of 120 (~18%)** |
| Employers corpus-wide on Greenhouse / Ashby / Workable / Lever / SmartRecruiters | **299** |

So roughly half the gap is reachable with no account, no proxy and nothing to defend - an order of
magnitude better than the cross-board recovery, and mostly a join over data already held.

**Two things to keep honest about it.** A slug probed from a company name produces false positives -
"Dex", "Kernel", "Fin" and "Orbital" are all real boards belonging to somebody, not necessarily to
the employer on the advert - so a probed token is confirmed against a posting before it is trusted
and the recovered link is marked as the inference it is, exactly as `MatchedOnAnotherBoard` is.
And Workday, at 69 employers, has no clean public board listing, so it stays out.

---

## 4. What the plan claimed, and what the code refuted

Recorded so nobody re-implements them. Each was written from outside the code, and each is wrong in
a way that reads as reasonable.

| Claimed | What is true |
| --- | --- |
| The queue is ordered by `score`, so filter on that | It is ordered by **`RankScore`**, which is an ordering key no client may display - min-maxed over one profile's pool, so the top of any pool is exactly 100. The *complaint* behind the claim held on live data, where an assessment of 75 outranked one of 92; the mechanism named did not. |
| Add `Blocked` and `Skipped` to `SubmissionEventType` | **They cannot be event types.** The fold takes the furthest-advanced phase, and no numbering makes "a captcha stopped us" advance or not advance an application without lying about one case or the other. Parking is an attribute on `Submissions`. |
| Return the remaining quota from `create_submission` | **A call that spends no quota reports none.** As the plan described it, `create_submission` wrote a row and no event, so it consumed nothing and had no burn-down to report. The quota belongs on `list_applyable`, where a batch is planned, and on `record_event`, where it is spent - and `create_submission` reports it only on the arm that now sends. |
| H6, the inline submitted event, depends on H7, the evidence columns | **Independent.** Both went into one migration, which is what a hand-dispatched schema change wants anyway. |
| Park it, and the posting comes back next run | A submission row alone removed the posting from the queue **permanently**. Parking means something only because the predicate in 2.2 changed with it. |
| Move the free text into "the daily generation pass" | **There is no daily generation pass.** Documents are user-initiated, one posting at a time. That is 1.2, and it is the loop's actual bottleneck. |
| Put a `profile` object on the pack | Breaches the no-whole-profile rule. It ships as repeated **named** allowlist entries, so a field added to `CandidateProfile` cannot start leaving the system without a diff saying so. |
| Mark fields `sensitive: true` | Nothing is marked, because nothing sensitive is *reachable*. The declared/derived split in 2.4 is the stronger guarantee: it does not depend on a flag being right. |

---

## 5. Deliberately absent

Worth stating, because each will look like an omission to whoever reads the tool list next.

- **No `submit_application`.** The server records that something was submitted; it never submits.
  Applying is irreversible and outward-facing, and keeping it outside means no bug in this
  repository can send anything to an employer. `McpEndpointTests` fails if one appears. Going from
  six tools to fourteen changed nothing here, and the writes are why it is worth repeating: they
  append to a log, set park columns, supersede an answer, and open and close a run. **None of them
  deletes, none of them edits, and none of them sets a status.**
- **No `get_profile`.** Tool results are transcript content wherever the client runs, and may be
  retained there. `get_submission_pack` is the honest exception and is treated as one - a tailored
  CV is the profile rewritten in prose - and is logged on the same terms rather than as a
  public-text read. `FormFieldCatalog` carries no date of birth, nationality, address, salary
  expectation or referee. A form will ask for some of those; a person types them, because a field
  an agent cannot fill is a field an agent cannot get wrong on somebody's behalf.
- **No `source` argument anywhere**, for the same reason: what a person asserted and what an agent
  inferred are different kinds of claim, and a log that cannot tell them apart cannot be audited
  after one turns out wrong.
- **No deletes on the tool surface or the API.** Withdrawing is a `Withdrawn` event.
  `dbadmin delete-submissions` is the single exception, for rows that never described a real
  application, and **it must stay a console command**: an HTTP route would be reachable with the
  same token the agent carries, and an agent that can erase real applications is a worse failure
  than every one this surface was designed to prevent.

---

## 6. How a client connects, and how to verify it

Entra supports no Dynamic Client Registration, so pre-registration is the only official route - and
`scripts/setup-api-app.ps1` already writes `preAuthorizedApplications` for the Azure CLI's fixed
first-party app id against the `Jobs.Read` scope. So a token minted by the CLI is a valid token for
this API, and **no OAuth code is needed at all**.

**Use `headersHelper`, not a static header.** Claude Code runs the helper fresh on every connection
with no caching, so it mints a new token each time and the hourly expiry stops being a problem. A
static `--header` works and is one line, but the token is then fixed for the life of the process
and dies after an hour.

```jsonc
// ~/.claude.json, under "mcpServers". User scope, not project: a .mcp.json is
// version-controlled and this repository is public, so the tenant's client id would be
// committed - which the hygiene rules forbid.
{
  "job-platform": {
    "type": "http",
    "url": "https://<api-fqdn>/api/v1/mcp",
    "headersHelper": "az account get-access-token --scope api://<api-client-id>/Jobs.Read --query \"{Authorization: join(' ', ['Bearer', accessToken])}\" -o json"
  }
}
```

`az account get-access-token` returns in well under a second against the helper's 10-second timeout
and reuses the refresh token from `az login`, so the only manual step is being logged in and the
credential never touches a config file. **If the helper fails, Claude Code reports the connection as
failed and does not fall back**, so `az account show` is the first thing to check when the server
will not connect.

**An unattended client is the other shape.** It authenticates app-only against the app role rather
than as a person, which is what the principal map in 1.1 exists for. A browser-based client is the
third and is more work: a 401 carrying `WWW-Authenticate`, a Protected Resource Metadata document,
and that client's fixed app id added to `preAuthorizedApplications`. The SDK ships `AddMcp()` and
`McpAuthenticationOptions.ResourceMetadata` for exactly this. It is not built.

Then, in order:

1. `az containerapp list --query "[0].properties.template.containers[0].image" -o tsv` - confirm the
   deploy landed before trusting any behaviour change.
2. `dbadmin status` - confirm the migration applied; then `dbadmin backfill-crossboard` per 1.1.
3. `dbadmin apply-links "<conn>" 1` - one day, not seven, per 3.1.
4. Generate documents for a handful of queued postings from the dashboard, or step 6 will be
   working the empty queue section 3 measured.
5. Create a submission by hand on the dashboard and add two events. The page is the proof the
   pipeline is legible to a person before anything automated writes to it.
6. Add the server to Claude Code and run `list_applyable` -> `get_submission_pack` ->
   `resolve_form_field` -> `create_submission(sent: true)` -> `list_submissions`; then
   `park_application` on something behind a login wall, and re-read the queue to see what parking
   actually did to it - 1.3's sixth bullet.
7. Read the `mcpDisclosures` container back. Confirm the field reads are there and **no value is**.
8. Confirm `/api/v1/mcp` answers 401 with no header against the live API.

### 6.1 Shipping a scraper-fork fix, kept because the next one will need it

The apply-URL work in 3.1 reached production through three repositories in a fixed order, and the
ordering is the part worth remembering: **the tag must exist before the pin moves, or the image
build fails on a 404.**

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

Watchtower pulls the new scraper image on the NAS and the next scheduled run is the first to emit
the new column. **Only postings scraped after that carry it**; the column stays null for the
existing corpus and fills in as postings are re-listed, which is why 3.1's backlog is history
rather than a defect.

---

## 7. Constraints that will bite

- **Every tool resolves the profile from the caller's token and never takes a profile id.**
  `SubmissionRepository` and `CandidateProfileRepository` both take a subject id and have no
  overload that does otherwise - the authorisation boundary expressed as a type. It matters more
  here than on a route, because a tool's arguments are named by a model rather than by a router: an
  unused `profileId` parameter is exactly what a model would helpfully fill in. The two ids that
  *are* accepted are bounded the same way - a posting id is checked against this candidate's matches
  before anything is written against it, and a submission id is resolved through their profile.
- **An app-only principal is mapped in configuration and never by an argument**, and the map is
  resolved in the MCP feature rather than in `CallerIdentity`: the API has one authenticated policy
  and no per-scope discrimination, so resolving it centrally would let an unattended client act as
  the candidate across every route instead of on these fourteen tools.
- **The caller comes from `RequestContext.JsonRpcRequest.Context.User`, not from
  `IHttpContextAccessor`.** The SDK populates the principal per message, which survives whatever
  async boundaries the transport introduces where an `AsyncLocal` may not. Read `oid` through
  `CallerIdentity.SubjectId`, never `ClaimTypes.NameIdentifier`, which resolves to `sub` and is
  pairwise per application.
- **No output cache on any of it.** Every tool is per-principal, and a shared cache keyed on a URL
  with no user in it is how one person is served another's pipeline.
- **`Api:AllowAnonymousReads` must not reach these routes.** Pinned as endpoint *metadata* in
  `McpEndpointTests` and `AuthorizationTests`, not only as a 401 - the behavioural test alone was
  found not to pin what it claimed, because every handler also calls `TryGetSubjectId`, which
  answers 401 when the token carries no `oid`. Defence in depth working, and a test measuring the
  second layer while describing the first.
- **The resolver is scoped and the pack store is a singleton**, and neither is arbitrary: the
  resolver depends on scoped repositories, while the store owns a connection pool and caches a user
  delegation key that a per-request instance would re-fetch on every call.
- **`../model.md` is amended**, and was amended before the code was written rather than after. The
  `Applications` row names the stored PDF and DOCX and the free text drafted in the same pass; the
  `Agent surface (MCP)` row names seven reads and seven writes, server-side resolution, parking as
  an attribute, and the app-only principal map. It is binding: a scheduled generation pass is an
  amendment before it is a function.
