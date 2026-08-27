# Handoff

State of the matching work as of 2026-08-27, for whoever picks this up next.

Conventions, architecture and the standing rules live in [`CLAUDE.md`](CLAUDE.md). This file
is only the delta and the open work. The previous round of fixes — the `containers`
vocabulary bug, the enrichment version coupling, each producer clearing only its own rows, the
withdrawn evidence-count rule, and the bounded admin endpoints — is described in `CLAUDE.md`
and in the `CurrentVersion` remarks on `EnrichedPosting` and `MatchResult`.

**Deployed and verified.** `main` is at the commit the container app runs, the corpus has been
re-enriched, and the working tree is clean. No sweep has run because no profiles exist. See §3
for what deploying this involved, including a real defect it surfaced.

---

## 1. What changed

### 1.1 The concept floor asks what the demands are, not how many

This is the open item the previous handoff left as §3.1: thin but genuine evidence scoring 100.
It was left undone deliberately, because two scoring rules had been changed on judgement alone
and one of them was wrong. It has now been measured before shipping, and the measurement
changed the answer.

**The two directions the previous handoff suggested were both measured and both rejected.**

Damping the score by `Coverage` — the one that looked obvious, and the reason `Coverage`
already existed — punishes the employer's terseness rather than the thin evidence:

| posting | concepts read | coverage | outcome |
| --- | --- | --- | --- |
| `.NET Developer - St Albans` | 12 | 0.83 | wrongly dropped from the top 60 |
| `Senior Platform Engineer` | 12 | 0.40 | wrongly dropped |
| `Full Stack .NET Developer - C#, Azure, REST API` | 4 | 0.25 | wrongly dropped, scored 96 |
| `Product Manager at Aries` | 2 | 0.85 | wrongly **kept** |

Damping by the number of demands, `n/(n+k)`, reproduces 1.4's ledger exactly. At k=1 it removes
`Yardi Implementation Consultant` (100, one concept) and `Senior Software Engineer - C#` (100,
two concepts) together — no threshold separates them, which is the same sentence the previous
handoff wrote about `.NET Developer` and `Home Delivery Driver`. It also ranks by how long an
advert is, which is a fact about the recruiter and not about the fit.

**What separates them is which concept carried the match.** Reading the actual evidence behind
the corpus top 60 makes it plain: every wrongly-ranked thin match rested on `skill.agile` or on
an `area.*` tag a board had applied, and every rightly-ranked thin one rested on a concrete
technology.

The vocabulary already records that distinction as `tagOnly` — *"in prose they mean nothing;
almost every advert contains them"* — and every domain is `tagOnly` implicitly. So the version 2
floor now asks whether anything **discriminating** was demanded, rather than whether anything
was. `Concept.IsDiscriminating` is that flag plus domains, and `skill.agile` joined the flag:
nearly every advert says agile, which is precisely what it describes.

Measured against the corpus, 117 of 3,908 scoring postings fall to zero. Eight leave the top 60:

| posting | was | evidence |
| --- | --- | --- |
| Space Data Engineer | 100 | 1 × board tag `area.data` |
| Real-Time Data Engineer | 100 | 1 × board tag `area.data` |
| Senior Product Manager — Partner Algorithms | 100 | `skill.agile` |
| Senior Platform Engineer — Energy Data | 100 | `skill.agile` |
| Senior Engineer @ Xero | 98 | `skill.agile` |
| Senior Manager, Regional Data Center Development | 94 | `area.cloud`, `area.ml` |
| Transformation Manager (×2) | 92 | `skill.agile` |

**No good match went with them**, which is the whole point — 1.4 removed one bad match and four
good ones. Eight genuine C#/.NET roles took their place. `Senior Software Engineer - C#` (100,
two concepts) and `Senior Full-Stack Engineer | C#, .NET & Azure` (100, four) both survive: what
they rest on is as thin as the Transformation Manager and it is a real requirement.

Five tests in `MatchScorerTests` pin both directions, including the one 1.4 got wrong — one
concrete skill still carries a match alone — and that an unknown key counts as discriminating,
so removing a concept from the vocabulary cannot silently zero the postings still referencing
it. Two in `ConceptGraphTests` pin agile in prose against agile in a board's skills field.

`MatchResult.CurrentVersion` is 5 and `EnrichedPosting.CurrentVersion` is 4. The remarks on
both carry this history, including the two rejected rules, so neither is retried from scratch.

### 1.2 `deploy.yml` has a concurrency group

Previously §3.2. Runs were concurrent and landed in whatever order the runners freed up; a
stale queued run could deploy over a newer commit, which nearly happened during an Actions
outage. The group serialises them and cancels the pending run when a newer one queues behind it.

`cancel-in-progress` is **false** deliberately: a run already executing may be part way through
a Bicep deployment or `dbadmin migrate`, and killing the runner there trades a recoverable delay
for a half-applied change. Waiting costs at worst a few minutes of the older image being live,
and the newer run still lands last.

---

## 2. How the measurement was done

Worth repeating rather than reinventing, because it is what caught 1.4 and what changed the
answer here.

The corpus was read **once** into a local JSON cache (4,078 postings — the enriched columns and
the concept assertions, which is exactly what `GetPostingFactsAsync` selects), and every
candidate rule was then evaluated offline against that cache. Nothing was written to the
database, and no profile row was created: `MatchScorer` is pure, so a `CandidateFacts` built in
memory scores the whole corpus without a profile existing anywhere.

The synthetic profile used was a senior C#/.NET engineer in London. It reproduced the previous
handoff's reported numbers exactly — `Space Data Engineer` and `Yardi` both at 100 with one
concept each — which is what made the diff trustworthy.

Two environment notes, since neither is obvious:

- **The SQL firewall holds only `AllowAllWindowsAzureIps`.** Reading the database from a
  laptop needs a temporary rule for the client IP:
  `az sql server firewall-rule create -g <rg> -s <server> -n <name> --start-ip-address <ip> --end-ip-address <ip>`,
  and `... delete` immediately afterwards. The database is `azureADOnlyAuthentication`, so the
  rule grants nobody access without a token for a database user — but leaving it behind on a
  public portfolio project is exactly the kind of thing to avoid. Cache the corpus on the first
  read and close the hole rather than keeping it open across an afternoon of iteration.
- The API's `Api__AllowAnonymousReads` is `False` in the deployed container app, and there is no
  bulk endpoint for posting concepts, so the API is not a route to the corpus.

---

## 3. What deploying this involved

Done, in this order. Recorded because step 2 surfaced a defect.

1. **Push.** `.github/workflows/deploy.yml` is in the workflow's own `paths:` filter, so the
   concurrency change deployed itself. The group is read from each run's own copy of the file,
   so it takes effect from that run onward and cannot rescue anything already queued.
2. **Re-enrich the corpus.** `EnrichedPosting.CurrentVersion` went to 4, marking every stored
   posting stale. 25 blobs, 0 failures. The floor never depended on this — it keys on the
   concept, not on the assertion's source, and was measured against the corpus before the
   reprocess — so this was cleanup rather than a prerequisite.

   Verified through `GET /api/v1/postings/facets`: `skill.agile` fell from 1,388 assertions to
   625. The 749 `Taxonomy` ones are gone, because the description matcher no longer produces
   them; the 554 `Model` readings and 85 `Board` tags survived, which is 1.3 doing exactly what
   it should — enrichment clears what enrichment writes. The floor is keyed on the concept, so
   the survivors still cannot carry a match alone.
3. **`seed-concepts` was not required.** `ConceptSeeder` projects labels, relations and the
   closure; `tagOnly` is not among them, so the SQL projection had not drifted. It is idempotent,
   and `deploy.yml` runs it beside `migrate` on a dispatch anyway.
4. **No re-sweep.** `MatchResult.CurrentVersion` went to 5, so every stored match is stale, but
   there are **no profiles** — `GET /api/v1/matches` returns 404 until one is created.

Verify the deployed image is the commit you expect:

```bash
az containerapp list --query "[0].properties.template.containers[0].image" -o tsv
```

A token for the API, which is how the facet check above was done without touching SQL:

```bash
CLIENT=$(az containerapp show -n <app> -g <rg> \
  --query "properties.template.containers[0].env[?name=='AzureAd__ClientId'].value | [0]" -o tsv)
az account get-access-token --resource "api://$CLIENT" --query accessToken -o tsv
```

---

## 4. Open work

### 4.1 A widely-held skill on a role from another field still scores 100

The honest residue of §1.1, and the clearest remaining ranking defect.

| posting | score | evidence |
| --- | --- | --- |
| Yardi Residential Implementation Consultant | 100 | `skill.sql` |
| Risk Strategy Program Manager | 92 | `skill.sql` |
| NetSuite Developer/Technical Consultant | 95 | `skill.rest` |
| Product Analyst | 94 | `skill.csharp`, `skill.sql`, `skill.python` |

These are correct readings of real requirements, and `skill.sql` genuinely discriminates — most
postings naming it do mean an engineering role — so the floor correctly leaves it alone. **No
rule over the concept axes separates these from a real match**, which is now measured three
ways rather than argued: not by count, not by coverage, and not by how specific the concept is
(the vocabulary is two levels deep, so `skill.sql` and `skill.csharp` are structurally
identical — neither is broader than the other).

What separates them is what the *role* is, which is a judgement rather than an arithmetic fact.
`ICandidacyAssessor` is the half of the design that makes judgements, and it already reads the
advert. Two directions worth considering, in order:

- The sweep spends its model budget on the highest-scoring unassessed pairs. It is currently
  spending it on these. That is the defect doing damage, and also the mechanism that could fix
  it — a verdict on Yardi is exactly the thing that would sink it.
- Nothing in ranking reads `Verdict`. A posting the model has already called a poor match still
  sits at rank 2. Surfacing that in `ListAsync`'s ordering is cheap and needs no new signal.

Take a top-60 snapshot before and diff it after, the way this round was done.

### 4.2 `RoleFamily` looks like the answer to 4.1 and is not — measured

`EnrichedPosting` carries a `RoleFamily`, classified from the title, and `MatchScorer` never
reads it — it is not even on `PostingFacts`. Since the residue in 4.1 is "this is a job from
another field", reaching for it is the obvious next move. It was checked first, because the
classifier is pure and runs on a title alone:

| posting | `RoleFamily` |
| --- | --- |
| Yardi Residential Implementation Consultant | `Unknown` |
| Risk Strategy Program Manager | `Unknown` |
| Transformation Manager | `Unknown` — not `Management` |
| `.NET Developer @ Noir` | `Unknown` — a core backend role |
| Senior Software Engineer - C# | `Backend` |

`Unknown` is 38.6% of the corpus and holds the residue and the best matches alike, so weighting
or filtering on it removes good with bad — 1.4's ledger for a third time. **Do not use it for
matching without fixing the classifier first**, and fixing it is a separate piece of work with
its own measurement.

There is a real, small finding in the table though: `RoleFamilyClassifier` calls `.NET
Developer` `Unknown`. It matches on title words and has no rule for the language-plus-role
shape that names half the corpus. That is worth fixing on its own merits — the API exposes
`roleFamily` as a browse filter, so the miss is user-visible today, quite apart from matching.

### 4.3 `limit` is not a bound on the reprocess endpoint, and it 504s

Found by driving the re-enrichment in §3, and it is the same gateway timeout 1.5 was written to
prevent. `POST /api/reprocess` returned **504 for `limit=50` and again for `limit=25`**.

The bound logic is correct and the reasoning behind it is correct — the continuation token is
only accurate at a page boundary, so that is the only place the budget can be applied. The gap
is that **`Budget` is 150s and a single page can take longer than the whole budget**. Measured
across the run: pages of 5 blobs took 4s, 11s, 12s, 47s and **151s**. Once the check at the end
of a page passes at, say, 149s, the call is committed to a whole further page with no way to
interrupt, and the gateway gives up at ~240s.

Two consequences, and the second is worse:

- `limit` above one page is not honoured. Only `PageSize` really bounds a call.
- **A 504 returns no continuation token**, so the caller loses its place and restarts from the
  beginning of the listing. That is survivable only because the writes are idempotent — the
  same property that saved the batch collector, relied on twice now.

Driving it one page per call (`limit: 5`) makes the gateway irrelevant and is what completed the
run: 25 blobs, 0 failures, 5 calls. That is the workaround, not the fix. The fix is for the
budget to bound what a page may start rather than only what follows one — check the remaining
budget *before* entering a page and stop if what is left cannot plausibly cover it, or shrink
`PageSize` toward 1 as the budget runs down. Either way the token stays accurate, because both
still act at a boundary.

Worth pairing with the test below, since this is precisely the untested bound.

### 4.4 The reprocess endpoint has no test

Unchanged from the previous handoff, and 4.3 is the argument for it. There is no ingestion test
project and nothing in the suite fakes a `BlobContainerClient`, so standing one up is a larger
change than the fix was. What is untested is a loop bound — the category that has caused real
incidents here more than once, including the gateway timeout that bounded collection in the
first place, and now this one.

### 4.5 Extraction coverage

The previous handoff recorded a graded share of 0.490 and called this the highest-leverage work
available. Measured directly this round, **92.6% of the corpus (3,775 of 4,078 postings) carries
at least one model assertion** — so 0.490 is the share of *assertions* that are model-sourced,
not the share of postings the model has read. The two are easy to confuse and they justify very
different work.

What is left is thinner than it looked: 168 postings carry no concepts at all, and 303 have
never been read by the model. The unresolved-mention log remains where the next vocabulary fix
comes from — it is what surfaced `containers`, and `agile` came from reading the ranking rather
than the log, which suggests the log is not the only place worth looking.

---

## 5. Environment notes

Carried forward; all still true.

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
- **Blob listing needs a data-plane role.** Subscription Owner is not enough; that needs
  `Storage Blob Data Reader` or better. `GET /api/v1/runs` is a usable proxy for what is in the
  container — one row per ingested blob.

Resource names are deliberately absent from this file — the repo is public. Discover them with
`az containerapp list`, `az functionapp list` and `az sql server list`, and see `CLAUDE.md` for
the placeholder convention used in commands.
