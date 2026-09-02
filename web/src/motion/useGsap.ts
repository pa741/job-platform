import { useEffect, useRef } from 'react';
import gsap from 'gsap';

/**
 * Whether this viewer has asked for less motion.
 *
 * Read once per call rather than cached in a module: the OS setting can change while the tab
 * is open, and a value captured at import time would keep animating for somebody who has just
 * turned it off.
 */
export function prefersReducedMotion(): boolean {
  return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
}

/**
 * Runs a GSAP timeline once, scoped to an element, and cleans it up.
 *
 * <b>The budget is four things: the nav marker, the drawer, an expanding entry, and chart
 * marks on first mount.</b> Everything else that was animated in the mock came back out. In
 * particular there are no count-ups on figures: a page mid-animation and a page legitimately
 * reading zero looked identical, and for the first second of every visit the lede read "the
 * scrapers found 0 new postings".
 *
 * Under reduced motion the callback is not called at all. That is deliberate rather than a
 * shortened duration: every animation here is written so the DOM's resting state is the
 * finished state, so skipping it lands on the right frame instead of a fast version of the
 * wrong one.
 */
export function useGsapEffect(
  effect: (context: gsap.Context, element: HTMLElement) => void,
  deps: React.DependencyList,
): React.RefObject<HTMLDivElement | null> {
  const scope = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    const element = scope.current;
    if (!element || prefersReducedMotion()) return;

    const context = gsap.context((self) => effect(self, element), element);
    return () => context.revert();
    // The caller owns the dependency list; this hook cannot know what the effect closes over.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps);

  return scope;
}

/**
 * Moves an indicator to sit under an element, by transform rather than by width.
 *
 * Used by both the section pill and the page tabs. Two details that were bugs in the mock
 * before they were rules here: it is placed with `set` on first paint rather than tweened, or
 * the selected label sits on a zero-width pill and reads as white-on-white for the length of
 * the animation; and it scales a 1px element rather than animating `width`, which is a layout
 * property and the one thing in this file that would cost a reflow per frame.
 */
export function placeIndicator(
  indicator: HTMLElement | null,
  target: HTMLElement | null,
  container: HTMLElement | null,
  animate: boolean,
  offset = 0,
): void {
  if (!indicator || !target || !container) return;

  const bounds = target.getBoundingClientRect();
  const base = container.getBoundingClientRect();
  const props = { x: bounds.left - base.left - offset, scaleX: bounds.width };

  if (animate && !prefersReducedMotion()) {
    gsap.to(indicator, { ...props, duration: 0.32, ease: 'power3.out' });
  } else {
    gsap.set(indicator, props);
  }
}

export { gsap };
