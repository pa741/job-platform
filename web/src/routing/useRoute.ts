import { useCallback, useEffect, useState } from 'react';
import { buildRoute, parseRoute, type PageId, type Route } from './route';

/**
 * The current route, and the two ways to change it.
 *
 * A hand-written router rather than a dependency. Nine pages, one path segment and a query
 * string is not what `react-router` is for, and a router would have to be named in `model.md`
 * to be there at all - the architecture doc lists the frontend's technology and is binding.
 *
 * <b>History API rather than a hash.</b> `staticwebapp.config.json` already rewrites
 * navigation and 404s to `/index.html`, which is the whole server-side requirement, so real
 * paths cost nothing here and read properly when somebody shares one.
 */
export interface Navigation {
  route: Route;

  /**
   * A new history entry. For anything a Back press should undo: changing page, opening a
   * posting.
   */
  go: (page: PageId, id?: string | null, params?: URLSearchParams) => void;

  /**
   * The same entry, new state. For filters and for walking the concept graph - otherwise Back
   * steps through every keystroke somebody typed into a search box, and leaving the page
   * means pressing it twenty times.
   */
  replace: (page: PageId, id?: string | null, params?: URLSearchParams) => void;

  /** Back, for closing something that pushed. Falls through to a replace at the start of history. */
  back: (fallback: () => void) => void;
}

function current(): Route {
  return parseRoute(window.location.pathname, window.location.search);
}

export function useRoute(): Navigation {
  const [route, setRoute] = useState<Route>(current);

  // popstate is the only signal for Back and Forward; pushState and replaceState do not fire
  // it, which is why both helpers below set the state themselves.
  useEffect(() => {
    const onPop = () => setRoute(current());
    window.addEventListener('popstate', onPop);
    return () => window.removeEventListener('popstate', onPop);
  }, []);

  const go = useCallback((page: PageId, id?: string | null, params?: URLSearchParams) => {
    const url = buildRoute(page, id, params);

    // Pushing the URL you are already on would make Back a no-op that looks broken.
    if (url !== window.location.pathname + window.location.search) {
      window.history.pushState(null, '', url);
    }

    setRoute(current());
  }, []);

  const replace = useCallback((page: PageId, id?: string | null, params?: URLSearchParams) => {
    window.history.replaceState(null, '', buildRoute(page, id, params));
    setRoute(current());
  }, []);

  const back = useCallback((fallback: () => void) => {
    // A drawer opened from a pasted link has nothing to go back to, and calling back() would
    // leave the app entirely. `state` is null on the entry the browser started with.
    if (window.history.state === null && window.history.length <= 1) {
      fallback();
      return;
    }

    window.history.back();
  }, []);

  return { route, go, replace, back };
}
