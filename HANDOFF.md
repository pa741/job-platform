# Handoff

State of the matching work as of 2026-08-27, for whoever picks this up next.

Everything described here is merged, deployed and verified against the live corpus. `main`
is at `edeb0fd`, the container app runs the image tagged with that same commit, and the
working tree is clean.

Conventions, architecture and the standing rules live in [`CLAUDE.md`](CLAUDE.md). This file
is only the delta and the open work.

---

## 1. What changed

Five fixes, all in the matching and enrichment path. Four of the five were found by running
against the real corpus rather than by review, which is the pattern worth carrying forward:
the test suite was green for every one of them.

### 1.1 `containers` is ambiguous, not an alias (`concepts.json` v4)

A job advert reading *"moving shopping containers up to 15KG"* resolved to the
Containerisation concept. The candidate held Kubernetes, Kubernetes implies containerisation,
so the only axis that answered scored full marks and a supermarket delivery driver role ranked
sixth in the whole corpus at 94/100.

The word now sits in `ambiguous` rather than `aliases`, so it records an unresolved mention
instead of an assertion. Two tests in `ConceptGraphTests` pin this: one carries that sentence
verbatim, the other checks a genuine containerisation advert still resolves, so the fix cannot
quietly cost us the postings that do mean it.

### 1.2 A vocabulary change now reaches stored postings

`concepts.json` carries its own version, and **nothing reads it** when deciding whether a
stored posting is stale. `JobPostingRepository` compares `EnrichedPosting.CurrentVersion`
against `JobPostings.EnrichmentVersion`. A vocabulary edit that leaves that constant alone is
an edit no existing row will ever pick up — it would have applied only to postings scraped for
the first time after the deploy.

`EnrichedPosting.CurrentVersion` is now 3, and the coupling is written down in `CLAUDE.md`
beside the `seed-concepts` rule, because it is invisible from either file alone.

### 1.3 Each producer clears only its own rows (the important one)

Bumping the enrichment version armed a trap that had existed for as long as extraction had.

The staleness rebuild called `ClearDerivedRowsAsync`, which deleted **all** of a posting's
concepts and mentions — including the ones the model wrote. Those do not come back. The
re-extraction the rebuild queues is keyed on a hash of the description, so a posting marked
stale by a *vocabulary* change has the same hash as before, converges on the
`PostingExtractions` row already in the table, and skips:

> corpus marked stale → every model assertion deleted → re-extraction skips as already-done →
> graded share falls toward zero, audit table says the work was done, nothing logged

Enrichment now clears what enrichment writes and leaves `AssertionSource.Model` alone. This
mirrors `PostingExtractionWriter`, which already deleted only its own rows before rewriting
them — and `CandidateProfileRepository`, which had the correct pattern on the other half of
the same schema all along.

Preserving the model's mentions re-opens the `(PostingId, SurfaceForm)` collision from the
opposite side, so surviving forms are loaded set-based with the other preloads and the rebuild
yields those keys to the model, whose claim is the more specific one.

Two regression tests in `JobPostingRepositoryTests`. Both fail against the old clear, and the
collision test also fails against the new clear without the yield — so it pins the collision,
not merely the end state.

### 1.4 The evidence floor: shipped, then withdrawn

**This one was wrong and was reverted. Do not reintroduce it.**

The first attempt required three string-matched (`Taxonomy`) concepts before they could carry
a score alone. Measured against the corpus it removed one bad match and four good ones:

| posting | evidence | verdict |
| --- | --- | --- |
| Home Delivery Driver | 1 × Containerisation | correctly removed |
| .NET Developer | 1 × .NET | wrongly removed |
| Software Engineer (AutoRek / SQL) | 1 × SQL | wrongly removed |
| Software Engineer — C# & .NET | 2 × .NET, C# | wrongly removed |
| Software Developer C# .Net | 2 × .NET, C# | wrongly removed |

`.NET Developer` and `Home Delivery Driver` both rest on exactly one string match. No
threshold separates them — counting was never the right axis. What separates them is the
vocabulary (1.1), which removes the bad assertion outright and lets the delivery advert fail
the **version 2** floor on its own, with no counting involved.

The version 2 floor stays: a posting that answers neither concept axis scores zero rather than
inheriting a perfect score from location alone. That one was measured too, against 44 of 60
top matches having no skills axis at all.

`MatchResult.CurrentVersion` is 4, and its remarks carry this history so the idea is not
retried from scratch.

### 1.5 Bounded, resumable admin endpoints

`MatchSweepFunction` splits its assessment ceiling: 40 for the nightly timer, 10 per HTTP
request. Scoring is deliberately left unbounded — it is arithmetic over rows already in
memory, and stopping half way would rank a profile against an arbitrary subset, which is worse
than not ranking it.

`ReprocessBlobFunction` was an unbounded loop over the whole landing container inside one HTTP
request, with a default prefix of `jobs/`. It is now bounded by blob count and wall clock, and
resumable through the blob listing's own continuation token, so no server state is needed.
Callers keep calling until the response reports `done`.

One subtlety, learned the hard way: the continuation token is only accurate at a page
boundary, so that is the only place the bounds can be applied. The first version sized the
page to the caller's limit, which put exactly one boundary in a call and placed it *after* all
the work — the budget could not interrupt anything, and a 50-blob call ran straight past the
gateway and returned a Server Error page. Pages are now a fixed small size independent of the
limit.

---

## 2. Verified production state

Measured after the corpus was re-enriched (21 blobs, 0 failures) and the sweep re-run.

| | value |
| --- | --- |
| Postings scored per sweep | 3,392 |
| Total assertions | 50,522 |
| Graded share (model-read) | 0.490 |
| Board / Taxonomy / Model assertions | 3,418 / 22,323 / 24,673 |

The re-enrichment removed 53 Taxonomy assertions across 8 postings and **zero** model
assertions, which is 1.3 doing exactly what it should. The delivery-driver posting now holds
no assertions at all and is absent from the rankings; the four legitimate C#/.NET/SQL roles
are back at ranks 6–9.

A validation profile used during this work has been deleted (`DELETE /api/v1/profile`,
confirmed 404 on re-read). No profiles currently exist, so `GET /api/v1/matches` returns 404
until one is created.

---

## 3. Open work

Ordered by how much it matters.

### 3.1 Thin but genuine evidence still scores 100

The clearest remaining ranking defect. It is **not** the bug fixed in 1.1 — the assertions here
are correct readings.

| posting | score | evidence |
| --- | --- | --- |
| Yardi Residential Implementation Consultant | 100 | 3 × SQL (Model + Board + Taxonomy) |
| Risk Strategy Program Manager | 95 | 2 × SQL |
| Space Data Engineer | 100 | 1 × Board: Data Engineering |

The model genuinely read "SQL" in the Yardi advert and it is genuinely there. The problem is
that a posting whose only stated requirement is one widely-held skill scores 100, because the
single axis that answered answered perfectly. Any SQL-only posting is a perfect match for
anyone who knows SQL.

`MatchResult.Coverage` already exists for exactly this — it distinguishes "scored badly" from
"could not be scored" — and is currently computed and reported but **not used in ranking**.
Damping the score by coverage, or ranking on a coverage-aware key, is the obvious direction.

Deliberately left undone. Two scoring rules have now been changed on judgement alone and one
of them was wrong (1.4); this one deserves a decision and a measurement against the corpus
before it ships. Take a top-60 snapshot first and diff it after, the way 1.4 was caught.

### 3.2 `deploy.yml` has no `concurrency:` group

An hour-old queued run for a superseded commit can still deploy over a newer one. This nearly
happened during a GitHub Actions outage: a stale Deploy for the previous commit sat queued for
hours while a newer commit deployed, and it would have silently rolled the container back.

With a concurrency group, a newly queued run cancels the previously pending one. Note it is
read from each run's own copy of the workflow file, so adding it does not retroactively rescue
already-queued runs.

The image tag is the full commit SHA, which makes a rollback detectable:

```bash
az containerapp list --query "[0].properties.template.containers[0].image" -o tsv
```

### 3.3 The reprocess endpoint has no test

There is no ingestion test project and nothing in the suite fakes a `BlobContainerClient`, so
standing one up is a larger change than the fix was. What is untested is a loop bound — the
category that has caused real incidents here more than once, including the gateway timeout in
1.5.

### 3.4 Extraction coverage is about half the corpus

The graded share is 0.490. Everything above degrades gracefully as that rises, because a model
assertion is worth more than any number of string matches: it is a deliberate reading, and it
is the only source that can state a requirement is *essential*. Raising coverage is the
highest-leverage work available on ranking quality.

The unresolved-mention log is where the next vocabulary fix comes from — the most frequent
ambiguous and unknown surface forms are, by construction, the concepts worth adding next. That
is what surfaced `containers`.

---

## 4. Environment notes

Things that cost time this session and are not obvious.

- **`jq` is not installed.** `gh --jq` works (built in); a standalone `jq` in a pipe silently
  produces nothing. A background monitor built on it emits no events, and silence is
  indistinguishable from "still running". Use `python -c` for JSON in shell loops.
- **Two shells, two syntaxes.** PowerShell is primary; a Bash tool is also available. Bash
  line continuations (`\`) pasted into PowerShell fail with
  `Falta una expresión después del operador unario '--'`. Use backticks or single lines there.
- **MSYS mangles Azure resource paths.** `az ... --scope /subscriptions/...` fails with
  `MissingSubscription` under Git Bash. Prefix with `MSYS_NO_PATHCONV=1`.
- **Blob listing needs a data-plane role.** Subscription Owner is not enough to list the
  landing container; that needs `Storage Blob Data Reader` or better. The functions hold it
  via managed identity, so the reprocess endpoint works even when `az storage blob list` does
  not. `GET /api/v1/runs` is a usable proxy for what is in the container — one row per
  ingested blob.
- **Heredocs and Python don't mix well here.** Multi-line edit scripts containing triple-quoted
  strings break Bash heredoc quoting. Write the script to a file first.

Resource names are deliberately absent from this file — the repo is public. Discover them with
`az containerapp list`, `az functionapp list`, and see `CLAUDE.md` for the placeholder
convention used in commands.
