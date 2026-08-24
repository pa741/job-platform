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
            first is what the boards filled in; the second is what is known once the
            description has been read. Showing only the first understates the market by
            about a factor of ten. */}
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
          {/* Surfaced rather than hidden. These are words the resolver saw and refused to
              guess at - a bare "Go" that might be the language or the verb, or a skill an
              employer typed that the vocabulary has never heard of. The number is only
              knowable because they are recorded instead of dropped, and it is the honest
              size of the blind spot rather than a defect count. */}
          <p className="muted" style={{ margin: 0, fontSize: 13 }}>
            Surface forms the resolver saw and declined to guess at — an ambiguous word like
            a bare <code>Go</code>, or a skill the vocabulary has not learned yet. They are
            recorded rather than discarded, which is the only reason this number exists;
            the most frequent of them are what the vocabulary should learn next.
          </p>
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
      {health.emptyColumns.length === 0 ? (
        <div className="empty">Every column the parser tracks was populated in at least one row.</div>
      ) : (
        <>
          <p className="muted" style={{ marginTop: 0, fontSize: 13 }}>
            {health.emptyColumns.length} column{health.emptyColumns.length === 1 ? '' : 's'} were empty
            in <strong>every</strong> row of the last run. A column that drops to zero usually means a
            board changed its markup and the scraper degraded quietly.
          </p>
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
            {health.emptyColumns.map((column) => (
              <span key={column} className="pill critical">▲ {column}</span>
            ))}
          </div>
        </>
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
