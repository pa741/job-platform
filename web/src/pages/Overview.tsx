import type { JobPlatformApi } from '../api/client';
import { NewPostingsTrend, PartToWhole, RankedBar, SiteSplit } from '../charts/Charts';
import { Card, ErrorNote, Meter, StatTile } from '../components/Primitives';
import { useMetricsFeed } from '../feed/useMetricsFeed';

export function Overview({ api, searchTerm }: { api: JobPlatformApi; searchTerm: string | undefined }) {
  const { snapshot, error, loading, kind, refresh } = useMetricsFeed(api, searchTerm);

  if (error && !snapshot) return <ErrorNote error={error} />;
  if (!snapshot) return <div className="empty">{loading ? 'Loading metrics…' : 'No metrics yet.'}</div>;

  const { summary, rollups, health } = snapshot;
  const enrichment = summary.enrichment;

  // Everything on this axis is present for every posting, including Unknown - dropping it
  // would leave every share reading against a denominator the reader cannot see.
  const classified = Object.entries(enrichment.bySeniority)
    .filter(([level]) => level !== 'Unknown')
    .map(([name, count]) => ({ name, count }));

  return (
    <div className="grid">
      {/* Every number below comes from Cosmos, never from SQL. That is what lets this page
          poll without spending the serverless database's monthly grant. */}
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
        <StatTile label="Seen in last run" value={summary.postingsInLastRun.toLocaleString()} />
        <Meter label="Remote" ratio={summary.remoteShare} caption="of postings that said" />
        {/* Two different numbers, both true, and the gap between them is the point. The
            first is what the scraper delivered; the second is what is known once the
            description has been read. The gap was tenfold before the scraper learned to
            extract salaries outside the US, and is a few points now that it does - which
            makes the pair a reading on the scraper as much as on the market. */}
        <Meter
          label="Salary in the columns"
          ratio={summary.salaryCoverage}
          caption="as the boards delivered it"
        />
        <Meter
          label="Salary known"
          ratio={enrichment.salaryCoverage}
          caption="after reading descriptions"
        />
        <StatTile
          label="Median salary"
          value={enrichment.medianAnnualSalary != null
            ? `£${Math.round(enrichment.medianAnnualSalary / 1000)}k`
            : '—'}
          hint={enrichment.salaryFromTextShare > 0
            ? `${Math.round(enrichment.salaryFromTextShare * 100)}% read from prose`
            : undefined}
        />
      </div>

      <div className="grid halves">
        <Card
          title="New postings per scrape day"
          subtitle="Postings whose first sighting was that day"
          actions={
            <button className="btn" onClick={refresh} title={`Feed: ${kind}`}>
              Refresh
            </button>
          }
        >
          <NewPostingsTrend rollups={rollups} />
        </Card>

        <Card title="Where postings came from" subtitle="Boards producing results in the last run">
          <SiteSplit bySite={summary.bySite} />
        </Card>
      </div>

      <div className="grid halves">
        <Card
          title="Skills in demand"
          subtitle="Concepts named by the most postings in the last run"
        >
          <RankedBar items={enrichment.topConcepts} valueLabel="Postings" />
        </Card>

        <Card
          title="Areas in demand"
          subtitle="The same demand rolled up — the shape under the scatter"
        >
          {/* A different question from the list beside it, not a summary of it. Individual
              tools scatter across a dozen ways of saying the same thing; the rollup is what
              shows whether the market wants backend or data people. This is the one number
              on the page that could not exist without the concept graph. */}
          <RankedBar items={enrichment.topDomains} valueLabel="Postings" />
        </Card>
      </div>

      <div className="grid halves">
        <Card
          title="Seniority mix"
          subtitle={`${classified.reduce((n, d) => n + d.count, 0)} of ${summary.postingsInLastRun} postings state a level`}
        >
          <RankedBar items={classified} valueLabel="Postings" />
        </Card>

        <Card
          title="How the work happens"
          subtitle="The three-way split a remote flag cannot express"
        >
          <PartToWhole
            data={enrichment.byWorkArrangement}
            emptyMessage="No postings in the last run."
          />
        </Card>
      </div>

      <div className="grid halves">
        <Card title="Who is hiring" subtitle="Companies with the most postings in the last run">
          <RankedBar items={summary.topCompanies} valueLabel="Postings" />
        </Card>

        <Card
          title="Job titles"
          subtitle="Normalised title keywords — what roles are called, not what they need"
        >
          <RankedBar items={summary.titleKeywords} valueLabel="Titles" />
        </Card>
      </div>

      <ScraperHealthCard health={health} />

      {enrichment.unresolvedMentions > 0 && (
        <Card
          title="What the vocabulary could not place"
          subtitle={`${enrichment.unresolvedMentions.toLocaleString()} mentions in the last run`}
        >
          {/* Surfaced rather than hidden, and listed rather than counted. The number alone
              says how big the blind spot is; the forms say what is in it, which is the only
              part anyone can act on. The reason column matters because the two halves need
              opposite responses - an ambiguous form needs context, not an entry. */}
          <p className="muted" style={{ marginTop: 0, fontSize: 13 }}>
            Surface forms the resolver saw and declined to guess at. They are recorded rather
            than discarded, which is the only reason this list can exist — and the frequent
            ones are what the vocabulary should learn next.
          </p>

          <div className="scroll-x">
            <table>
              <thead>
                <tr>
                  <th>Form</th><th>Needs</th><th className="num">Mentions</th>
                </tr>
              </thead>
              <tbody>
                {enrichment.topUnresolved.map((entry) => (
                  <tr key={`${entry.form}:${entry.reason}`}>
                    <td><code>{entry.form}</code></td>
                    <td>
                      {entry.reason === 'Ambiguous' ? (
                        <span title="The vocabulary knows this word and distrusts it — it needs surrounding context to resolve, not a new entry">
                          context
                        </span>
                      ) : (
                        <span title="The vocabulary has no concept for this — it is a genuine gap">
                          vocabulary
                        </span>
                      )}
                    </td>
                    <td className="num">{entry.count}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </Card>
      )}

      <div className="muted" style={{ fontSize: 12 }}>
        Updated {snapshot.receivedAt.toLocaleTimeString()} · feed: {kind}
        {summary.lastScrapedAtUtc && ` · scraped ${new Date(summary.lastScrapedAtUtc).toLocaleString()}`}
      </div>
    </div>
  );
}

/**
 * The scraper's own health.
 *
 * Given prominence deliberately: a column silently falling to 0% is the earliest signal a
 * job board changed its markup and the scraper degraded without failing. Nothing else in
 * the pipeline raises an error when that happens, so if it is not on this page it is
 * nowhere.
 */
function ScraperHealthCard({ health }: { health: import('../api/types').ScraperHealth }) {
  const degraded = health.status === 'degraded';
  const regressed = health.emptyColumns.filter((c) => c.lastFilledUtc !== null);
  const neverShipped = health.emptyColumns.filter((c) => c.lastFilledUtc === null);

  return (
    <Card
      title="Scraper health"
      subtitle={`${health.rowsInLastRun} rows in the last run, ${health.invalidInLastRun} unparseable`}
      actions={
        // Icon plus label, never colour alone: the status palette is not distinguishable
        // by hue for every reader, and two of its four steps sit below 3:1 on light.
        <span className={`pill ${degraded ? 'critical' : 'good'}`}>
          {degraded ? '▲' : '●'} {health.status}
        </span>
      }
    >
      {health.emptyColumns.length === 0 && (
        <div className="empty">Every column the parser tracks was populated in at least one row.</div>
      )}

      {/* Two faults, one symptom. A column that was arriving and stopped is a board changing
          its markup - the thing this card exists to catch. A column that has never arrived is
          one the scraper does not emit yet, and alerting on it is how the whole card becomes
          something you scroll past. They are separated here rather than counted together. */}
      {regressed.length > 0 && (
        <>
          <p className="muted" style={{ marginTop: 0, fontSize: 13 }}>
            {regressed.length} column{regressed.length === 1 ? '' : 's'} that {regressed.length === 1 ? 'was' : 'were'}{' '}
            arriving stopped. That usually means a board changed its markup and the scraper
            degraded quietly.
          </p>
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
            {regressed.map((column) => (
              <span
                key={column.field}
                className="pill critical"
                title={`Last filled ${new Date(column.lastFilledUtc!).toLocaleDateString()}, at ${Math.round((column.lastFillRate ?? 0) * 100)}%`}
              >
                ▲ {column.field}
              </span>
            ))}
          </div>
        </>
      )}

      {neverShipped.length > 0 && (
        <div style={{ marginTop: regressed.length > 0 ? 14 : 0 }}>
          <p className="muted" style={{ marginTop: 0, fontSize: 13 }}>
            {neverShipped.length} tracked column{neverShipped.length === 1 ? ' has' : 's have'} never
            been populated. Nothing broke: the scraper does not emit {neverShipped.length === 1 ? 'it' : 'them'} yet.
          </p>
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
            {neverShipped.map((column) => (
              <span key={column.field} className="pill">{column.field}</span>
            ))}
          </div>
        </div>
      )}

      {health.sparseColumns.length > 0 && (
        <div style={{ marginTop: 14 }}>
          <div className="sub" style={{ marginBottom: 6 }}>
            Sparse — populated, but in under a quarter of rows
          </div>
          <div className="scroll-x">
            <table>
              <thead>
                <tr><th>Column</th><th className="num">Fill rate</th></tr>
              </thead>
              <tbody>
                {health.sparseColumns.map((field) => (
                  <tr key={field.field}>
                    <td>{field.field}</td>
                    <td className="num">{Math.round(field.fillRate * 1000) / 10}%</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}
    </Card>
  );
}
