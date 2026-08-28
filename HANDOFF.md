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

Start with the smallest useful piece: have the sweep log what it *asked for* against what it
*wrote*, and warn when they diverge. That alone turns last night's 55% loss from invisible into
obvious, it ships on its own, and it changes no behaviour.

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

The fix is to stop correlating by position: give each role an opaque id and require it echoed
back. That is this codebase's own lesson from the other side of the same problem — *"A batch API
echoes a `custom_id` per request, so correlation is the platform's problem."* The assessor is the
last path still trusting ordering.

Reducing the batch size is the tempting shortcut. It only makes each loss smaller.

Before changing anything, log one rejected **response** — never the prompt — and the
misalignment will be obvious rather than guessed at.

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

---

## 2. What changed this round

Four fixes, all deployed and verified against the live corpus. The `CurrentVersion` remarks on
`EnrichedPosting` and `MatchResult` carry the same history where the code can see it.

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
