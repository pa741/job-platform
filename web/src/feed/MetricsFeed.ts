import type { JobPlatformApi } from '../api/client';
import type { DailyRollup, MetricsSummary, ScraperHealth } from '../api/types';

/** One coherent read of everything the overview renders. */
export interface MetricsSnapshot {
  summary: MetricsSummary;
  rollups: DailyRollup[];
  health: ScraperHealth;
  receivedAt: Date;
}

export type SnapshotListener = (snapshot: MetricsSnapshot) => void;
export type FeedErrorListener = (error: unknown) => void;

/**
 * A source of metric snapshots.
 *
 * The seam that keeps "live metrics" from being a rewrite. The dashboard consumes snapshots
 * and never learns how they arrived, so replacing polling with the planned Cosmos change
 * feed over Web PubSub is a new implementation of this interface plus one line where the
 * feed is constructed - no component changes.
 *
 * That is worth an interface now rather than later because the alternative is every
 * component holding its own fetch and refresh timer, which is exactly the shape that cannot
 * be converted to push.
 */
export interface MetricsFeed {
  /**
   * Starts delivering snapshots and returns an unsubscribe function. Implementations must
   * deliver one snapshot as soon as they can rather than waiting for their first interval
   * or message, so the UI is not empty on load.
   */
  subscribe(onSnapshot: SnapshotListener, onError: FeedErrorListener): () => void;

  /** Asks for a snapshot now, out of band. */
  refresh(): void;

  /** How this feed gets its data, shown in the UI so freshness is never a guess. */
  readonly kind: 'polling' | 'push';
}

/**
 * Polls the API on an interval.
 *
 * The right implementation today: the underlying data changes once a day, when the scraper
 * runs, and these endpoints are served from Cosmos behind an output cache - so a slow poll
 * costs almost nothing and a push channel would mostly deliver silence. It is the freshness
 * the data actually has, not the freshness a WebSocket would imply.
 */
export class PollingMetricsFeed implements MetricsFeed {
  readonly kind = 'polling';

  private timer: ReturnType<typeof setInterval> | undefined;
  private listeners = new Set<SnapshotListener>();
  private errorListeners = new Set<FeedErrorListener>();
  private inFlight = false;

  constructor(
    private readonly api: JobPlatformApi,
    private readonly searchTerm: string,
    private readonly intervalMs = 60_000,
  ) {}

  subscribe(onSnapshot: SnapshotListener, onError: FeedErrorListener): () => void {
    this.listeners.add(onSnapshot);
    this.errorListeners.add(onError);

    if (!this.timer) {
      void this.poll();
      this.timer = setInterval(() => void this.poll(), this.intervalMs);
    }

    return () => {
      this.listeners.delete(onSnapshot);
      this.errorListeners.delete(onError);

      if (this.listeners.size === 0 && this.timer) {
        clearInterval(this.timer);
        this.timer = undefined;
      }
    };
  }

  refresh(): void {
    void this.poll();
  }

  private async poll(): Promise<void> {
    // A slow response must not stack up behind the interval; skipping is better than
    // queueing requests the user will never see the result of.
    if (this.inFlight) return;
    this.inFlight = true;

    try {
      const [summary, rollups, health] = await Promise.all([
        this.api.summary(this.searchTerm),
        this.api.rollups(this.searchTerm),
        this.api.scraperHealth(this.searchTerm),
      ]);

      const snapshot: MetricsSnapshot = { summary, rollups, health, receivedAt: new Date() };
      for (const listener of this.listeners) listener(snapshot);
    } catch (error) {
      for (const listener of this.errorListeners) listener(error);
    } finally {
      this.inFlight = false;
    }
  }
}
