import { Bar, BarChart, CartesianGrid, Cell, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts';
import type { DailyRollup } from '../api/types';
import { useChartTokens } from './chartTheme';

interface TooltipRow { name: string; value: number | string }

/** Shared tooltip. Tokened ink, never the series colour, so text stays legible in both modes. */
function ChartTooltip({ active, label, rows }: { active?: boolean; label?: string; rows: TooltipRow[] }) {
  if (!active || rows.length === 0) return null;

  return (
    <div className="tooltip">
      {label && <div className="k" style={{ marginBottom: 4 }}>{label}</div>}
      {rows.map((row) => (
        <div key={row.name} style={{ display: 'flex', gap: 12, justifyContent: 'space-between' }}>
          <span className="k">{row.name}</span>
          <span className="v">{row.value}</span>
        </div>
      ))}
    </div>
  );
}

const axisTick = (fill: string) => ({ fill, fontSize: 11 });

/**
 * New postings per scrape day.
 *
 * One series, so no legend - the card title names it. Deliberately not plotted against
 * cumulative postings on a second axis: two y-scales on one chart is the single most
 * misleading thing a dashboard can do, and cumulative is a stat tile instead.
 */
export function NewPostingsTrend({ rollups }: { rollups: DailyRollup[] }) {
  const t = useChartTokens();

  // Weekend from the calendar, never from the magnitude. A threshold on the count renders a
  // Tuesday the scraper failed on as though the market had gone quiet, which is the one
  // reading of this chart that must not be available.
  const data = rollups.map((r) => {
    const day = new Date(`${r.date}T00:00:00Z`).getUTCDay();
    return { date: r.date, newPostings: r.newPostings, weekend: day === 0 || day === 6 };
  });

  if (data.length < 2) {
    return (
      <div className="empty">
        Only {data.length} day{data.length === 1 ? '' : 's'} of history so far — a trend needs at
        least two scrape days.
      </div>
    );
  }

  return (
    <>
      <ResponsiveContainer width="100%" height={240}>
        {/* Columns rather than a line. Each scrape day is a discrete run, and a line between
            them draws a slope through hours in which nothing was measured. */}
        <BarChart data={data} margin={{ top: 8, right: 12, bottom: 4, left: -12 }}>
          <CartesianGrid stroke={t.grid} vertical={false} />
          <XAxis dataKey="date" tick={axisTick(t.muted)} tickLine={false} axisLine={{ stroke: t.axis }} />
          <YAxis tick={axisTick(t.muted)} tickLine={false} axisLine={false} width={44} allowDecimals={false} />
          <Tooltip
            cursor={{ fill: t.grid }}
            content={({ active, label, payload }) => (
              <ChartTooltip
                active={active}
                label={String(label ?? '')}
                rows={(payload ?? []).map((p) => ({ name: 'New postings', value: Number(p.value) }))}
              />
            )}
          />
          <Bar dataKey="newPostings" radius={[3, 3, 0, 0]}>
            {data.map((row) => (
              <Cell key={row.date} fill={row.weekend ? t.muted : t.series[0]} />
            ))}
          </Bar>
        </BarChart>
      </ResponsiveContainer>
      <p className="note">
        First sighting, not the date the board printed. Recessive columns are weekends, taken
        from the calendar — so a flat weekday reads as a scrape that failed rather than as a
        quiet market.
      </p>
    </>
  );
}
