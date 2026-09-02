# UI directions

Three standalone mocks of the dashboard, each a different design direction over the same
invented-but-realistic dataset, so the comparison is about design rather than content. Open
any of them straight in a browser - no build, no API. GSAP comes from a CDN.

| File | Direction | Interaction model |
| --- | --- | --- |
| `telemetry.html` | Dark-first instrument panel. IBM Plex, hairline panels, cool cyan signal. | Fixed rail, live pipeline strip, command palette, in-place match detail. |
| `briefing.html` | **Chosen direction.** Light editorial briefing. Newsreader over Karla, ink blue and oxide. | Shortlist-first landing page, two-tier nav (section pill, then page tabs), hamburger sheet under 860px, hash routing with deep links, facets, insight drawer, walkable concept graph, waking-database states. |
| `deck.html` | Bold triage deck. Bricolage Grotesque, ultramarine and coral. | The shortlist is the home screen: one role per card, drag or arrow keys to decide. |

`briefing.html` covers all eight pages; the other two cover three screens each and stub the
rest. All three honour `prefers-reduced-motion` and carry a full palette for light and dark.

Routing in `briefing.html` is hash-based (`#/postings/41209`) because the mock is opened from
a file and from inside an artifact host. The deployed app can use real paths unchanged:
`staticwebapp.config.json` already rewrites navigation and 404s to `/index.html`, which is all
the History API needs. Only the adapter changes; the URL scheme does not.

The footer carries a demo control that pauses the database, so the waking state is
inspectable. `sqlSku` defaults to `free-serverless`, which auto-pauses when idle, so that
state is the default for a fresh clone rather than an edge case.

Whichever direction wins gets rebuilt as React components under `../src`; these files are
the argument, not the implementation.
