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

**In flight.** 1.1 has all four call sites, a dashboard page and a replay route; 1.2 has a
candidate fix. Both are marked *Progress* below, with what remains.

**1.6 is built and has not yet run.** The matches page is now ordered by `MatchRanker` rather
than by the score, and until `run-embed-corpus` has been through the corpus that ordering is
identical to the old one - correctly, and invisibly. `POST /api/run-embed-corpus` is the first
thing to do after deploying, and `EmbedSummary.Embedded` against `.Corpus` is how to tell it
worked.

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

**Remaining:**

1. **Verify extraction and batch records in production.** Only the assessor has been seen writing
   a real record. Extraction is skipped for unchanged content by design - `PostingExtractions` is
   keyed on a hash of the text - so it will not be exercised until the next scrape brings new
   postings. Check `GET /api/v1/ai-calls/summary?days=2` after one and expect
   `posting-extraction` beside `candidacy-assessment`.
2. **Turn `AiLedger:RecordPrompts` on when actually debugging, and off afterwards.** It is an app
   setting on both hosts, so it needs no deploy either way.
3. **Consider the change feed.** The Realtime component in `../model.md` is still the one piece
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

**This is a hypothesis, not a confirmation**, and the confirmation ships with it: the warning
used to say only "unusable" and could not tell a wrong type from an out-of-range number from a
repeat. It now names the `JsonValueKind` and the value. **Check the next run.** If the warnings
stop, it was the type. If they continue, the log now says which of the three it is.

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

It is now unblocked, it is the most demonstrable thing in the system for a portfolio repository,
and one call would tell you whether it works. It is also the only consumer of the `writing`
deployment, which is the one place a missing registration shows up as CVs quietly written by the
cheap model — `AiRegistrationTests` asserts both resolve, but nothing has exercised it live.

Cheapest interesting thing available.

### 1.5 Extraction coverage

92.6% of the corpus (3,775 of 4,078 postings) carries at least one model assertion. The 0.490
figure an earlier revision called the graded share is the share of *assertions* that are
model-sourced, not the share of postings read. The two are easy to confuse and justify very
different work.

What is left is thinner than it looked: 168 postings carry no concepts at all, and 303 have
never been read by the model. The unresolved-mention log remains where the next vocabulary fix
comes from — it is what surfaced `containers`, though `agile` came from reading the ranking
rather than the log, which suggests the log is not the only place worth looking.

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

**What is not done and is the first thing to check.** The pass has never run against the real
corpus. `POST /api/run-embed-corpus` is bounded to ~150s and resumable, so a first pass over
~4,000 adverts is several calls; the timer at 03:00 UTC will also chew through it. Until it has,
every match ranks on its score alone - which is the old behaviour, correctly, and is
indistinguishable from the new one without looking. **`EmbedSummary` reports `Embedded` against
`Corpus`, and that ratio is the check.**

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

4. **Run the pass and re-measure against the corpus** the way §3 describes - the top-60 diff, not
   the six rows anybody already has in mind. The α=0.6 is fitted on 195 labels and the honest
   summary of the bootstrap is that anything in [0.6, 0.7] is indistinguishable.
5. **Then revisit 1.3.** The embedding is a signal with 100% coverage once the pass has run, so
   the question is whether the top of the list still needs the verdict to be tolerable. The
   verdict counts in the top 30 went 6/14/10 (S/P/W) on the score to 11/16/3 combined on the
   assessed 195, which suggests it does not need it as badly - but that is 195 rows deep and
   ranked within the assessed subset, not the corpus.
6. **A second profile would break the ranker's assumptions in a useful way.** Everything measured
   here is one candidate against one corpus. `RankScore` is pool-normalised per profile, so it is
   correct by construction across candidates, but nothing has tested that.

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
