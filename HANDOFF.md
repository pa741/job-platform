# Handoff

State of the matching work as of 2026-08-28, for whoever picks this up next.

Conventions, architecture and the standing rules live in [`CLAUDE.md`](CLAUDE.md). This file is
the delta and the open work, and **the open work comes first** because that is what someone
picking this up needs. What changed, and how it was measured, are below it as context for why.

**Deployed and verified.** `main` is at the commit the container app runs, the corpus is
re-enriched at `EnrichedPosting` version 5, and the working tree is clean. One profile exists
and has been swept: 4,078 pairs scored, 2,495 over the assessment threshold, 50 assessed.

Nothing about that profile belongs in this repository. It is somebody's employment history, and
the rule in `CLAUDE.md` covers fixtures, examples and screenshots alike.

**The stratified label set has reached the size 1.6 said to wait for.** As of 2026-09-02 there
are **357 assessed pairs**, of which **151 sit below score 80** - 44 in 70-79, 56 in 60-69, 51 in
45-59. Section 1.6 closed with "cohort D holds only 22 rows below score 80... at ten stratified
labels a night it will hold roughly 150 in a fortnight, which is when re-running `balanced.py`
becomes worth doing." That threshold is crossed, so **the corpus-wide claim is testable for the
first time** - the one thing 1.6 could not settle. Nothing else on that topic needs code.

The 30/10 split is doing what it was built for: over the two nights of 2026-09-01 and 09-02, 80
assessments came back 6 / 6 / 4 across 45-59, 60-69 and 70-79 with the rest at 80+, and **nothing
was discarded** - 80 requested, 80 returned, continuing the run since the 1.2 fix.

**In flight.** 1.1 has all four call sites, a dashboard page and a replay route; 1.2 has a
candidate fix. Both are marked *Progress* below, with what remains.

**A second feature now has its own file.** [`mcp_handoff.md`](mcp_handoff.md) covers the
submission pipeline and the agent surface over it, both built on 2026-08-31. Two things in it
belong on this file's radar rather than only on that one:

- **The realtime feed broadcasts.** `IRealtimeFeed.PublishAsync` sends to every connected client,
  which is right for the AI-failure feed 1.1 built and wrong for anything addressed to a person.
  The questions channel planned in `mcp_handoff.md` 1.4 assumed reusing that transport was free
  and it is not - `NegotiateAsync` already carries a `subjectId` it does not use, so the fix is a
  per-user send, but it is unbuilt work nobody had costed.
- **A behavioural authorization test was found not to pin what it claimed.** Every handler calls
  `CallerIdentity.TryGetSubjectId`, which answers 401 when the token carries no `oid`, so relaxing
  a route group's policy to `PublicReadPolicy` left the read cases green. The policy is now
  asserted as endpoint metadata. **The same weakness applies to the existing `/searches` and
  `/me` cases**, which are behavioural only - worth converting when something next touches them.

**1.6 is built, deployed, and has been through its first holdout - which moved the floor from
45 to 80 and cut the headline claim down.** The corpus is at **4,668 of 4,668** embedded. On 154
labels drawn *after* the ranking shipped, the ranking beats the score by **+0.045, CI
[-0.015, +0.101] - not significant**, where in-sample it had been a significant +0.123. What did
replicate, cleanly, is the reason the thing exists: inside the top band the score is flat
(-0.051, interval containing zero) and the embedding is not (+0.520, interval excluding it).
**Quote the ranking as better at the top of the list, not as better overall.** Read 1.6 before
using any earlier number from this file - +0.521 and 68.5% are in-sample and superseded.

**The next sweep at 03:30 UTC is the test for 1.2.** If the "unusable role index" warnings stop,
the cause was the quoted index; if they continue, the warning now names which fault it is. Either
way the ledger will have recorded it, so start at
`GET /api/v1/ai-calls?days=2` rather than in App Insights.

---

## 1. Open work, in the order worth doing it

### 1.1 AI calls fail silently, everywhere, and nobody can see it

**The general problem, and the one to fix first, because it is the instrument for the rest.**

Every AI path in this system degrades silently by design. `AddAiProvider` registers nothing when
no provider is configured, and `IDocumentExtractor`, `ICandidacyAssessor` and
`IApplicationWriter` are all resolved as nullable so consumers skip their step rather than
throw. That design is right: a provider failure must not take down endpoints with nothing to do
with AI. But *"must not fail loudly"* was implemented as *"must not be recorded at all"*, and
those are different things.

What it has cost, three times now:

- **2026-08-28.** The nightly sweep was raised to 90 assessments. Nine batches of ten went out;
  five came back with role indices the assessor could not use and were discarded whole. 40
  written, 50 paid for and thrown away. No exception, no error, and the sweep reported success.
- **The first real extraction backfill** spent its calls collecting HTTP 429s and extracted
  almost nothing. The symptom was a stalled count rather than a red anything.
- **`KernelDocumentExtractor.Distribute`** drops misaligned answers by design, and the affected
  postings are quietly re-extracted by a later backfill. Correct, and invisible.

**What to build.** A record per model call: which path made it, which deployment served it
(`bulk` or `writing`), when, the outcome, and on failure a bounded reason plus the ids affected.
Then a dashboard view whose subject is failures — a success count nobody reads is not the point.

Constraints that are not obvious and will bite:

- **Never store the prompt.** The assessor's and extractor's prompts carry the candidate's
  profile. Store the outcome and a reason, not the payload, and bound the reason's length. The
  posting side is public text; the profile side is not, and one store holding both is one store
  that leaks.
- **Cosmos, not SQL.** SQL is billed on wall-clock time online against a monthly grant that one
  daily ingest half-consumes, and it is reserved for posting browse, search and detail. Every
  dashboard metric already comes from Cosmos. This is a metric.
- **App Insights is not a substitute, and that was measured.** Sampling is on with
  `excludedTypes: "Request;Exception"`, so traces are sampled and these failures throw nothing.
  On the night of 2026-08-28 the "unusable role index" warnings happened to survive while the
  sweep's own completion line did not. A ledger you cannot trust to be complete is not a ledger.
- **A failure has to name what it lost.** "One call failed" is not actionable; "these 10 posting
  ids went unassessed and will be retried" is. The retry is usually automatic here, so the
  reader's question is almost always *how much* and *is it converging*, not *what threw*.
- **The Realtime component in `../model.md` is the one piece never built**, and it is a Cosmos
  change-feed trigger pushing to clients. An AI failure feed is a natural first consumer, if you
  would rather build it for a reason than for its own sake.

**Progress, 2026-08-28. Built and deployed, verified end to end in production.**

- `SweepSummary` carries `Requested` and `Discarded` beside `Assessed`, and the sweep warns when
  they diverge, naming the postings it lost. A caller could not derive those.
- `AiCallRecord` / `IAiCallLog` in Core, `AiCallLogRepository` writing to a new Cosmos `aiCalls`
  container - partitioned by UTC day, ninety-day TTL, shared database throughput so it costs
  nothing against the free-tier ceiling. `AiCallRecord.Create` is the only constructor, so the
  bounds on the reason and the id list cannot be forgotten at a call site.
- `GET /api/v1/ai-calls` and `/ai-calls/summary`, reading through `IAiCallSource` the way the
  metrics endpoints read through `IMetricsSource`. `failuresOnly` defaults to true.
- Verified live: the assessor wrote a record, the endpoint read it back -
  `candidacy-assessment / bulk / Succeeded / 10 of 10 / 35.4s`.

**Also done since:** posting and profile extraction and application writing both report to the
ledger, and the dashboard has a **Model calls** page - failures first, requested paired with
returned everywhere, each failure naming the postings it lost and saying they are retried.

Wiring extraction turned up a real find. `ExtractionPrompt.Int` had **exactly the bug the
assessor had** - `JsonValueKind.Number` only - so a quoted index or confidence would have dropped
a whole extraction batch just as silently, and had been able to since the file was written. Both
now read through `AiJson.Int`: one reader, for the reason `TitleTokenizer` gives.

**All four call sites are wired.** The batch path records **at collection**, not at submission:
collection is the only moment that knows the answer, and the two are up to 24 hours apart, so a
record written at submission would have to be updated later or left as a question. It is written
only once a batch actually completes - never on an invocation that deferred work, which would
report a shortfall the next tick is about to fill - and the counts come from the provider's whole
result set rather than one invocation's tallies.

**A failed call keeps its prompt, so it can be replayed rather than theorised about.** The
parsing faults this ledger exists to catch are exactly the kind that need the actual bytes: a
quoted index and an out-of-range one are indistinguishable from a count, and that cost a day.
`GET /api/v1/ai-calls/{id}/replay` returns the prompt and a ready-made curl.

Three guards hold it down, **all in the sink rather than at the four call sites**, because a rule
enforced in four places survives only until somebody adds a fifth:

| guard | why |
| --- | --- |
| off unless `AiLedger:RecordPrompts` | a clone stores none; turning it on is a per-deployment decision to store personal data |
| only for a call that lost something | a success has nothing to reproduce, and successes are where the data would accrue |
| never on the list response | `Api:AllowAnonymousReads` can open that route; the prompt has its own, behind `AuthenticatedPolicy` |

That last one is the point of the split. These prompts carry employment history, contact details
and salary expectations, and without it one config flag is the difference between a dashboard and
a published CV. Verified live: the list carries no prompt field, the replay route answers 401
without a token and 404 with one while recording is off.

**Verified in production, 2026-08-31.** All four call sites now appear in
`GET /api/v1/ai-calls/summary?days=7`, which was the outstanding check:

| operation | calls | failed | requested | returned | tokens |
| --- | --- | --- | --- | --- | --- |
| posting-extraction | 127 | 1 | 1,171 | 1,170 | 2.01M |
| candidacy-assessment | 34 | 0 | 264 | 264 | 410k |
| text-embedding | 38 | 0 | 1,172 | 1,172 | 911k |
| application-writing | 1 | 0 | 1 | 1 | 5.1k |

**The ledger has now caught three real failures it was built for**, and each is the shape the
section predicted: one `posting-extraction` partial, recorded as *"1 of 7 documents missing from
the response"* with the affected posting named; and four `text-embedding` batches lost to
`404 DeploymentNotFound` on a freshly provisioned deployment, which is what produced the retry in
`KernelTextEmbedder`. None of them threw. Without the ledger all five would have been a count
nobody was comparing to anything.

**Remaining:**

1. **Turn `AiLedger:RecordPrompts` on when actually debugging, and off afterwards.** It is an app
   setting on both hosts, so it needs no deploy either way.
2. **Consider the change feed.** The Realtime component in `../model.md` is still the one piece
   never built, and a failure appearing on the dashboard as it happens is a better reason to
   build it than building it for its own sake.

### 1.2 The assessor correlates answers by position, and loses whole batches

The specific defect 1.1 would have surfaced. Do this second, or first if you would rather stop
the money before building the instrument.

`KernelCandidacyAssessor` packs ten roles into one call as `ROLE 0..9` and matches answers back
by index. When the model returns a different count, or 1-based indices, or a truncated response,
every index is unusable and the whole batch is dropped. Measured on 2026-08-28: **4 of 9 batches
usable, 5 discarded**.

**Dropping them is right.** An answer landing against the wrong posting would be wrong,
self-consistent and undetectable afterwards, which is exactly why
`KernelDocumentExtractor.Distribute` drops rather than clamps. Do not "fix" this by clamping.

**Progress, 2026-08-28.** Losing a whole batch at once is the signature of a response that is
well formed and *typed* differently, not one that is wrong. `Int` demanded
`JsonValueKind.Number` while the prompt asked for the index to be "copied exactly" from its
heading — and copying it as text is a reasonable reading. `Int` now accepts a JSON string holding
an integer, and the schema line says "integer, unquoted" to discourage it at the source. Six
tests in `CandidacyAssessorTests`, the first of which fails against the old parser.

**Confirmed, 2026-08-31. The hypothesis was right and this is closed.** Over the seven days
since the fix the ledger records **34 assessment calls, 264 requested, 264 returned, 0
discarded** - including four consecutive nightly sweeps at the full budget of forty. Before the
fix, five of nine batches were lost in a single night. Nothing has been discarded since, and the
warning that would have named a `JsonValueKind` has not fired once.

The diagnostic added alongside it stays: if this ever recurs the log now names the kind and the
value, so the next reader does not have to re-derive which of the three faults it is.

The range and duplicate checks are untouched and now pinned by tests. Accepting `"3"` as 3 is
parsing; clamping an out-of-range index would be guessing, and is still refused.

If it recurs, the durable fix is to stop correlating by position entirely: give each role an
opaque id and require it echoed back — this codebase's own lesson from the other side of the same
problem, *"A batch API echoes a `custom_id` per request, so correlation is the platform's
problem."* Reducing the batch size is the tempting shortcut; it only makes each loss smaller.

### 1.3 A widely-held skill on a role from another field still scores 100

The clearest remaining ranking defect. It now has a real profile and 50 assessed pairs behind it
rather than an argument.

| posting | score | model |
| --- | --- | --- |
| Legal Counsel | 92 | **5, Weak** |
| Product Developer (fixed term) | 100 | **10, Weak** |
| Research Associate, Cognitive Neuroscience | 92 | 25, Weak |
| Tax Engineer | 92 | 30, Weak |
| `.NET Developer` | **90** | **92, Strong** |
| VoidZero Engineer | 100 | 92, Strong |

The scorer ranks Legal Counsel above `.NET Developer`, and it stays there after the model has
said 5 out of 100. Across the 50 assessed: 14 Strong, 23 Possible, 13 Weak.

These are correct readings of real requirements — `skill.sql` genuinely discriminates, so the
version 5 floor correctly leaves them alone. **No rule over the concept axes separates them**,
which is measured three ways rather than argued: not by count, not by coverage, and not by how
specific the concept is (the vocabulary is two levels deep, so `skill.sql` and `skill.csharp`
are structurally identical). What separates them is what the *role* is, which is a judgement,
and `ICandidacyAssessor` is the half of the design that makes judgements.

Nothing in `JobMatchRepository.ListAsync` reads `Verdict`. Surfacing it in the ordering is the
cheap half and needs no new signal.

Two things to be careful of:

- **Only 50 of 2,495 eligible pairs are assessed.** Most rows have no verdict at all, and the
  ordering must leave those where they are rather than sorting them above or below everything
  judged. Getting that wrong buries the corpus under a handful of rows.
- **The sweep spends its budget strictly top-down**, so a better ranking feeds itself a better
  shortlist. That is also why 1.1 and 1.2 come first: half of what is paid for never arrives.

Take a top-60 snapshot before and diff it after, the way §3 describes.

#### The plan

**The decision is what to do with the 98% of rows that have no verdict.** Any rule that only
moves assessed rows makes "not yet judged" an advantage, which is the same shape as the
silence problems the scorer already had. Two candidates survive; two are ruled out below.

**R1 — the best available estimate.** `rank = AssessmentScore ?? Score`. Both numbers answer
the same question on the same 0-100 scale - how good is this match - and one of them is
better informed, so it replaces the estimate once it exists. No new arithmetic, nothing
invented.

**R2 — buckets, then the number.** Strong, then Possible, then unassessed, then Weak. Puts
the 14 Strong rows on top today and sinks every Weak. It asserts more: that a Possible at 60
belongs above an unjudged 96, which is a claim about judgement beating estimate that the data
may not support.

The two differ mainly in how much they depend on coverage, and that is the thing to weigh:
**R1 changes almost nothing at 50 of 2,495 assessed** - the top would stay nearly all
unassessed - while R2 works at any coverage precisely because it asserts more.

**Ruled out, with reasons rather than preferences:**

- **The model may only demote** (`min(Score, AssessmentScore)`). It would suppress the exact
  case `CLAUDE.md` calls the informative one: *"a 58 the model calls strong is precisely the
  posting worth surfacing"*. Rejected on the documented design, not on taste.
- **A weighted blend** (`w·Score + (1-w)·AssessmentScore`). Inventing a weight is the shape of
  the coverage damping that was measured and rejected in 2.1. If a blend ever looks necessary,
  it needs a reason that is not "it looked about right".

**How to measure it, before writing any of it.** Everything needed is already on
`GET /api/v1/matches` - score, verdict, assessmentScore, title, company - so this needs no SQL
and no firewall rule. Page all rows, compute each ordering offline, and diff the **whole**
top-30 with titles. Not the six rows in the table above: every regression this repository has
caught came from diffing the whole list, and none would have been found from the examples
already in hand.

Do it after a night with the 1.2 fix in place. At 40 usable assessments a night instead of
~18, the assessed set roughly doubles, and R1 is only judgeable once there is enough of it.

**How to implement it, once chosen.** Both candidates are a pure function of columns already
stored, so neither needs new data - only somewhere indexable to put it. A **persisted computed
column** is the right home:

```sql
RankScore AS ISNULL(AssessmentScore, Score) PERSISTED
```

with an index on `(ProfileId, RankScore)`. EF expresses this through
`HasComputedColumnSql(..., stored: true)`. **This is what answers the objection that sank a
stored `Coverage` column** - *"a second copy that could drift"* - because SQL maintains it and
drifting is not a state it can reach. R2 is expressible the same way with a `CASE` over the
verdict.

**What must not change:**

- **Keep the `(ProfileId, Score)` index.** `minScore` filters on the arithmetic and the sweep
  still selects on it. Adding a rank index is not replacing that one.
- **`GetUnassessedAsync` needs no change.** It selects only unassessed rows, and for those the
  rank *is* the score under either candidate - so the model budget still goes to the
  highest-scoring unjudged pair, which is what makes the list converge as coverage grows.
- **`Verdict = Unknown` is not the same as unassessed.** Unknown means the model answered and
  said nothing usable; unassessed means it has not run. A rule keyed on `AssessedAtUtc` and one
  keyed on `Verdict` disagree on exactly those rows.
- **The list already shows both numbers and calls neither the answer.** That stays. What needs
  saying in the UI is what the list is *ordered* by, which is currently implicit and would
  become a lie.

**One thing the data may force.** Weak verdicts in the current 50 carry assessment scores from
5 to 60, so under R1 a Weak at 60 outranks a Possible at 50 - the label and the number
disagree. If that shows up in the diff, the fix is to rank on the verdict first and the number
within it, which is R2 arriving by evidence rather than by assertion.

### 1.4 Generated documents have never run against real data

`POST /api/v1/applications/{postingId}` writes a tailored CV and cover letter, renders them to
PDF on demand, and **refuses to generate without an existing match** — the gap list is what the
writer is told it must not claim. Until a profile existed there was nothing to run it against,
so this path has never been exercised outside its unit tests.

**Run, and it works. Verified 2026-08-31 against the call made on 2026-08-29.**

- The ledger carries one `application-writing` record: **deployment `writing`**, Succeeded, 1 of 1,
  26.6s, 2,126 in / 3,017 out / 1,664 reasoning tokens. That deployment name is the important
  half - it is the one place a missing registration would show up as a CV quietly written by the
  cheap model, and Semantic Kernel would have fallen back silently rather than thrown.
- One document exists, revision 1, against posting 379.
- **Both PDFs render**: `cv.pdf` 49,057 bytes and `cover-letter.pdf` 18,717 bytes, both valid
  `%PDF-1.7` with `application/pdf`. That exercises `MarkdownPdfRenderer`'s AST walk and
  `EmbeddedFontResolver` in the Linux container, which is where PDFsharp's platform-independent
  build throws on its first call if no resolver is registered - the failure this design was
  chosen to avoid, never previously seen not to happen.

**What is left here is a judgement, not a verification.** Whether the CV is any good, and whether
the writer respected the gap list rather than claiming skills the profile does not show, is a
question for the person whose CV it is. The machinery is proven; the output is not reviewed.

### 1.5 Extraction coverage - measured 2026-08-31, and the coverage half is done

Read it with `dbadmin coverage "<connection-string>" [top-mentions]`, which is what this section
previously had no way to answer. It needs a temporary SQL firewall rule; the invocation below
adds and removes one with a shell trap so the removal survives a failure.

**Coverage is effectively complete and needs no work.**

| | postings | share |
| --- | --- | --- |
| total | 5,909 | |
| with a description | 5,837 | 98.8% |
| with any concept | 5,662 | 95.8% |
| with a model concept | 5,470 | 92.6% |
| **read by the model, ever** | **5,822** | **98.5%** |
| read at the current extractor version | 5,822 | 98.5% |

87 postings have never been read, and 72 of those have no description to read - so the real
backlog is about fifteen. The old "303 never read" is gone. Note the corpus grew from 4,078 to
5,909 while the model-concept share stayed at 92.6%, which is extraction keeping pace rather than
a coincidence.

**The 0.490 figure an earlier revision called the graded share is the share of *assertions* that
are model-sourced, not the share of postings read.** That distinction is why this command reports
distinct postings and says so in its output.

#### The mention log, read for the first time, and it says one thing loudly

Ranked by how many postings name a form - one advert repeating a word twenty times is one
employer's habit, twenty adverts saying it once is a gap:

| form | postings | reason |
| --- | --- | --- |
| Go / C / R | 979 / 325 / 310 | Ambiguous - deliberate, and now quantified |
| Claude Code | 248 | UnknownModelSkill |
| RAG | 155 | UnknownModelSkill |
| Claude | 141 | UnknownModelSkill |
| Cursor | 136 | UnknownModelSkill |
| MCP | 117 | UnknownModelSkill |
| LangGraph | 111 | UnknownModelSkill |
| GitHub Copilot | 101 | UnknownModelSkill |
| AI | 89 | UnknownModelSkill |
| vector databases / JAX / S3 / Salesforce | 78 / 66 / 63 / 61 | UnknownModelSkill |
| cloud-native / data-science / stakeholder-management | 55 / 50 / 47 | UnknownBoardSkill |
| Android / CUDA / Codex / NoSQL / machine learning / CSS | 55 / 54 / 54 / 53 / 52 / 52 | UnknownModelSkill |
| Copilot / HTML / LlamaIndex / Anthropic / prompt engineering | 51 / 50 / 46 / 46 / 45 | UnknownModelSkill |

**The vocabulary is missing the entire AI-engineering cluster**, which is the one thing this
corpus is full of. Claude Code, RAG, MCP, LangGraph, Cursor, Copilot, LlamaIndex, vector
databases, prompt engineering, OpenAI, Anthropic, Codex - roughly 1,400 posting-mentions between
them, none of which the matcher can see. Alongside it a plainer gap: S3, ECS, IAM, CUDA, JAX,
NoSQL, CSS, HTML, Android, Salesforce, ServiceNow, Power Automate.

`Go` at 979 postings is the cost of the ambiguity rule, now measured rather than assumed. It is
still the right call - a false spike in demand for Go is worse than undercounting it - but 979 is
the number to weigh against any proposal to resolve it from context.

#### And a defect the log exposed: forms the vocabulary already knows are recorded as unknown

`machine learning` appears 52 times as `UnknownModelSkill` while `area.ml` carries the alias
`machine learning`. `AI` appears 89 times and `area.ml` carries `ai`. `generative AI` appears 43
times and `skill.llms` carries `generative ai`.

The cause is a seam, not a vocabulary gap. `ExtractionPrompt.BuildVocabulary` sends the model
`key = label` and **no aliases**, so a model reading "generative AI" sees only `skill.llms = LLMs`
and quite reasonably puts it in `unknownSkills`. And `ExtractionPrompt.Parse` records everything
in `unknownSkills` verbatim **without ever consulting the graph**. The resolver already knows
these forms; nothing asks it.

**The fix is to resolve `unknownSkills` through `ConceptGraph.TryResolve` before recording a
mention**, rather than to send the aliases and pay for them in every prompt. It reuses the
judgement already encoded instead of asking the model to re-derive it, and it inherits the
ambiguity refusal for free - `Go`, `C` and `R` stay unresolved, correctly. `fromStructuredField:
true` is the right mode: the model naming a technology is a deliberate act, much closer to a
board's curated skills field than to a regex hit in prose, and that flag is what lets a domain
like `area.ml` resolve at all.

#### Applying any of this to the corpus is cheaper than it looks

**`PostingExtractions.PayloadJson` stores the raw model response per posting.** So a change to
`ExtractionPrompt.Parse` - the fix above, or any future one - can be applied to the whole corpus
by **re-parsing stored payloads at zero model cost**. Re-extracting 5,822 postings would be
roughly 10M tokens on the measured rate of 1,700 per document; re-parsing is a query and some
CPU. That pass does not exist yet and is the single highest-leverage thing to build here, because
it turns every future parser and vocabulary change from an expensive decision into a cheap one.

Vocabulary additions reach the deterministic path through a `EnrichedPosting.CurrentVersion` bump
and a reprocess, which also costs no model calls. Only the model's own key choices need
re-extraction, and those are the part the re-parse cannot fix.

#### Done, 2026-08-31, and here is the corpus before and after

All three shipped: the `unknownSkills` resolution, 24 concepts, and `ReparseExtractionsFunction`.
Applied in the order seed -> reparse -> reprocess. **The whole application cost zero model calls.**

| | before | after |
| --- | --- | --- |
| with any concept | 5,662 (95.8%) | **5,687 (96.2%)** |
| with a model concept | 5,470 (92.6%) | **5,551 (93.9%)** |
| read by the model, ever | 5,822 (98.5%) | **5,837 (98.8%)** |
| postings with no concepts at all | 247 | **222** |
| never read by the model | 87 | **72** |

5,822 postings reparsed, **0 unparseable**. 72 unread against 73 with no description: essentially
every advert carrying text has now been read.

**The mention log is the clearest evidence.** Gone from it entirely: Claude Code (248 postings),
RAG (155), MCP (117), LangGraph (111), GitHub Copilot (101), AI (89), vector databases (78), JAX
(66), S3 (63), Salesforce (61), Android (55), CUDA (54), Codex (54), NoSQL (53), machine learning
(52), CSS (52), HTML (50), LlamaIndex (46), Anthropic (46), prompt engineering (45), generative AI
(43), ECS (43), ServiceNow (40), IAM (39). Roughly 1,700 posting-mentions the matcher could not
see, and can now.

What remains at the top is exactly what should: `Go` 979, `C` 324, `R` 310, `containers` 84 - the
ambiguity rule, unchanged - joined by `Claude` and `Cursor` and `Copilot`, which are the three
this round deliberately added as ambiguous. They are recorded and never asserted, which is the
design working rather than a gap.

#### And the log has already produced the next round

Unprompted, which is the point of it. Ranked as before, by postings naming the form:

| form | postings | note |
| --- | --- | --- |
| CrewAI | 38 | agent framework, sits beside LangGraph |
| AutoGen | 38 | the same |
| Gemini | 37 | **the omission worth noting** - OpenAI and Anthropic were added and Google was not |
| Jest | 37 | JavaScript testing; `area.quality` has no JS entry |
| Triton | 36 | ambiguous - the inference server and the GPU language share the name |
| Power platform | 36 | probably an alias of the Power Automate concept rather than its own |

Gemini is the useful lesson: the additions were drawn from a log that had never been read, so the
first pass inherited whatever that log happened to emphasise. A second reading finds what the
first crowded out. **Do not treat one pass over the mention log as having finished the job** -
run `dbadmin coverage` again after any vocabulary change and expect a new top ten.

#### The two defects this shipped with, and what they cost

Both were mine and both were caught in production rather than in review.

**The reparse pass deleted without writing.** `PostingExtractionWriter.ApplyAsync` deletes through
`ExecuteDelete`, which commits immediately, and leaves its inserts for a `SaveChanges` the caller
owns so a batch can go in one round trip. The new pass never called it. Every posting it touched
on its first run lost its model concepts and mentions - recoverable only because the pass rebuilds
from `PayloadJson`, which is luck rather than design. The asymmetry is now documented on
`ApplyAsync` itself, because a caller cannot see it from the signature.

**`ExecuteDelete` leaves the change tracker holding what it deleted.** So applying one posting
twice on a context collided, and EF's message named `PostingConceptEntity` and neither the posting
nor the key. The writer now detaches what it bulk-deleted, and its concept loop dedupes by
resolved id - the guard the mention loop beside it always had.

**Both tests written for these passed on their first run while testing nothing**, because each
used a concept key that is not in the vocabulary, so nothing resolved, nothing was added, and
nothing could collide. That is twice in two days. **A test written for a known bug that passes
first time deserves suspicion, not a tick** - run it against the unfixed code before believing
it.

### 1.6 Embeddings are the axis 1.3 was missing - measured, then built

**Built on 2026-08-28.** `MatchRanker`, `EmbeddingVector`, `EmbeddingText`, `PostingEmbeddings`,
`ProfileEmbeddings`, `EmbedCorpusFunction`, an `embeddings` deployment in Bicep, and the list
ordered by the result. The evidence below is kept in full because every constant in
`MatchRanker` is one of these numbers, and a future tuning has to argue with them rather than
around them.

**What was built, against what this section proposed:**

| proposed | built | why the difference |
| --- | --- | --- |
| a similarity axis inside `MatchScorer` | a separate `MatchRanker` | folding it into the score clears every stored assessment - a moved score is the staleness signal - so it would have destroyed the 195 labels it was fitted on |
| `vector(512)` columns on `JobPostings` and `CandidateProfiles` | two side tables | the blob is read by two passes and would otherwise be carried by every query that materialises a posting row |
| ledger operation `posting-embedding` | `text-embedding` | one call site serves both sides, and naming it for the posting half would have been wrong on the other |
| raw product of score and similarity | convex combination at α=0.6 | measured after this section was written; +0.045, CI [-0.006, +0.098] - not significant, but it is the form whose weight means something |

#### It has run, and here is what it did

**Coverage is 4,668 of 4,668.** Six calls to `run-embed-corpus`, each bounded to ~150s, then one
`run-match-sweep`. Cost, from the ledger: **2.07M tokens across 85 calls for the first 2,592
adverts**, roughly 800 tokens an advert - about four pence for the corpus. Embedding is not a
cost decision at this scale.

**Measured on production, over all 195 assessed pairs, spanning scores 45-100:**

| ordering | top 10 (S/P/W) | top 20 | top 30 |
| --- | --- | --- | --- |
| deterministic score | 2/6/2 | 4/11/5 | **6/14/10** |
| embedding only | 1/8/1 | 4/14/2 | 7/18/5 |
| **shipped rank** | **5/5/0** | 6/11/3 | **8/19/3** |

The score row reproduces the offline numbers exactly, which is the useful part: it says the
measurement path - API, ordering, verdict join - agrees with the scratch analysis, so the other
two rows are describing the same thing the research described.

**Three caveats, and the first is the one that matters.**

1. **This is in-sample.** α=0.6 was fitted on these 195 labels and is now being scored against
   them. It confirms the implementation does what the analysis said; it does **not** establish
   that the gain generalises. See the holdout below, which settles part of it and not all.
2. The shipped ranking normalises over the production pool (4,668 postings at score >= 45) where
   the offline fusion normalised similarity over the assessed 195. Different bounds, slightly
   different ordering - which is why the top-30 here is 8/19/3 against the 11/16/3 predicted.
3. 195 rows. A difference of one or two is noise; 10 Weak to 3 is not.

**A qualitative check that is worth more than it looks.** Before the ranking, the top of the list
was five score-100 rows including "Senior Freelance Consultant, AI Safety" and "Senior Security
Researcher - Agent Workflow". After it, both are out of the top 15, and a Kotlin/JVM role scoring
84 and a games role scoring 70 have climbed in on similarity alone. That is 1.3 being fixed in
the direction 1.3 predicted.

#### The result

**Re-measured on the stratified 195. The first version of this section was measured on the 70
pairs the deterministic score had selected, and the numbers below it are the ones to trust.**

| signal | Spearman vs the model | 95% CI |
| --- | --- | --- |
| deterministic score | +0.315 | [+0.174, +0.443] |
| embedding | +0.296 | [+0.154, +0.426] |
| **score x embedding** | **+0.476** | [+0.352, +0.583] |

Paired bootstrap, 10,000 resamples:

- **embedding vs score: -0.018, CI [-0.235, +0.196] - not significant.** Across the corpus they
  are equally good. The +0.501 recorded earlier was the pooling bias flattering the embedding,
  exactly as the research warned.
- **score x embedding beats score: +0.161, CI [+0.085, +0.244] - significant.**
- **score x embedding beats embedding: +0.180, CI [+0.013, +0.339] - significant.**

**The combination beats either signal alone. That is the finding.**

#### And they are complementary in a specific, useful way

Restricted to the top two bands - score >= 80, n=90, which is the band a candidate actually
looks at and where the score inverts:

| signal | Spearman | 95% CI |
| --- | --- | --- |
| deterministic score | **-0.191** | [-0.394, +0.029] |
| embedding | **+0.448** | [+0.254, +0.608] |

Mean similarity there: Strong 0.508, Possible 0.502, Weak 0.466.

So the two signals fail and succeed in opposite places:

- **The score is a good filter and a bad final sort.** +0.315 across the corpus, -0.191 inside
  its own top band.
- **The embedding is a mediocre filter and a good final sort.** No better than the score overall,
  clearly better exactly where the score gives up.

That is the standard retrieve-then-rank shape arrived at by measurement rather than by
architecture diagram, and it says what to build: **keep the score as the filter, add the
embedding as the axis that orders what survives it.**

One caveat on the form. What was measured is the raw product, and the score dominates it by
scale. The fusion literature (Bruch, ACM TOIS 2023) recommends a convex combination over
normalised inputs, with the normalisation computed over all ~4,000 pairs rather than the labelled
ones. That has not been measured yet and may beat the product; the product is what the numbers
above describe.

#### The original measurement, kept for the record

Cosine similarity between the profile document and each advert, `text-embedding-3-small` at
512 dimensions, against the model's own assessment score:

| ordering | Spearman vs the model | Weak in top 20 | Strong buried in bottom 20 |
| --- | --- | --- | --- |
| arithmetic score alone | **-0.198** | 5 | 8 |
| embedding similarity | **+0.488** | 3 | 2 |
| score x similarity | +0.422 | 3 | 2 |

**Read the -0.198 carefully.** It is not evidence that `MatchScorer` is worthless: the
assessed set was *selected* by high score, so every row in it sits between 90 and 100 and the
score has almost no range left to correlate with. What it does say is precise and is exactly
1.3 - **within the band the score has already chosen, the score carries no further signal about
which matches are good.** Similarity was not used to select the set, so its +0.488 is not
restricted the same way.

The extremes are convincing. `Legal Counsel - UK` is the **lowest similarity of all 70** at
0.341, and the model scored it 5. `Workday Integrations Specialist` (0.392, model 20),
`Senior Product Manager` (0.414, model 35), `TM1 Developer` (0.427, model 20) and
`Product Developer` (0.429, model 10) follow it. At the other end `.NET Developer` (0.557) and
`VoidZero Engineer` (0.556) are near the top. **Every posting the concept axes could not tell
apart, the embedding does.**

It is not clean, and the failures matter. Three `Senior Research Engineer` rows score 0.524 -
high - and the model called them 20/Weak. `Delivery Solutions Engineer` (0.449) and
`Lead Technical Consultant` (0.445) are both genuine Strong matches that similarity ranks low.
Mean by verdict is Strong 0.503, Possible 0.493, Weak 0.459: the right order, with heavy
overlap. **This is a better prior, not an oracle**, and it belongs beside the concept axes
rather than instead of them.

#### The first holdout: the central claim replicates, the weight is still unvalidated

The nightly sweep of **2026-08-29** produced 40 assessments that did not exist when α was chosen,
so they are honest in a way the 194 are not.

| signal | Spearman vs the model | 95% CI |
| --- | --- | --- |
| deterministic score | **-0.051** | [-0.407, +0.297] |
| embedding similarity | **+0.520** | [+0.267, +0.714] |
| shipped rank | **+0.531** | [+0.267, +0.729] |

Mean similarity runs monotone with the verdict - Strong 0.5249, Possible 0.5149, Weak 0.4763 -
and the top 10 goes from 4 Strong / 2 Possible / **4 Weak** under the score to 7 / 3 / **0** under
the shipped rank.

**What this settles.** The score carries no signal inside its own top band: its interval straddles
zero, out of sample, on fresh labels. The embedding does, and the +0.448 measured in-sample for
the top two bands came back as **+0.520**. That is the claim the whole design rests on and it
replicated.

**What it does not settle, and this is the part not to overstate.**

- **It does not validate α=0.6.** These 40 span scores 92-100, so the score axis barely varies and
  the fusion is effectively all-embedding - which is exactly why `shipped rank` and `embedding
  only` come out identical here. The holdout tests the *axis*, not the *weight*.
- **It says nothing corpus-wide.** The +0.521 headline remains in-sample only.
- n=40. The intervals are wide and one more night will not fix that.

#### The sweep cannot produce the sample that would settle it

**`GetUnassessedAsync` orders by score descending**, so the nightly budget goes to the top of the
range every night - the 40 above span 92 to 100 and nothing else. The standing process therefore
generates top-band labels in perpetuity and **can never produce the stratified sample the
corpus-wide claim needs**. That is the pooling bias this whole section began with, now built into
the mechanism that produces the evidence.

It is also not simply a bug to fix, because top-down is *right* for the product: the model budget
should go where the arithmetic is most hopeful, and those are the rows a candidate actually reads.
The two purposes genuinely conflict.

**The fix is to split the budget rather than to choose.** Something like 30 of the 40 top-down,
unchanged, and 10 drawn across the bands below - which costs about 7k tokens a night, produces an
unbiased sample at roughly 300 labels a month, and leaves the shortlist essentially as it is. The
band machinery already exists: `SweepRequest` takes `MinScore` and `MaxScore`, and
`GetUnassessedAsync` already switches to posting-id order when a ceiling is given, precisely so a
band sample is not itself top-restricted. Nothing new has to be built - the nightly path just has
to use what the HTTP path already has.

Until that lands, a stratified holdout has to be drawn by hand, the way the 115 were on
2026-08-28: band-bounded calls to `run-match-sweep`. About 80k tokens for 100 labels, which is
pennies.

#### The stratified holdout: what replicated, what did not, and what changed because of it

Drawn by hand on 2026-08-29 - 40 from the nightly sweep plus 114 across the bands below 90 via
band-bounded `run-match-sweep` calls, because the nightly sweep cannot produce a stratified sample
(see above). **154 labels, spanning 45-100, none of which existed when α was chosen.**

**Under an identical equal-weight-per-band scheme, fitted against holdout:**

| | fitted (n=194) | holdout (n=154) |
| --- | --- | --- |
| deterministic score | +0.390 | +0.523 |
| embedding similarity | +0.288 | **+0.186** |
| shipped rank | +0.513 | +0.568 |
| **rank - score** | **+0.123, significant** | **+0.045, CI [-0.015, +0.101], NOT significant** |
| embedding - score | -0.100, not significant | **-0.337, significant (embedding is worse)** |

**So the corpus-wide claim did not replicate.** The gain shrank by roughly two thirds and its
interval now contains zero, and the embedding alone went from "as good as the score" to
"significantly worse than it". That is textbook optimism in a figure fitted and scored on the same
labels, and the earlier +0.521 should be read as such.

**What did replicate is the claim the design actually rests on.** Per band, on the holdout:

| band | score vs model | embedding vs model |
| --- | --- | --- |
| 45-59 | +0.352 | +0.119 |
| 60-69 | +0.161 | +0.148 |
| 70-79 | +0.153 | +0.205 |
| 80-89 | +0.282 | +0.087 |
| **90-100** | **-0.051** | **+0.520** (interval excludes zero) |

The two are near-perfect complements: the embedding's interval excludes zero only in the top band,
and the score's contains zero only in the top band. Mean similarity is correctly ordered by
verdict in 90-100 (0.5249 / 0.5149 / 0.4763) and is not ordered at all in 45-59, 60-69 or 80-89.

**Which is why the floor moved.** At 45 the embedding was taking 0.6 of the weight across 45-79
while contributing nothing there, diluting a score that was working - and that, not the top band,
is where the whole-range gain went. Re-running the shipped arithmetic over the holdout at several
floors:

| floor | Spearman | vs score alone | top 10 (S/P/W) |
| --- | --- | --- | --- |
| none | +0.504 | - | 4/2/4 |
| **45 (was shipped)** | +0.565 | +0.061, CI [-0.061, +0.185] | 5/5/0 |
| 70 | +0.592 | +0.088, CI [+0.023, +0.156] | 7/3/0 |
| **80 (now)** | +0.575 | +0.071, CI [+0.025, +0.125] | 7/3/0 |
| 90 | +0.540 | +0.036, CI [+0.009, +0.073] | 5/3/2 |
| 95 | +0.506 | +0.002, CI [-0.006, +0.011] | 4/3/3 |

Everything from 70 to 92 beats the score significantly and 45 does not, so the finding is
**"restrict the fusion"**, not "restrict it to exactly here". 80 was taken from inside that range
rather than at its argmax because it is not a new free parameter - it is the boundary the original
research already named, the "top two bands" where the score measured -0.191 and the embedding
+0.448. Choosing 70 would be fitting the floor to the data meant to test it.

**This choice is in-sample for this holdout, and the next batch of labels is its test.**

#### The second holdout, 2026-08-30: underpowered, and that is the finding

The first sweep after the floor moved produced 40 more labels - cohort C, drawn while
`FusionFloor` was already 80, so out of sample for both α and the floor. Three cohorts now exist,
separated by when they were labelled:

| cohort | n | scores | rank - score |
| --- | --- | --- | --- |
| A - fitted, <= 2026-08-28 | 194 | 45-100 | +0.091, CI [+0.048, +0.142] |
| B - first holdout, 2026-08-29 | 154 | 46-100 | +0.065, CI [+0.023, +0.115] |
| C - second holdout, 2026-08-30 | 40 | 89-100 | **+0.110, CI [-0.269, +0.467]** |
| **B+C inside the fused region (>= 80)** | **110** | 80-100 | **+0.202, CI [+0.044, +0.360]** |

**C on its own settles nothing, and could not have.** At n=40 every interval in it contains zero -
including the deterministic score's, which is not a claim anybody wants to make. Its embedding
figure is +0.162, CI [-0.162, +0.460], against B's top-band +0.520, CI [+0.264, +0.710]; the two
intervals overlap across [+0.264, +0.460], so C is a wide estimate consistent with a smaller
effect, not a failure to replicate. **One night of labels cannot test a ranking. Do not read a
single cohort as a verdict.**

**Pooled, the fused region holds up.** Across B and C together, restricted to score >= 80 where
the ranking now acts: the score is flat (+0.107, interval containing zero), the embedding is not
(+0.298, excluding it), and the ranking beats the score by **+0.202, CI [+0.044, +0.360]**. B was
used to choose the floor so this is not fully clean, but it is 110 rows and it points the same way
as everything else.

**The floor change did what it was supposed to.** On cohort B the gain went from +0.075, CI
[-0.030, +0.183] under floor 45 to +0.065, CI [+0.023, +0.115] under floor 80. The point estimate
barely moved and the interval halved - which is the signature of removing noise rather than adding
signal, and is exactly what dropping a non-contributing axis should look like.

**Honest status of `MatchRanker`:** the axis is supported by two out-of-sample cohorts pooled and
by neither alone; the floor is fitted to one holdout and has not yet been tested; α has never been
tested at all, and at a floor of 80 the score barely varies inside the fused pool, so the weight
is close to inert there and the sweep that chose it was measuring something else.

#### Validating it immediately found a bug that would have hidden for weeks

Worth recording as a method as much as a fix: the split was validated by triggering
`run-match-sweep` straight after deploying, rather than waiting for 03:30. The first run returned
its ten as **three from 45-59, two from 70-79 and nothing from 60-69**, where round-robin should
give roughly 2/1/1/1.

The band was not empty. `GetUnassessedAsync` applied `Take(limit)` and then filtered out postings
with no description **in memory**, so a request for five rows could return none - and because a
band is ordered by posting id ascending, the same unusable rows sit at its head permanently. They
are never assessed, so they never leave the unassessed set, so the next draw fetches exactly the
same dead rows. Probed against the live database: the 60-69 band returned **nothing at a limit of
five and five usable rows at a limit of ten**, from the same starved head.

Two things made it land precisely here:

- **They concentrate in the low bands.** A posting with no description resolves no concepts, so it
  cannot clear the concept floor, so it scores low - and the stratified sample lives exactly where
  they are.
- **The top-down path could never have noticed.** Nothing reaches 80+ without a description. The
  bug was invisible for as long as the sample was top-band-only, which is to say for as long as
  the thing that made the sample worth fixing.

Same class as the embedding pass starvation of 2026-08-28, and the rule is worth stating plainly:
**a filter applied after a bound is not a filter, it is a silent reduction of the bound.** That is
now three times in this codebase - `BoundedWalk`'s page boundary, the embedding pass's failed
head, and this - so treat `Take(...).Where(...)` as a defect on sight.

After the fix, the same call returned 6 in 80-89 (five of them the top-down shortlist), 1 in
70-79, 1 in 60-69 and 2 in 45-59: the measurement half spread 2/1/1/1 across the four bands,
exactly as designed.

**A note on the test.** The first version of it was vacuous - the usable rows sorted first, so
`Take` never reached the unusable ones and it passed with or without the fix. It was corrected by
putting the description-less postings at the head of the band, and then checked properly by
reverting the fix and watching it fail. **A test written for a bug that has already been fixed
should be run against the unfixed code once**, or it is only evidence that the code compiles.

#### The floor, tested on labels it was not chosen from

The first full night of the 30/10 split ran on 2026-08-31: 40 assessed of 40 requested, nothing
discarded, and the measurement half spread **3/3/2/2** across 45-59, 60-69, 70-79 and 80-89
exactly as designed. That gives a cohort D - everything labelled from 2026-08-30 onward, n=110 -
which is clean for both α and the floor, because the floor changes `RankScore` and not which pairs
get selected.

| floor | Spearman | vs score alone (95% CI) | top 10 (S/P/W) |
| --- | --- | --- | --- |
| none | +0.237 | - | 4/3/3 |
| 45 (previous) | +0.367 | +0.130, CI [-0.105, +0.362] | 7/3/0 |
| 70 | +0.415 | +0.178, CI [-0.015, +0.368] | 7/3/0 |
| **80 (shipped)** | +0.404 | **+0.168, CI [+0.002, +0.340]** | 7/3/0 |
| 90 | +0.259 | +0.023, CI [-0.008, +0.062] | 5/4/1 |

**80 is the only setting whose gain over the score is significant out of sample.** But
`floor 80 - floor 45` is +0.038, CI [-0.054, +0.134] - **not significant**. So the change is
vindicated rather than proven: 80 works and 45 does not quite reach significance, and the direct
comparison cannot separate them. That is the same shape as the original finding, which was
"restrict the fusion" rather than "restrict it to exactly here".

**The core claim now has three independent cohorts behind it.** Inside the fused region of cohort
D (score >= 80, n=88) the score is flat at **-0.016**, CI [-0.249, +0.223], and the embedding is
**+0.244**, CI [+0.027, +0.434]. With B's top band (+0.520) and C's (+0.162, wide), every cohort
points the same way and the two that are large enough exclude zero.

#### What is left on this topic, honestly

**The engineering is done. What remains is accumulation, and it now happens by itself.**

- **The corpus-wide claim is still open**, and cannot be closed yet: cohort D holds only 22 rows
  below score 80. At ten stratified labels a night it will hold roughly 150 in a fortnight, which
  is when re-running `balanced.py` becomes worth doing. Until then the honest line is unchanged -
  **better at the top of the list, not established as better overall.**
- **α has still never been tested and probably cannot be, at this floor.** Above 80 the score
  barely varies, so the weight is close to inert - which is itself the answer: it is not worth
  re-sweeping until something moves the floor back down.
- **Nothing else here needs code.** Re-running the analysis on more of the same data is not a next
  step, it is the same step with a larger n.

#### Cost, measured rather than estimated

71 documents cost 54,271 tokens, so about 764 per posting after truncation to 6,000 chars.
The whole 4,078-posting corpus is therefore **~3.1M tokens, one-off** - pennies on a small
embedding model, and the cheapest model call this system makes by a wide margin. Re-embedding
on a vocabulary change costs the same again, which is what makes it affordable to treat as
versioned data.

#### Why the architecture takes it cleanly

- **Cosine similarity is pure arithmetic.** `MatchScorer` can take a precomputed similarity
  and stay pure and Azure-free - the property that makes its numbers exactly assertable.
- **This is not a kNN search.** One profile against ~4,000 postings, nightly, in memory. The
  sweep already loads every posting's facts and can load vectors too, so **the SQL `vector`
  type is not needed for the matching path at all** - which matters, because this database is
  Basic DTU: 5 DTUs, under one vCore, HDD-backed.
- **Managed identity already works.** An embeddings deployment on the existing account needs
  the same `Cognitive Services OpenAI User` role and no new secret.
- **The AI call ledger exists**, so an embedding pass is observable from its first run.

#### Constraints found, with the ones that bite

| | |
| --- | --- |
| SQL `vector` max dimensions | **1998** - `text-embedding-3-large` at 3072 **will not fit** |
| `text-embedding-3-small` | 1-1536 dims, MRL-trained, so truncation is graceful |
| Storage, 4,078 postings | ~25 MB at 1536 dims, ~8 MB at 512 (Basic caps at 2 GB) |
| Region | both `-3-small` and `-3-large` are available in Spain Central |

- **`sp_describe_first_result_set` does not report the `vector` type correctly**, so EF and
  many drivers see `varchar`/`nvarchar`. A mapping problem, not a theoretical one.
- **You cannot upgrade between embedding models.** Changing the model *or the dimension count*
  means re-embedding everything, so it needs a version constant with the same discipline as
  `EnrichedPosting.CurrentVersion`.
- **A freshly created GlobalStandard deployment answers 404 intermittently** while it
  propagates across the global pool - measured as 200, 404, 200 on three consecutive calls.
  Treat `DeploymentNotFound` as transient for a new deployment or a backfill will die on it.

#### Two things to rule out now

- **Never as a replacement for the concept graph.** A similarity is a number with no
  explanation. `MatchResult.Matched` is presentable to the candidate rather than merely
  diagnostic, and every point traces to an assertion. This is an *additional axis* or nothing.
- **Not in-database `AI_GENERATE_EMBEDDINGS`.** It would bypass the AI call ledger, and Basic
  tier caps external connections at three.

#### Rejected: writing a virtual advert from the CV and comparing advert to advert

A CV and a job advert are different genres - one says *"I built X at Y"*, the other *"you
will build X, we offer Z"* - so comparing them mixes topical similarity with genre distance.
The obvious fix is HyDE: ask the model to write the advert this candidate is the ideal
applicant for, embed **that**, and compare advert to advert. It was tried on the same 70
pairs. **It is materially worse.**

| query side | Spearman vs the model | Weak in top 20 | Strong in top 20 | Strong buried |
| --- | --- | --- | --- | --- |
| the CV itself | **+0.501** | 3 | 8 | 2 |
| a generated advert | +0.256 | 6 | 7 | 4 |
| mean of both | +0.379 | - | - | - |

**Genre matching worked exactly as predicted, and that is the problem.** Every similarity
rose - the range moved from 0.34-0.58 to 0.50-0.76 - because the query now reads like the
documents it is compared against. But the Strong-to-Weak gap *shrank*, from +0.045 to +0.030,
and ranking lives entirely on that gap. **It raised the floor more than the ceiling.**

The likely reading, from the movers: five of the ten biggest gainers were Weak matches. A
generated advert is written in advert register - "what the role involves", "you will design
and own" - and that register is precisely what every advert shares, including the management
and consulting roles that were the residue in the first place. Closing the genre gap moved
the query toward the generic middle of advert-space, which is where the confusion already
was.

So **the genre gap was carrying signal rather than noise.** The CV's distinctness is part of
what separates a .NET role from a Legal Counsel one.

Two honest limits on this result. It tests one prompt and one generated advert, so it rules
out this formulation rather than the whole idea - though halving the correlation is not a
marginal difference. And two runs of the *same* CV measurement gave +0.488 and +0.501, so
treat anything under about 0.02 as run-to-run noise; the HyDE gap is far outside that.

#### What the statistics actually license, and two claims above that were too strong

Researched and then applied to our own numbers. Three corrections, one confirmation.

**Confidence intervals at n=70** (Fisher z with the 1.06 Spearman correction):

| signal | Spearman | Kendall tau-b | 95% CI |
| --- | --- | --- | --- |
| deterministic score | -0.198 | -0.143 | **[-0.43, +0.05] - includes zero** |
| CV embedding | +0.501 | +0.367 | [+0.29, +0.67] |
| HyDE embedding | +0.256 | +0.179 | [+0.01, +0.48] |
| score x CV | +0.432 | +0.318 | [+0.21, +0.61] |

**Paired bootstrap on the differences** (10,000 resamples - paired, because these share the
same 70 items and the product contains the embedding as a factor, so independent intervals
would be the wrong test):

- **CV beats HyDE: +0.245, CI [+0.082, +0.417] - significant.** The rejection above holds.
- **CV vs score x CV: +0.069, CI [-0.012, +0.152] - NOT significant.** The table above should
  not be read as showing the embedding alone beats the combination. It does not.
- CV beats the deterministic score: +0.699, CI [+0.41, +0.96] - significant.

**Kendall tau-b is the more defensible statistic here** - integer scores with many ties - and
runs about 0.73x the Spearman value throughout. Do not compare a tau against the Spearman
numbers already recorded; recompute both if switching.

**The 0.015 run-to-run wobble noted earlier is not the dominant uncertainty.** It is
judge non-determinism. Sampling error at n=70 is an order of magnitude larger - CI half-widths
of ~0.25. Re-running the judge on the same pairs buys nothing; **only new labels tighten these
numbers, and halving the interval needs roughly 270 labels** - about five more nights.

**Thorndike's range-restriction correction was tried and is not usable here.** The assessed 70
have an SD of 2.98 against 12.67 across the 2,943 eligible pairs, a 4.3x restriction, and the
Case II correction turns -0.198 into -0.652. But applying it to the CI bounds gives roughly
[-0.89, +0.22] - still spanning zero, and enormous. **Correcting a non-significant estimate
produces an amplified non-significant estimate, not new information.** Recorded so nobody
quotes the -0.652.

**The pooling bias is the thing to fix, and it is fixable for free.** The 70 labels were
selected by the deterministic score - a single selector, which is a sharper version of the
pooling bias TREC evaluation has documented since Zobel 1998, and closer still to the
missing-not-at-random problem in recommender evaluation (Schnabel et al., ICML 2016). Every
number here therefore describes ranking **only within the top decile of deterministic score**.
That is a coverage problem, not a variance problem: no interval widens to cover it.

The fix costs nothing extra. **Spend the assessment budget on a stratified sample across the
score range instead of the top 40.** Same budget, and it is the only way any of these
correlations become statements about the corpus rather than about its top decile.

**Done - `SweepRequest` now takes `MinScore` and `MaxScore`.** Triggering more sweeps used to
just walk further down the same ranked list; a band makes a stratified sample reachable. The
timer is unchanged.

A band changes the *ordering* as well as the filter, and it has to. Without one the shortlist
takes the highest-scoring unassessed pairs, which is right - the budget should go where the
arithmetic is most hopeful. With one, taking the top of the band would reproduce the same
restriction a level down (ask for 80-89, get forty 89s), so the order falls back to posting id,
which is scrape order and uncorrelated with score.

**Done, and it reverses the headline finding.** 115 assessments were drawn across the four
bands below 90, giving 195 assessed pairs spanning scores 45 to 100 instead of 90 to 100.

| sample | n | score SD | Spearman vs the model |
| --- | --- | --- | --- |
| top band only (what every earlier number used) | 70 | 2.98 | **-0.198** |
| full range | 195 | 16.77 | **+0.315**, CI [+0.174, +0.443] |

**The deterministic score is positively and significantly correlated with the model's judgement.**
The -0.198 was range restriction and nothing else. A Strong match averages a score of 85.4; a
Weak one averages 71.5. Every conclusion drawn from the old number needs re-reading, and this
file's earlier framing - that the score "carries no further signal" - was true only of the slice
it was measured on.

**But the sharper finding is where the score fails, and it is not where anyone would guess:**

| band | Strong | Weak |
| --- | --- | --- |
| 90-100 | 25% | **31%** |
| 80-89 | 25% | 20% |
| 70-79 | 14% | 17% |
| 60-69 | 6% | 41% |
| 45-59 | 5% | 52% |

**The top band carries a higher Weak share than the two below it.** 31% against 20% and 17%.
The score orders the corpus well and then *inverts at the very top* - which is exactly 1.3,
now quantified: the roles that reach 90-100 on one widely-held skill are concentrated in the
band a candidate actually looks at.

That is a different problem from "the ranking is wrong", and it wants a different fix. The
score is a good filter and a bad final sort. Whatever goes on top - the verdict, the embedding,
a role-family axis - only has to re-order the top two bands, not replace the score.

**An assessment costs 716 tokens, measured**, so those 115 labels cost roughly 82k tokens. The
sizing question was never a cost question.

**Fifteen consecutive batches, 115 of 115 assessed, zero discarded.** Before the fix in 1.2, five
of nine batches were discarded whole. That is the strongest evidence yet that the quoted index
was the cause.

#### Mean-centring: tested, and the mechanism worked without the ranking improving

The leading recommendation from the embedding research was corpus mean-centring - subtract
the mean advert vector from everything before taking cosine, removing the shared
"advert-ness" direction. Anisotropy is the named cause of compressed similarity spread, and
Mu & Viswanath (all-but-the-top) and Su et al. (BERT-whitening, which moved median pairwise
cosine from 0.833 to -0.010) report large gains on symmetric similarity tasks.

Tested with the mean estimated from a random 143-posting sample of the corpus - deliberately
not from the assessed 70, which were selected for being good matches and whose mean is not
the corpus mean.

| | Spearman | range | Strong-Weak gap | Weak in top 20 | Strong buried |
| --- | --- | --- | --- | --- | --- |
| baseline cosine | **+0.501** | 0.341 to 0.579 | +0.045 | 3 | 2 |
| mean-centred | +0.366 | -0.195 to 0.162 | **+0.062** | **1** | 5 |

Paired bootstrap: **-0.135, CI [-0.329, +0.065] - not significant.**

**The mechanism did exactly what the theory says and it did not help.** The Strong-to-Weak gap
widened by 38% and the range nearly doubled, so the shared direction was real and removing it
did re-expand the spread. Weak matches in the top 20 fell from 3 to 1. But genuine Strong
matches buried in the bottom 20 rose from 2 to 5, and rank correlation did not improve.

That is worth more than the negative result itself: **compressed spread was my diagnosis of
the HyDE failure, and widening the spread turns out not to be sufficient.** Spread was a
symptom, not the cause. Do not reach for whitening or all-but-the-top on the strength of that
diagnosis without re-testing.

Caveats: the mean came from 143 postings rather than all 4,078, only mean-centring was tried
(not top-principal-component removal), and at n=70 the interval is wide enough that a real
effect of this size could hide in it.

#### Other findings worth not rediscovering

- **The ledger records tokens now**, split so reasoning is visible on its own. Duration is not
  cost: a batch of ten adverts and a batch of one differ by an order of magnitude in tokens and
  barely at all in wall clock. First reading: 20 assessments cost 14,314 tokens, 11% of it
  reasoning. Zero means not reported, which is not the same as free.
- **`RoleFamily` is already computed, stored, indexed - and unread by the scorer.** Measured
  earlier and rejected because `Unknown` is 38% of the corpus and holds the residue and the
  best matches alike. The research adds the part that resolves it: `Unknown` conflates "no
  signal" with "different profession" because the classifier only enumerates *tech* families.
  Add Legal, Tax, Academic, Healthcare, HR, Sales and the residue classifies instead of
  falling through. **This is the cheapest remaining fix for 1.3 and needs no model call.**
- **LinkedIn published our exact failure.** *Learning to Retrieve for Job Matching* describes
  "a job posting aims to recruit a backend developer proficient in Java; however, the system
  may match frontend developers with Java expertise" - and their fix is deterministic rules
  layered on the learned signal, not a retrained embedding. Our cheap-corpus-pass into
  expensive-shortlist-pass is also the standard retrieve-rank-rerank shape.
- **RRF is the wrong fusion tool here.** Its k=60 was "fixed during a pilot investigation and
  not altered" (Cormack et al., SIGIR 2009) - a rounded number, not a derived constant. Bruch
  (ACM TOIS 2023) shows a convex combination beats RRF significantly, p<0.01, on every dataset
  tested. RRF exists for unnormalisable scores across many black-box systems with no labels;
  we have two signals with known ranges and some labels. **Normalise using statistics from all
  ~4,000 pairs, never from the 70** - min-maxing on a range-restricted sample is the textbook
  error - then fit one convex weight by leave-one-out cross-validation.
- **No learned ranker is justified at n=70.** LambdaMART and friends need orders of magnitude
  more. One coefficient, cross-validated, is the ceiling - which is also all the C# constraint
  would accept.
- **Cohere Rerank on Azure AI Foundry requires API-key auth**; Entra ID is not supported on
  that route today. It would breach the no-secrets rule. Azure AI Search's semantic ranker is
  the Entra-compatible reranker, at roughly $0.10 a night for this corpus, but needs an index
  kept in sync - real engineering, unverified gain on this domain.
- **Do not prefix OpenAI embeddings with `query:`/`passage:`.** That scheme belongs to E5 and
  friends; `text-embedding-3-*` was never trained on those tokens. Cargo-culting it is more
  likely to add noise than signal.
- **Azure OpenAI embeddings cannot be fine-tuned at all** - a hard blocker, not a cost
  trade-off. The strongest result found anywhere (ConFit v2, +13.8 recall / +17.5 nDCG over
  `text-embedding-3`) requires an open-weights model and far more labels, so it is a roadmap
  item rather than an option.

#### Legal, and it changes with distribution

**NYC Local Law 144 does not apply** - it governs employers and agencies evaluating
applicants; this ranks postings for one person. **The EU AI Act Annex III point 4 makes
recruitment AI high-risk**, and the Commission's AI Act Service Desk has said job boards
recommending vacancies to seekers are in scope - **but Article 3(4) excludes use "in the
course of a personal non-professional activity"**, which is exactly this.

**That carve-out disappears the moment this is offered to other job seekers.** Worth knowing
before, not after.

#### What was built, and what is left

Done, on 2026-08-28:

1. **An `embeddings` deployment in Bicep** - `text-embedding-3-small` v1, GlobalStandard, 350k
   TPM, threaded to both hosts as `Ai__AzureOpenAi__EmbeddingDeployment`. The hand-made probe
   deployment the experiment used was deleted rather than left as drift; the account now carries
   exactly the three the template declares.
2. **`PostingEmbeddings` and `ProfileEmbeddings`**, filled by `EmbedCorpusFunction` (03:00 UTC,
   plus `run-embed-corpus`) and by the sweep respectively, reporting to the ledger as
   `text-embedding`. Staleness is `EmbeddingVector.EmbeddingVersion` plus, on the posting side,
   the `ContentHash`/`DescriptionLength` pair `HasMaterialChange` already uses - so "what needs
   embedding" is a join over short columns rather than a nightly pull of every description.
3. **`MatchRanker`**, pure and Azure-free like `MatchScorer`, with 15 tests pinning exact
   numbers. The score is untouched; `JobMatches` gained `Similarity`, `RankScore` and
   `RankerVersion`, and the list orders by `RankScore`.

Left:

4. **Done twice, and the second time said the sample is the bottleneck.** 154 stratified labels
   moved `FusionFloor` from 45 to 80; the 40 that followed could not confirm or deny it, because
   40 rows in one band cannot. Pooled across both holdouts inside the fused region the ranking
   beats the score by +0.202, CI [+0.044, +0.360], on 110 rows.
   **The remaining work is not more analysis, it is more labels of the right shape** - which is
   4b, and it is now the highest-value item in this file. Nothing else about the ranking can be
   settled until the sample stops being top-band-only: not the floor, not α, and not the
   corpus-wide figure.
4b. **Done on 2026-08-30, and validated by triggering a sweep rather than waiting for the
   timer.** 30 of the 40 go to the shortlist, 10 are drawn round-robin across 45-59, 60-69,
   70-79 and 80-89. **It costs nothing** - the measurement rows are merged into the same batches
   and the assessor sends the profile once per batch, so they cost ten adverts' worth of tokens
   rather than a second pass; a night is four batches of ten either way. The estimate of 7k extra
   tokens in the previous version of this line was wrong.
   `StratifiedShortlist` holds the merge, pure and tested for the reason `BoundedWalk` is.
   A band-bounded request stratifies nothing, so the hand-drawn band route is unchanged.
   The measurement share is capped at a **quarter** of the budget, not a half. The nightly forty
   is unaffected - a quarter of it is the ten this wants - but the HTTP route's ten drops to two,
   which matters because that route exists for somebody who has just filled in their profile and
   has nothing to look at until morning. Spending half their single call on a measurement sample
   would take the shortlist away from the only person it was for.
   Verified in production on 2026-08-31: 3/3/2/2 across the four bands, plus thirty top-down.
5. **Then revisit 1.3.** The embedding now has 100% coverage, so the question is whether the top
   of the list still needs the verdict to be tolerable. On the evidence above it needs it less -
   zero Weak in the top 10 - but "less" is not "not at all", and this is the in-sample number.
6. **A second profile would break the ranker's assumptions in a useful way.** Everything measured
   here is one candidate against one corpus. `RankScore` is pool-normalised per profile, so it is
   correct by construction across candidates, but nothing has tested that.
7. **The first sweep after a ranker change does not fit in an HTTP request.** `run-match-sweep`
   returned 504 at 249s on the run that applied the ranking - every row's key moved off the
   migration's seeded value, so all ~4,700 were rewritten. The work completed anyway, because the
   504 is the gateway rather than the function and the writes are idempotent, and the check is to
   read `/matches` rather than to retry blindly. Steady-state sweeps write only what moved, and
   the timer has minutes rather than 230 seconds. Worth knowing before somebody bumps
   `MatchRanker.CurrentVersion` and reads the 504 as a failure.

---

## 2. What changed this round

Five changes. The first four are deployed and verified against the live corpus; the fifth - the
ranking - is built and tested but has not yet met real data. The `CurrentVersion` remarks on
`EnrichedPosting`, `MatchResult` and `MatchRanker` carry the same history where the code can see
it.

### 2.1 The concept floor asks what the demands are, not how many

The open item the previous revision left as "thin but genuine evidence still scores 100",
deliberately undone because two scoring rules had been changed on judgement alone and one was
wrong. Measured before shipping, and the measurement changed the answer.

**Both directions the previous revision suggested were measured and rejected.** Damping the
score by `Coverage` punishes the employer's terseness rather than the thin evidence: it dropped
a `.NET Developer` with twelve concepts read out of the top 60 for stating no salary, while
keeping a Product Manager that answered every peripheral axis on one word. Damping by the number
of demands, `n/(n+k)`, reproduces the withdrawn count rule exactly — it removes `Yardi
Implementation Consultant` (100, one concept) and `Senior Software Engineer - C#` (100, two)
together — and it ranks by how long an advert is, which is a fact about the recruiter.

What separates them is **which concept carried the match**. Every wrongly-ranked thin match
rested on `skill.agile` or an `area.*` board tag; every rightly-ranked one on a concrete
technology. The vocabulary already records that as `tagOnly`, and every domain is `tagOnly`
implicitly, so the floor now asks whether anything *discriminating* was demanded rather than
whether anything was. `Concept.IsDiscriminating` is that flag plus domains, and `skill.agile`
joined the flag.

Measured: 117 of 3,908 scoring postings fall to zero and eight leave the top 60, every one
correctly — two Transformation Managers and a Senior Product Manager on the single word agile, a
Space Data Engineer at 100 on one board tag reading "Data Engineering". **No good match went
with them**, against the withdrawn rule's ledger of one bad and four good.

### 2.2 `deploy.yml` has a concurrency group

Runs were concurrent and landed in whatever order the runners freed up; a stale queued run could
deploy over a newer commit, which nearly happened during an Actions outage. The group serialises
them and cancels the pending run when a newer one queues behind it. `cancel-in-progress` is
false deliberately — a run already executing may be part way through a Bicep deployment or
`dbadmin migrate`.

### 2.3 The reprocess endpoint's bound can act, and is tested

`POST /api/reprocess` returned 504 for `limit=50` and again for `limit=25`. The reasoning behind
the original bound was right — the continuation token is only accurate at a page boundary — but
**a page can cost more than the whole budget**: pages of five blobs took 4s, 11s, 12s, 47s and
**151s** against a 150s budget.

`BoundedWalk` now stops *between items* and hands back the token for the start of the page it
was in, so the resume point is still a real boundary and nothing is skipped; the cost is redoing
that page's finished items, which idempotency makes free. It will not stop before one page has
completed, or the next call would stop in the same place forever.

It is generic over the item and takes a clock, so **it needs no Azure types to test** — which is
what unblocked a test open since the endpoint was written. The previous revision called it
untestable because faking a `BlobContainerClient` is hard; the answer was that the bound was
never about blobs. `tests/JobPlatform.Ingestion.Tests` is new and holds seven tests.

Verified live afterwards: `limit=50`, the request that had failed twice, returned 200 in 161s,
and a full re-enrichment ran 35 blobs in 4 calls with 0 failures.

### 2.4 A rule list written in a spelling the tokeniser destroys

`RoleFamilyClassifier` called `.NET Developer` `Unknown`. The rule **was** there: `.net`,
`node.js` and `ui/ux` had been in the lists since the file was written, and `TitleTokenizer`
splits on `.` and `/`, so by the time a rule saw the text those were already `net`, `node`+`js`
and `ui`+`ux`. Three dead entries that read exactly like working ones.

`TitleTokenizer.DottedNames` folds the three names whose spelling contains a separator. Measured
over 2,709 distinct titles: 13 moved out of `Unknown`, all unambiguous .NET backend roles, and
nothing moved between families or into `Unknown`.

Two rejected attempts, both caught by diffing the whole corpus rather than the examples in hand:
making `.` a word character fixed .NET and broke `Sr.Product Manager` and `React.js Developer`;
folding without a leading space broke `C#.NET` and `VB.NET`.

**Asserting the classifier alone would not have caught any of this** — a dead entry and a
working one look identical from its output. The tests assert the tokenisation too.

### 2.5 The matches page is ordered by the embedding, not by the score

The whole of 1.6, and the evidence is there rather than repeated here. In one line: the score
orders the corpus at +0.315 and its own top band at **-0.191**, the embedding does the reverse,
and the combination orders 68.5% of judged pairs correctly against the score's 61.3%.

What a reader of this file most needs to know about it:

- **`MatchResult.Score` did not move and no assessment was cleared.** The ranking is a separate
  column with its own version constant. That was the design constraint, not a nicety - folding it
  into the score would have invalidated the 195 labels it was fitted on.
- **`RankScore` must never be displayed.** It is min-maxed per profile per sweep, so the top of
  any pool is exactly 100 and nothing is comparable across pools. `Similarity` is the durable
  number and is the one to argue with.
- **The migration seeds `RankScore` from `Score`**, so the page is ordered exactly as it was
  yesterday between the deploy and the first sweep, rather than by a column of zeroes.
- **Nothing below score 45 is re-ordered.** That is the edge of the labelled range, and it is
  what stops a posting the concept floor scored at zero from climbing on textual resemblance.

---

## 3. How measurement is done here

Worth repeating rather than reinventing. It is what caught the withdrawn count rule, and it
changed the answer on both 2.1 and 2.4.

Read the corpus **once** into a local JSON cache — the enriched columns and the concept
assertions, which is exactly what `GetPostingFactsAsync` selects — then evaluate every candidate
rule offline against that cache. Nothing is written to the database and no profile row is
needed: `MatchScorer` is pure, so a `CandidateFacts` built in memory scores the whole corpus.

Diff the **whole** top 60 before and after, not the examples you already have in mind. Every
regression this round was found that way and none would have been found otherwise.

Two access notes:

- **The SQL firewall holds only `AllowAllWindowsAzureIps`.** Reading the database from a laptop
  needs a temporary rule for the client IP, and deleting it immediately afterwards. The database
  is `azureADOnlyAuthentication`, so the rule grants nobody access without a token for a database
  user — but leaving it behind on a public portfolio project is exactly the thing to avoid.
  Cache the corpus on the first read and close the hole rather than keeping it open.
- **A token for the API avoids touching SQL at all**, which is how the vocabulary change was
  verified through `/postings/facets`:

```bash
CLIENT=$(az containerapp show -n <app> -g <rg> \
  --query "properties.template.containers[0].env[?name=='AzureAd__ClientId'].value | [0]" -o tsv)
az account get-access-token --resource "api://$CLIENT" --query accessToken -o tsv
```

Verify a deploy landed before trusting a behaviour change:

```bash
az containerapp list --query "[0].properties.template.containers[0].image" -o tsv
```

---

## 4. Environment notes

- **`jq` is not installed.** `gh --jq` works (built in); a standalone `jq` in a pipe silently
  produces nothing. Use `python -c` for JSON in shell loops.
- **Two shells, two syntaxes.** PowerShell is primary; a Bash tool is also available. Bash line
  continuations (`\`) pasted into PowerShell fail with
  `Falta una expresión después del operador unario '--'`.
- **MSYS mangles Azure resource paths.** `az ... --ids /subscriptions/...` fails with
  `invalid resource ID: C:/Program Files/Git/subscriptions/...` under Git Bash. Prefix with
  `MSYS_NO_PATHCONV=1`, or pass `--resource-group` and `--server` instead of `--ids`.
- **Heredocs and Python don't mix well here.** Multi-line edit scripts break Bash heredoc
  quoting. Write the script to a file first.
- **Editing a CRLF file from Python needs `newline=""` on both the read and the write.** Reading
  with it and writing without it turns every line ending into `\r\r\n` — a whole-file rewrite
  that looks like a small diff until you check the bytes.
- **`az monitor app-insights query` ignores `ago()` in the query.** It applies its own window,
  defaulting to the last hour, so a query for last night returns zero rows and looks like an
  absence of telemetry rather than a wrong question. Pass `--offset 12h`.
- **Blob listing needs a data-plane role.** Subscription Owner is not enough; that needs
  `Storage Blob Data Reader` or better. `GET /api/v1/runs` is a usable proxy for what is in the
  container — one row per ingested blob.

Resource names are deliberately absent from this file — the repo is public. Discover them with
`az containerapp list`, `az functionapp list` and `az sql server list`, and see `CLAUDE.md` for
the placeholder convention used in commands.
