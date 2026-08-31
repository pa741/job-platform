import { useEffect, useMemo, useRef, useState } from 'react';
import { HttpTransportType, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import type { HubConnection } from '@microsoft/signalr';
import type { JobPlatformApi } from '../api/client';

/** A model call that lost something, as the server pushes it. */
export interface AiFailureNotice {
  operation: string;
  deployment: string | null;
  outcome: string;
  requested: number;
  returned: number;
  reason: string | null;
  occurredAtUtc: string;
  discarded: number;
}

export type RealtimeState = 'connecting' | 'live' | 'unavailable';

/**
 * Live AI failures, pushed as they are recorded.
 *
 * **Deliberately not routed through `MetricsFeed`.** That seam exists so the overview's polling
 * can become a push without touching components, and it should stay polling: the metrics it
 * carries change once a day when the scraper runs, so a socket there would mostly deliver
 * silence. A failed model call is the opposite — sporadic, unpredictable, and worth knowing about
 * within seconds rather than within a poll interval. Different data, different transport, and
 * forcing one interface over both would make the overview's honest "polling" label a lie.
 *
 * The page it feeds still fetches its own history on load and still works with this returning
 * nothing at all: `unavailable` is a normal state, not an error. A deployment can have no
 * realtime service, and the API says so with a 503 rather than pretending.
 */
export function useAiFailures(api: JobPlatformApi, enabled: boolean) {
  const [state, setState] = useState<RealtimeState>(enabled ? 'connecting' : 'unavailable');
  const [failures, setFailures] = useState<AiFailureNotice[]>([]);

  // Held in a ref so a re-render cannot start a second connection against a free tier that
  // allows twenty in total.
  const connection = useRef<HubConnection>(undefined);

  useEffect(() => {
    if (!enabled) {
      setState('unavailable');
      return;
    }

    let cancelled = false;

    const connect = async () => {
      try {
        const negotiated = await api.negotiateRealtime();

        if (cancelled) return;

        const hub = new HubConnectionBuilder()
          .withUrl(negotiated.url, {
            accessTokenFactory: () => negotiated.accessToken,
            // WebSockets only, and skip the negotiate handshake: the API has already done it.
            // Left to itself the client negotiates a second time against the service, which in
            // serverless mode has no app server to answer and fails in a way that reads like an
            // auth problem.
            transport: HttpTransportType.WebSockets,
            skipNegotiation: true,
          })
          .withAutomaticReconnect()
          .configureLogging(LogLevel.Warning)
          .build();

        hub.on('aiFailure', (notice: AiFailureNotice) => {
          // Newest first, and bounded. This is a live tail, not a store - the ledger endpoint is
          // the history, and an unbounded array on a page somebody leaves open for a day is a
          // leak with a chart on top.
          setFailures((current) => [notice, ...current].slice(0, 50));
        });

        hub.onreconnecting(() => setState('connecting'));
        hub.onreconnected(() => setState('live'));
        hub.onclose(() => setState('unavailable'));

        await hub.start();

        if (cancelled) {
          await hub.stop();
          return;
        }

        connection.current = hub;
        setState('live');
      } catch {
        // No realtime service, no permission, or the service is down. All three mean the same
        // thing to this page: carry on with what the ledger endpoint returned.
        if (!cancelled) setState('unavailable');
      }
    };

    void connect();

    return () => {
      cancelled = true;
      const hub = connection.current;
      connection.current = undefined;

      if (hub && hub.state !== HubConnectionState.Disconnected) {
        void hub.stop();
      }
    };
  }, [api, enabled]);

  return useMemo(() => ({ state, failures }), [state, failures]);
}
