import { useEffect, useMemo, useRef, useState } from 'react';
import type { JobPlatformApi } from '../api/client';
import { PollingMetricsFeed, type MetricsFeed, type MetricsSnapshot } from './MetricsFeed';

export interface FeedState {
  snapshot: MetricsSnapshot | undefined;
  error: unknown;
  loading: boolean;
  kind: MetricsFeed['kind'];
  refresh: () => void;
}

/**
 * Subscribes to the metrics feed for a search term.
 *
 * Keeps the previous snapshot visible while a newer one is in flight: a dashboard that
 * blanks itself every refresh is worse than one showing data a minute old, and the
 * `receivedAt` stamp is what tells the reader which they are looking at.
 */
export function useMetricsFeed(api: JobPlatformApi, searchTerm: string | undefined): FeedState {
  const [snapshot, setSnapshot] = useState<MetricsSnapshot>();
  const [error, setError] = useState<unknown>();
  const [loading, setLoading] = useState(true);

  const feed = useMemo(
    () => (searchTerm ? new PollingMetricsFeed(api, searchTerm) : undefined),
    [api, searchTerm],
  );

  const feedRef = useRef(feed);
  feedRef.current = feed;

  useEffect(() => {
    if (!feed) return;

    setLoading(true);
    setError(undefined);
    // Not cleared: the outgoing term's data stays until the new term's arrives, which reads
    // as a refresh rather than a flash of empty state.

    const unsubscribe = feed.subscribe(
      (next) => {
        setSnapshot(next);
        setError(undefined);
        setLoading(false);
      },
      (err) => {
        setError(err);
        setLoading(false);
      },
    );

    return unsubscribe;
  }, [feed]);

  return {
    snapshot,
    error,
    loading,
    kind: feed?.kind ?? 'polling',
    refresh: () => feedRef.current?.refresh(),
  };
}
