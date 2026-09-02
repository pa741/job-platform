import { useCallback } from 'react';
import type { JobPlatformApi } from '../api/client';
import type { EmptyColumn, ScraperHealth, SkillGapItem } from '../api/types';
import { NewPostingsTrend } from '../charts/Charts';
import { Card, ErrorNote, Meter, RankList, SplitBar, StatTile } from '../components/Primitives';
import { useApiResource } from '../components/useApiResource';
import { WakingRegion } from '../components/WakingRegion';
import { useMetricsFeed } from '../feed/useMetricsFeed';
import type { PageId } from '../routing/route';

/**
 * The market, read against the reader.
 *
 * Deliberately not the landing page any more. Every figure below except the first card is
 * about the corpus, and a corpus statistic changes nothing about anybody's morning: Python
 * leads the demand list today and will lead it next year. The page is opened on purpose —
 * when the question is whether to widen a search, move a salary floor, or learn something.
 *
 * Every number here comes from Cosmos, which is what lets the page poll without spending the
 * serverless database's monthly grant, and what lets it render while SQL is asleep. The one
 * exception is the skills gap, which is SQL and per-principal, and degrades on its own.
 */
export function Briefing({ api, searchTerm, go }: {
  api: JobPlatformApi; searchTerm: string | undefined; go: (page: PageId) => void;
}) {
  const { snapshot, error, loading, kind, refresh } = useMetricsFeed(api, searchTerm);

  if (error && !snapshot) return <ErrorNote error={error} />;
  if (!snapshot) return <div className="empty">{loading ? 'Loading metrics…' : 'No metrics yet.'}</div>;

  const { summary, rollups, health } = snapshot;
  const enrichment = summary.enrichment;

  // Everything on this axis is present for every posting, including Unknown - dropping it
  // would leave every share reading against a denominator the reader cannot see. "82% senior"
  // is a very different claim from "82% of the 18% we could classify".
  const seniority = Object.fromEntries(
    Object.entries(enrichment.bySeniority).filter(([level]) => level !== 'Unknown'),
  );

  return (
    <div className="stack">
      <p className="lede">
        <b>{summary.newInLastRun.toLocaleString()}</b> new postings in the last run
        {summary.newPostingsDelta != null && (
          <>, <b>{summary.newPostingsDelta > 0 ? '+' : ''}{summary.newPostingsDelta}</b> on the
          previous day with data</>
        )}.
      </p>

      <p className="lede-note">
        Everything below is about the corpus rather than about you — with one exception, which
        is why it is first. The counts are last-run figures; the facets on Postings count the
        whole corpus, and the two denominators are not interchangeable.
      </p>

      <SkillGap api={api} searchTerm={searchTerm} go={go} />

      <div className="grid kpi">
        <StatTile
          label="Distinct postings"
          value={summary.cumulativePostings.toLocaleString()}
          hint={`across ${summary.daysOfHistory} day${summary.daysOfHistory === 1 ? '' : 's'} of history`}
        />
        <StatTile
          label="New in last run"
          value={summary.newInLastRun.toLocaleString()}
          delta={summary.newPostingsDelta}
          hint={summary.newPostingsDelta == null ? 'no previous day to compare' : undefined}
        />
        <StatTile
          label="Seen in last run"
          value={summary.postingsInLastRun.toLocaleString()}
          hint={`${summary.invalidInLastRun} rows would not parse`}
        />
        <StatTile
          label="Median stated salary"
          value={enrichment.medianAnnualSalary != null
            ? `£${Math.round(enrichment.medianAnnualSalary / 1000)}k`
            : '—'}
          hint="of the postings that state one — see below"
        />
      </div>

      <div className="grid halves">
        <Card
          title="New postings per scrape day"
          subtitle="Postings whose first sighting was that day"
          actions={<button className="btn" onClick={refresh} title={`Feed: ${kind}`}>Refresh</button>}
        >
          <NewPostingsTrend rollups={rollups} />
        </Card>

        <Card title="Where postings came from" subtitle="Boards producing results in the last run">
          <SplitBar data={summary.bySite} emptyMessage="No postings in the last run." />
        </Card>
      </div>

      <Card
        title="What the salary figure is, and is not"
        subtitle="Read before the median above"
      >
        {/* Two true numbers, and the gap between them is the point. The first is what the
            scraper delivered; the second is what is known once the description has been read.
            The gap was tenfold before the scraper learned to extract salaries outside the US. */}
        <div className="grid kpi">
          <Meter label="Salary in the columns" ratio={summary.salaryCoverage} caption="as the boards delivered it" />
          <Meter label="Salary known" ratio={enrichment.salaryCoverage} caption="after reading descriptions" />
          <Meter label="Remote" ratio={summary.remoteShare} caption="of postings that stated a mode" />
        </div>

        <p className="note">
          The median is over the postings that state a salary at all, and those skew low —
          agencies and public sector publish bands, and the roles that do not publish are
          disproportionately the ones above a senior floor. Read it as a floor on the visible
          market, never as the market.
          {enrichment.salaryFromTextShare > 0 && (
            <> {Math.round(enrichment.salaryFromTextShare * 100)}% of what is known was read out
            of prose rather than a salary field, which is weaker evidence than a column.</>
          )}
        </p>
      </Card>

      <div className="grid halves">
        <Card title="Skills in demand" subtitle="Concepts named by the most postings in the last run">
          <RankList items={enrichment.topConcepts} unit="postings" />
        </Card>

        <Card
          title="Areas in demand"
          subtitle="The same demand rolled up — the shape under the scatter"
        >
          {/* A different question from the list beside it, not a summary of it. Individual
              tools scatter across a dozen ways of saying the same thing; the rollup is what
              shows whether the market wants backend or data people. This is the one number on
              the page that could not exist without the concept graph. */}
          <RankList items={enrichment.topDomains} unit="postings" />
        </Card>
      </div>

      <div className="grid halves">
        <Card title="How the work happens" subtitle="The three-way split a remote flag cannot express">
          <SplitBar data={enrichment.byWorkArrangement} emptyMessage="No postings in the last run." />
        </Card>

        <Card
          title="Seniority mix"
          subtitle="Of the postings that state a level at all"
        >
          <SplitBar data={seniority} emptyMessage="No posting stated a level." />
        </Card>
      </div>

      <div className="grid halves">
        <Card
          title="Who posts most"
          subtitle="Counted by postings published, which is not the same as hiring most"
        >
          <RankList items={summary.topCompanies} unit="postings" />
          <p className="note">
            The top of this list is consistently the outsourcers, who post continuously against
            a bench. Volume, not demand.
          </p>
        </Card>

        <Card
          title="What the roles are called"
          subtitle="Normalised title keywords — what the work is called, not what it needs"
        >
          <RankList items={summary.titleKeywords} unit="titles" />
        </Card>
      </div>

      <ScraperHealthCard health={health} />

      {enrichment.unresolvedMentions > 0 && (
        <Card
          title="What the vocabulary could not place"
          subtitle={`${enrichment.unresolvedMentions.toLocaleString()} mentions in the last run`}
        >
          {/* Surfaced rather than hidden, and listed rather than counted. The number alone says
              how big the blind spot is; the forms say what is in it, which is the only part
              anyone can act on. The reason matters because the two halves need opposite
              responses - an ambiguous form needs context, not an entry. */}
          <p className="note" style={{ marginTop: 0 }}>
            Surface forms the resolver saw and declined to guess at. They are recorded rather
            than discarded, which is the only reason this list can exist — and the frequent ones
            are what the vocabulary should learn next.
          </p>

          <div className="scroll-x">
            <table>
              <thead>
                <tr><th>Form</th><th>Needs</th><th className="num">Mentions</th></tr>
              </thead>
              <tbody>
                {enrichment.topUnresolved.map((entry) => (
                  <tr key={`${entry.form}:${entry.reason}`}>
                    <td><code>{entry.form}</code></td>
                    <td>
                      {entry.reason === 'Ambiguous'
                        ? <span title="The vocabulary knows this word and distrusts it — it needs surrounding context to resolve, not a new entry">context</span>
                        : <span title="The vocabulary has no concept for this — it is a genuine gap">vocabulary</span>}
                    </td>
                    <td className="num">{entry.count}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </Card>
      )}

      <p className="note">
        Updated {snapshot.receivedAt.toLocaleTimeString()} · feed: {kind}
        {summary.lastScrapedAtUtc && ` · scraped ${new Date(summary.lastScrapedAtUtc).toLocaleString()}`}
      </p>
    </div>
  );
}

/**
 * The join, run backwards.
 *
 * The only figure on this page that is about the reader, and the only one that changes what
 * they would do next. It exists because postings and profiles are extracted into the same
 * vocabulary, which makes this a set difference rather than a similarity score.
 *
 * It is the one SQL read on an otherwise Cosmos-backed page, and per-principal so it carries
 * no cache — hence its own request and its own degraded state rather than being folded into
 * the metrics feed.
 */
function SkillGap({ api, searchTerm, go }: {
  api: JobPlatformApi; searchTerm: string | undefined; go: (page: PageId) => void;
}) {
  const load = useCallback(() => api.skillGap(searchTerm), [api, searchTerm]);
  const gap = useApiResource(load);

  if (gap.state.status === 'waking') {
    return <WakingRegion what="Your skills gap" onRetry={gap.reload} />;
  }

  // A 404 here means no profile, which is not a failure worth an error box on a market page.
  if (gap.state.status === 'error') return null;
  if (gap.state.status === 'loading') return null;

  const items = gap.state.data.items;
  if (items.length === 0) return null;

  return (
    <Card
      title="What the market asks of you that you do not hold"
      subtitle={`Over your matches above ${gap.state.data.minScore}, not over the corpus`}
    >
      <p className="note" style={{ marginTop: 0 }}>
        A set difference over two tables of the same shape — the concepts postings ask for, less
        the concepts your profile holds. Ranked by how many of <em>your</em> matches name each
        one: the corpus list is led by things you already have, which is what makes it the least
        useful number on this page.
      </p>

      {items.map((item) => <GapRow key={item.concept} item={item} />)}

      <p className="note">
        Fixing one is editing your{' '}
        <button className="linkish" onClick={() => go('profile')}>profile</button>, and the
        change is scored on tonight&rsquo;s sweep.
      </p>
    </Card>
  );
}

function GapRow({ item }: { item: SkillGapItem }) {
  return (
    <div className="gap">
      <span className="gap-name">{item.label}</span>
      <span className="gap-counts">
        <b>{item.matchPostings}</b> of your matches · {item.corpusPostings.toLocaleString()} in the corpus
      </span>
      <p className="gap-why">
        {item.kind === 'Qualification' ? (
          <>
            A qualification rather than a skill: it cannot be picked up before an application
            closes, so this is a filter on what you can apply to rather than something to learn.
          </>
        ) : item.held ? (
          <>
            You hold <em>{item.heldLabel}</em>, which the graph records as <em>{item.relation}</em>
            {item.relation === 'Specialisation'
              ? ' — that satisfies it outright.'
              : ' — it earns partial credit and never full.'}
          </>
        ) : (
          <>Nothing in your profile touches it, by any relation in the graph. This is the gap
          with no partial credit behind it.</>
        )}
      </p>
    </div>
  );
}

/**
 * The scraper's own health.
 *
 * Given prominence deliberately: a column silently falling to 0% is the earliest signal a job
 * board changed its markup and the scraper degraded without failing. Nothing else in the
 * pipeline raises an error when that happens, so if it is not on this page it is nowhere.
 */
function ScraperHealthCard({ health }: { health: ScraperHealth }) {
  const regressed = health.emptyColumns.filter((c) => c.lastFilledUtc !== null);
  const neverShipped = health.emptyColumns.filter((c) => c.lastFilledUtc === null);
  const degraded = health.status === 'degraded';

  return (
    <Card
      title="Scraper health"
      subtitle={`${health.rowsInLastRun} rows in the last run, ${health.invalidInLastRun} unparseable`}
      actions={
        // Icon plus label, never colour alone: the status palette is not distinguishable by
        // hue for every reader, and two of its four steps sit below 3:1 on light.
        <span className={`stamp ${degraded ? 'warn' : 'good'}`}>
          {degraded ? '▲' : '●'} {health.status}
        </span>
      }
    >
      {health.emptyColumns.length === 0 && (
        <div className="empty">Every column the parser tracks was populated in at least one row.</div>
      )}

      {/* Two faults, one symptom, opposite responses. A column that was arriving and stopped
          is a board changing its markup. A column that has never arrived is one the scraper
          does not emit yet, and alerting on that is how the whole card becomes something you
          learn to scroll past. */}
      {regressed.length > 0 && (
        <>
          <p className="note" style={{ marginTop: 0 }}>
            {regressed.length} column{regressed.length === 1 ? '' : 's'} that{' '}
            {regressed.length === 1 ? 'was' : 'were'} arriving stopped. That usually means a
            board changed its markup and the scraper degraded quietly.
          </p>
          <div className="chips">
            {regressed.map((column) => <RegressedColumn key={column.field} column={column} />)}
          </div>
        </>
      )}

      {neverShipped.length > 0 && (
        <div style={{ marginTop: regressed.length > 0 ? 'var(--s4)' : 0 }}>
          <p className="note" style={{ marginTop: 0 }}>
            {neverShipped.length} tracked column{neverShipped.length === 1 ? ' has' : 's have'}{' '}
            never been populated. Nothing broke — the scraper does not emit{' '}
            {neverShipped.length === 1 ? 'it' : 'them'} yet.
          </p>
          <div className="chips">
            {neverShipped.map((column) => (
              <span key={column.field} className="stamp">{column.field}</span>
            ))}
          </div>
        </div>
      )}

      {health.sparseColumns.length > 0 && (
        <div style={{ marginTop: 'var(--s4)' }}>
          <h4 className="mini">Sparse — arriving, in under a quarter of rows</h4>
          <RankList
            items={health.sparseColumns.map((f) => ({
              name: f.field, count: Math.round(f.fillRate * 1000) / 10,
            }))}
            unit="% filled"
          />
        </div>
      )}
    </Card>
  );
}

function RegressedColumn({ column }: { column: EmptyColumn }) {
  const when = column.lastFilledUtc ? new Date(column.lastFilledUtc).toLocaleDateString() : '';
  const rate = column.lastFillRate != null ? `${Math.round(column.lastFillRate * 100)}%` : '';

  return (
    <span className="stamp warn" title={`Last filled ${when}, at ${rate}`}>
      ▲ {column.field} — last filled {when}
    </span>
  );
}
