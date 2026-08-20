import {
  Bar, BarChart, CartesianGrid, Cell, Line, LineChart, ResponsiveContainer,
  Tooltip, XAxis, YAxis,
} from 'recharts';
import type { DailyRollup, NamedCount } from '../api/types';
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
  const data = rollups.map((r) => ({ date: r.date, newPostings: r.newPostings, seen: r.postingsSeen }));

  if (data.length < 2) {
    return (
      <div className="empty">
        Only {data.length} day{data.length === 1 ? '' : 's'} of history so far — a trend needs at
        least two scrape days.
      </div>
    );
  }

  return (
    <ResponsiveContainer width="100%" height={240}>
      <LineChart data={data} margin={{ top: 8, right: 12, bottom: 4, left: -12 }}>
        <CartesianGrid stroke={t.grid} vertical={false} />
        <XAxis dataKey="date" tick={axisTick(t.muted)} tickLine={false} axisLine={{ stroke: t.axis }} />
        <YAxis tick={axisTick(t.muted)} tickLine={false} axisLine={false} width={44} allowDecimals={false} />
        <Tooltip
          cursor={{ stroke: t.axis }}
          content={({ active, label, payload }) => (
            <ChartTooltip
              active={active}
              label={String(label ?? '')}
              rows={(payload ?? []).map((p) => ({ name: 'New postings', value: Number(p.value) }))}
            />
          )}
        />
        <Line
          type="monotone"
          dataKey="newPostings"
          stroke={t.series[0]}
          strokeWidth={2}
          dot={{ r: 4, fill: t.series[0], stroke: t.surface, strokeWidth: 2 }}
          activeDot={{ r: 6, stroke: t.surface, strokeWidth: 2 }}
        />
      </LineChart>
    </ResponsiveContainer>
  );
}

/**
 * Magnitude comparison over named categories - companies, keywords, locations.
 *
 * Horizontal because the labels are long words, not dates: rotated x-axis labels are a
 * readability tax that a horizontal layout simply avoids. One hue on the sequential ramp,
 * because these bars are one measure at different magnitudes, not distinct series - giving
 * each bar its own colour would imply an identity the data does not have.
 */
export function RankedBar({ items, max = 12, valueLabel }: {
  items: NamedCount[]; max?: number; valueLabel: string;
}) {
  const t = useChartTokens();
  const data = items.slice(0, max);

  if (data.length === 0) return <div className="empty">Nothing to show yet.</div>;

  const top = Math.max(...data.map((d) => d.count));

  return (
    <ResponsiveContainer width="100%" height={Math.max(180, data.length * 26 + 20)}>
      <BarChart data={data} layout="vertical" margin={{ top: 0, right: 28, bottom: 0, left: 8 }}>
        <CartesianGrid stroke={t.grid} horizontal={false} />
        <XAxis type="number" hide allowDecimals={false} />
        <YAxis
          type="category"
          dataKey="name"
          width={150}
          tick={axisTick(t.muted)}
          tickLine={false}
          axisLine={false}
        />
        <Tooltip
          cursor={{ fill: t.grid }}
          content={({ active, label, payload }) => (
            <ChartTooltip
              active={active}
              label={String(label ?? '')}
              rows={(payload ?? []).map((p) => ({ name: valueLabel, value: Number(p.value) }))}
            />
          )}
        />
        {/* 4px rounded data-end, square against the baseline. */}
        <Bar dataKey="count" radius={[0, 4, 4, 0]} barSize={14} label={{
          position: 'right', fill: t.muted, fontSize: 11,
        }}>
          {data.map((d) => (
            // More is darker: the ramp encodes the same magnitude the bar length does,
            // which is redundancy on purpose - it survives a greyscale print.
            <Cell
              key={d.name}
              fill={t.sequential[Math.min(
                t.sequential.length - 1,
                Math.floor((d.count / top) * (t.sequential.length - 1)),
              )]}
            />
          ))}
        </Bar>
      </BarChart>
    </ResponsiveContainer>
  );
}

/**
 * Which boards produced the postings - a part-to-whole across a handful of sources.
 *
 * A stacked horizontal bar rather than a pie: comparing arc lengths is harder than
 * comparing positions along a line, and the bar keeps the total explicit. Categorical
 * colours here because the boards ARE the subject; a 2px surface gap separates the
 * segments so adjacent fills never blur into one another under CVD.
 */
export function SiteSplit({ bySite }: { bySite: Record<string, number> }) {
  const t = useChartTokens();
  const entries = Object.entries(bySite).sort((a, b) => b[1] - a[1]);
  const total = entries.reduce((sum, [, count]) => sum + count, 0);

  if (total === 0) return <div className="empty">No postings in the last run.</div>;

  // Past four boards the tail folds into "Other" rather than reaching for a fifth hue:
  // generated colours are indistinguishable under colour-vision deficiency.
  const shown = entries.slice(0, 4);
  const rest = entries.slice(4).reduce((sum, [, count]) => sum + count, 0);
  const segments = rest > 0 ? [...shown, ['Other', rest] as const] : shown;

  return (
    <div>
      <div style={{ display: 'flex', gap: 2, height: 34, marginBottom: 12 }}>
        {segments.map(([name, count], index) => (
          <div
            key={name}
            title={`${name}: ${count}`}
            style={{
              width: `${(count / total) * 100}%`,
              background: index < t.series.length ? t.series[index] : t.muted,
              borderRadius:
                index === 0
                  ? '4px 0 0 4px'
                  : index === segments.length - 1
                    ? '0 4px 4px 0'
                    : '0',
              display: 'grid',
              placeItems: 'center',
              color: '#fff',
              fontSize: 11,
              fontWeight: 600,
            }}
          >
            {/* Direct label inside the segment when it is wide enough to hold one. This is
                also the relief for the light-mode aqua slot, which sits below 3:1 against
                the surface: the label carries the value, not the fill alone. */}
            {count / total > 0.08 ? count : ''}
          </div>
        ))}
      </div>

      <table>
        <thead>
          <tr>
            <th>Board</th>
            <th className="num">Postings</th>
            <th className="num">Share</th>
          </tr>
        </thead>
        <tbody>
          {segments.map(([name, count], index) => (
            <tr key={name}>
              <td>
                <span style={{ display: 'inline-flex', alignItems: 'center', gap: 8 }}>
                  <i
                    aria-hidden="true"
                    style={{
                      width: 10, height: 10, borderRadius: 3,
                      background: index < t.series.length ? t.series[index] : t.muted,
                      display: 'inline-block',
                    }}
                  />
                  {name}
                </span>
              </td>
              <td className="num">{count}</td>
              <td className="num">{Math.round((count / total) * 1000) / 10}%</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
