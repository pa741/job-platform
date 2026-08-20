import type { ReactNode } from 'react';

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
