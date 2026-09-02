import type { ReactNode } from 'react';
import type { NamedCount } from '../api/types';

export function Card({ title, subtitle, children, actions }: {
  title?: string; subtitle?: string; children: ReactNode; actions?: ReactNode;
}) {
  return (
    <section className="card">
      {(title || actions) && (
        <header style={{ display: 'flex', alignItems: 'flex-start', gap: 12 }}>
          <div style={{ flex: 1 }}>
            {title && <h2>{title}</h2>}
            {subtitle && <div className="sub">{subtitle}</div>}
          </div>
          {actions}
        </header>
      )}
      {children}
    </section>
  );
}

/**
 * A single headline number.
 *
 * A stat tile rather than a one-bar chart: for one current value plus its change, the
 * number IS the visualisation, and a bar of length one only adds furniture.
 */
export function StatTile({ label, value, delta, hint }: {
  label: string; value: string | number; delta?: number | null; hint?: string;
}) {
  const direction = delta == null || delta === 0 ? '' : delta > 0 ? 'up' : 'down';

  return (
    <div className="card tile">
      <div className="label">{label}</div>
      <div className="value">{value}</div>
      {delta != null && (
        // The arrow carries the direction as well as the colour, so the meaning survives
        // for a reader who cannot separate the red from the green.
        <div className={`delta ${direction}`}>
          {delta > 0 ? '↑' : delta < 0 ? '↓' : '→'} {Math.abs(delta)} vs previous day
        </div>
      )}
      {delta == null && hint && <div className="delta">{hint}</div>}
    </div>
  );
}

/**
 * One ratio against its limit.
 *
 * A meter, not a two-slice pie: the reader is comparing one value to 100%, and a track
 * shows that directly. The percentage is always written out, so the bar is reinforcement
 * rather than the only encoding.
 */
export function Meter({ label, ratio, caption }: { label: string; ratio: number; caption?: string }) {
  const percent = Math.round(ratio * 1000) / 10;

  return (
    <div className="card tile">
      <div className="label">{label}</div>
      <div className="value">{percent}%</div>
      <div className="meter">
        <div
          className="track"
          role="meter"
          aria-valuenow={percent}
          aria-valuemin={0}
          aria-valuemax={100}
          aria-label={label}
        >
          <div className="fill" style={{ width: `${Math.min(100, Math.max(0, percent))}%` }} />
        </div>
      </div>
      {caption && <div className="delta">{caption}</div>}
    </div>
  );
}

export function Legend({ items }: { items: { name: string; color: string }[] }) {
  return (
    <div className="legend">
      {items.map((item) => (
        <span key={item.name}>
          <i style={{ background: item.color }} aria-hidden="true" />
          {item.name}
        </span>
      ))}
    </div>
  );
}

export function ErrorNote({ error, onRetry }: { error: unknown; onRetry?: () => void }) {
  const message = error instanceof Error ? error.message : String(error);
  const detail = (error as { detail?: string })?.detail;
  const name = (error as { name?: string })?.name;

  // A timeout on this platform has one overwhelmingly likely cause, and saying so is more
  // useful than "the request timed out": the postings database is serverless, pauses when
  // idle, and takes up to a minute to wake. Naming that turns a dead end into "wait and
  // press retry".
  const timedOut = name === 'ApiTimeoutError';

  return (
    <div className="err">
      <strong>{timedOut ? 'The API did not respond in time.' : message}</strong>
      {timedOut && (
        <div className="muted" style={{ marginTop: 4 }}>
          The postings database pauses when idle and can take up to a minute to wake. If this
          was the first request in a while, retrying usually succeeds.
        </div>
      )}
      {!timedOut && detail && <div className="muted" style={{ marginTop: 4 }}>{detail}</div>}
      {onRetry && (
        <button className="btn" style={{ marginTop: 10 }} onClick={onRetry}>
          Retry
        </button>
      )}
    </div>
  );
}

/**
 * One labelled form control.
 *
 * Lives here rather than beside the form that first needed it: it is the shared shape of every
 * input in this dashboard, and a second copy is how two pages start looking different for no
 * decision anybody made.
 */
export function Field({ label, hint, children }: {
  label: string; hint?: string; children: ReactNode;
}) {
  return (
    <label className="field">
      <span className="field-label">{label}</span>
      {children}
      {hint && <span className="field-hint">{hint}</span>}
    </label>
  );
}

/**
 * A ranked list: name, value, and a proportional rule beneath.
 *
 * Replaces the Recharts `RankedBar`. This was never a chart — it is a name, a number and a
 * bar whose length is a ratio, which is three DOM nodes and a percentage. Rendering it through
 * a charting library cost a 110KB dependency an SVG axis nobody read, and it could not put the
 * value on the same baseline as the label.
 *
 * The bar is 6px on a real track rather than a 2px rule: at 2px against the card it measured
 * 1.57:1, and this is the most-used mark on the page. The number is always written out, so the
 * bar reinforces rather than encodes.
 */
export function RankList({ items, unit, max = 12 }: {
  items: NamedCount[]; unit?: string; max?: number;
}) {
  const rows = items.slice(0, max);

  if (rows.length === 0) return <div className="empty">Nothing to rank yet.</div>;

  const top = Math.max(...rows.map((r) => r.count), 1);

  return (
    <div className="rank">
      {rows.map((row) => (
        <div key={row.name}>
          <div className="rank-row">
            <span className="nm">{row.name}</span>
            <span className="vl">
              {row.count.toLocaleString()}{unit ? ` ${unit}` : ''}
            </span>
          </div>
          <div className="measure">
            <i style={{ width: `${(row.count / top) * 100}%` }} />
          </div>
        </div>
      ))}
    </div>
  );
}

/**
 * Part-to-whole, as one bar and a key.
 *
 * Five categorical slots in fixed order, never cycled: a sixth category folds into "Other"
 * rather than reusing slot one, which would make two things one colour. Every share is written
 * out beside its name, so the bar is never the only encoding — the segments below a few per
 * cent are a handful of pixels and no hue survives that.
 */
export function SplitBar({ data, emptyMessage }: {
  data: Record<string, number>; emptyMessage: string;
}) {
  const entries = Object.entries(data).filter(([, n]) => n > 0).sort((a, b) => b[1] - a[1]);

  if (entries.length === 0) return <div className="empty">{emptyMessage}</div>;

  const shown: [string, number][] = entries.length <= 5
    ? entries
    : [...entries.slice(0, 4), ['Other', entries.slice(4).reduce((sum, [, n]) => sum + n, 0)]];

  const total = shown.reduce((sum, [, n]) => sum + n, 0);
  const slot = (i: number) => `var(--series-${i + 1}, var(--text-muted))`;

  return (
    <>
      <div className="split">
        {shown.map(([name, count], i) => (
          <i key={name} style={{ flex: count, background: slot(i) }} title={`${name}: ${count}`} />
        ))}
      </div>
      <div className="keys">
        {shown.map(([name, count], i) => (
          <div key={name}>
            <i style={{ background: slot(i) }} aria-hidden="true" />
            <span>{name}</span>
            <b>{Math.round((count / total) * 100)}%</b>
          </div>
        ))}
      </div>
    </>
  );
}
