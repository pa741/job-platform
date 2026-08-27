# Handoff

State of the matching work as of 2026-08-27, for whoever picks this up next.

Conventions, architecture and the standing rules live in [`CLAUDE.md`](CLAUDE.md). This file
is only the delta and the open work. The previous round of fixes — the `containers`
vocabulary bug, the enrichment version coupling, each producer clearing only its own rows, the
withdrawn evidence-count rule, and the bounded admin endpoints — is described in `CLAUDE.md`
and in the `CurrentVersion` remarks on `EnrichedPosting` and `MatchResult`.

**Deployed and verified.** `main` is at the commit the container app runs, the corpus has been
re-enriched, and the working tree is clean. No sweep has run because no profiles exist. §3 is
what deploying §1.1 involved — and it is where §1.3 came from, because driving the
re-enrichment is what exposed the bound that could not act.

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

`MatchResult.CurrentVersion` is 5, and `EnrichedPosting.CurrentVersion` went to 4 here (5 by
the end of §1.4). The remarks on both carry this history, including the two rejected rules, so
neither is retried from scratch.

### 1.2 `deploy.yml` has a concurrency group

Previously §3.2. Runs were concurrent and landed in whatever order the runners freed up; a
stale queued run could deploy over a newer commit, which nearly happened during an Actions
outage. The group serialises them and cancels the pending run when a newer one queues behind it.

`cancel-in-progress` is **false** deliberately: a run already executing may be part way through
a Bicep deployment or `dbadmin migrate`, and killing the runner there trades a recoverable delay
for a half-applied change. Waiting costs at worst a few minutes of the older image being live,
and the newer run still lands last.

### 1.3 The reprocess endpoint's bound can act, and is tested

Found by driving §3's re-enrichment: `POST /api/reprocess` returned **504 for `limit=50` and
again for `limit=25`** — the same gateway timeout 1.5 was written to prevent.

The reasoning behind the original bound was right. The continuation token is only accurate at
a page boundary, so that is the only place it can be applied. What was missed is that **a page
can cost more than the whole budget**: measured across the run, pages of five blobs took 4s,
11s, 12s, 47s and **151s** against a 150s budget. A check passing at 149s committed the call to
a whole further page with nothing able to interrupt it, and the gateway gave up at ~240s.
Worse, a 504 carries no token, so the caller lost its place and restarted the listing —
survivable only because the writes are idempotent, the second time that property has covered
for a bound that could not act.

The fix is not a smaller budget; no budget survives a page that costs more than all of it. The
loop is now `BoundedWalk`, which stops **between items** and hands back the token for the start
of the page it was in. The resume point is still a real boundary, so nothing is skipped, and
the cost is redoing that page's finished items — free here, because a blob whose content has
not changed converges in about a second. Overshoot past the budget falls from one page to one
item.

It will not stop before a page has completed, deliberately: bailing out of the first page would
hand back the token the call arrived with, and the next call — with a fresh clock — would stop
in the same place forever.

`BoundedWalk` is generic over the item and takes a clock, so **it needs no Azure types to
test**, which is what unblocked the test that had been open since the endpoint was written.
That is the lesson worth carrying: the previous handoff called this untestable because faking a
`BlobContainerClient` is hard, and the answer was to stop trying — the bound was never about
blobs. `tests/JobPlatform.Ingestion.Tests` is new and holds seven tests, each a shape that
actually happened, including the residual limit: a single item slower than the whole budget
still overshoots by its own duration, and the margin to the gateway's ~230s absorbs it. The
production-shape test was checked against the pre-fix code and fails there.

### 1.4 A rule list written in a spelling the tokeniser destroys

`RoleFamilyClassifier` called `.NET Developer` `Unknown`. The previous revision of this file
guessed it had "no rule for the language-plus-role shape". That was wrong, and the real cause
is worth more than the fix: the rule **was** there. `.net`, `node.js` and `ui/ux` had been in
the lists since the file was written, and `TitleTokenizer` splits on `.` and `/`, so by the
time a rule saw the text those were already `net`, `node` + `js` and `ui` + `ux`. Three dead
entries that read exactly like working ones.

`TitleTokenizer.DottedNames` now folds the three names whose spelling contains a separator,
and the rules name the folded form. `ui/ux` was deleted outright — the bare `ux` beside it
already covered every title it aimed at.

Measured over 2,709 distinct corpus titles: **13 moved out of `Unknown`, all of them
unambiguously .NET backend roles, and nothing moved between families or into `Unknown`.**

Two intermediate attempts are worth not repeating. Making `.` a word character — the obvious
fix, and what `ConceptGraph.NameChar` does for the vocabulary — fixed .NET and broke
"Sr.Product Manager" and "React.js Developer", because any `Word.Word` spelling then becomes
one token. Thirteen fixes for two regressions is the shape of a change to reject. Folding
without a leading space then broke `C#.NET` and `VB.NET`, which are written glued to what
precedes them. Both regressions were caught by diffing the whole corpus rather than by the
examples in hand, which is the third time that has paid this session.

Ten tests, and the pairing matters: the ones that assert `.NET Developer` is Backend fail
without the fold, and the ones that assert `Sr.Product Manager` is still Product fail under
the word-character version. **Asserting the classifier alone would not have caught this** —
a dead entry and a working one look identical from there.

`EnrichedPosting.CurrentVersion` is 5: the classifier produces a different answer for the
same input, so stored rows are stale.

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

Done, in this order. Recorded because step 2 surfaced the defect §1.3 fixes.

1. **Push.** `.github/workflows/deploy.yml` is in the workflow's own `paths:` filter, so the
   concurrency change deployed itself. The group is read from each run's own copy of the file,
   so it takes effect from that run onward and cannot rescue anything already queued.
2. **Re-enrich the corpus.** `EnrichedPosting.CurrentVersion` went to 4, marking every stored
   posting stale. 25 blobs, 0 failures. The floor never depended on this — it keys on the
   concept, not on the assertion's source, and was measured against the corpus before the
   reprocess — so this was cleanup rather than a prerequisite.

   Verified through `GET /api/v1/postings/facets`: `skill.agile` fell from 1,388 assertions to
   625. The 749 `Taxonomy` ones are gone, because the description matcher no longer produces
   them; the 554 `Model` readings and 85 `Board` tags survived, which is the *previous* round's
   "each producer clears only its own rows" doing exactly what it should — enrichment clears what
   enrichment writes. The floor is keyed on the concept, so the survivors still cannot carry a
   match alone.
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

### 4.2 Extraction coverage

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
