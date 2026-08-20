import { useEffect, useState } from 'react';

/**
 * Reads the resolved value of a design token.
 *
 * SVG `fill`/`stroke` in Recharts will not accept `var(--x)` reliably across renderers, so
 * tokens are resolved to concrete hex here. That resolution has to re-run when the theme
 * changes, which is what the hook below is for - otherwise a theme toggle repaints the page
 * and leaves the charts in the old palette.
 */
function readToken(name: string): string {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

export interface ChartTokens {
  series: string[];
  sequential: string[];
  grid: string;
  axis: string;
  muted: string;
  surface: string;
  text: string;
}

function snapshotTokens(): ChartTokens {
  return {
    // Fixed order. A chart takes slots from the front and never cycles - the fifth series
    // folds into "Other" rather than reusing slot 1, which would make two things one colour.
    series: ['--series-1', '--series-2', '--series-3', '--series-4'].map(readToken),
    sequential: ['--seq-250', '--seq-350', '--seq-450', '--seq-550', '--seq-650'].map(readToken),
    grid: readToken('--gridline'),
    axis: readToken('--axis'),
    muted: readToken('--text-muted'),
    surface: readToken('--surface-1'),
    text: readToken('--text-primary'),
  };
}

export function useChartTokens(): ChartTokens {
  const [tokens, setTokens] = useState<ChartTokens>(snapshotTokens);

  useEffect(() => {
    const update = () => setTokens(snapshotTokens());

    // Both triggers matter: the attribute for an explicit toggle, the media query for the
    // OS setting when no explicit choice has been made.
    const observer = new MutationObserver(update);
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });

    const media = window.matchMedia('(prefers-color-scheme: dark)');
    media.addEventListener('change', update);

    return () => {
      observer.disconnect();
      media.removeEventListener('change', update);
    };
  }, []);

  return tokens;
}
