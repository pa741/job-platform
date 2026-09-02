import { useCallback, useEffect, useState } from 'react';
import { ApiTimeoutError } from '../api/client';

/**
 * The four states a read can be in, with the one that matters given a name.
 *
 * `waking` is a timeout, and a timeout here has one overwhelmingly likely cause: the postings
 * database is serverless, pauses when idle, and takes up to a minute to come back. Treating
 * that as an error tells somebody something went wrong when nothing did. Treating it as
 * loading leaves a spinner with nothing to click, which `CLAUDE.md` calls the worst failure
 * this architecture can produce, because pausing here is normal rather than exceptional.
 */
export type ResourceState<T> =
  | { status: 'loading'; data?: undefined; error?: undefined }
  | { status: 'ok'; data: T; error?: undefined }
  | { status: 'waking'; data?: undefined; error: unknown }
  | { status: 'error'; data?: undefined; error: unknown };

export interface Resource<T> {
  state: ResourceState<T>;
  reload: () => void;
}

/**
 * Runs one read and classifies how it went.
 *
 * The `load` callback must be stable - wrap it in `useCallback` with the query it closes over
 * as dependencies, the way the pages already do. That is what makes a filter change refetch
 * and a re-render not.
 */
export function useApiResource<T>(load: () => Promise<T>): Resource<T> {
  const [state, setState] = useState<ResourceState<T>>({ status: 'loading' });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let live = true;
    setState({ status: 'loading' });

    load().then(
      (data) => {
        if (live) setState({ status: 'ok', data });
      },
      (error: unknown) => {
        if (!live) return;

        // instanceof first; the name check is the fallback, because that is how ErrorNote has
        // always detected it and a bundling boundary can break identity where the string holds.
        const waking = error instanceof ApiTimeoutError
          || (error as { name?: string })?.name === 'ApiTimeoutError';

        setState({ status: waking ? 'waking' : 'error', error });
      },
    );

    return () => { live = false; };
  }, [load, attempt]);

  const reload = useCallback(() => setAttempt((n) => n + 1), []);

  return { state, reload };
}
