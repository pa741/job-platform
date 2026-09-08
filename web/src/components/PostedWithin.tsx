/**
 * "Posted within" as one control, shared by every list read for what to do today.
 *
 * The whole pipeline is built around a day: the scraper runs once, the sweep judges what
 * arrived, and an application sent a week after the advert went up is competing against a
 * shortlist the employer has already drawn. So age is a filter on the shortlist, on the corpus
 * search and on the apply queue, and it is the same filter in all three — one option list, one
 * set of words for it, and one place to change when the cadence changes.
 *
 * It sends a window in days rather than a date, because the server holds the clock. A date
 * resolved here is resolved against a browser that may be minutes out, and a bookmarked filter
 * would ask yesterday's question with today's wording still on the screen.
 *
 * What the server does with the window is in `PostingAge`: the board's own posted date where it
 * published one, and when this system first read the posting where it did not. Two postings in
 * five state a date, so a filter that believed only those would hide most of the market — and a
 * filter that believed only first-seen would let a search term added this week deliver
 * three-week-old jobs as today's.
 */
export function PostedWithin({ id, value, onChange }: {
  id: string;
  /** Days, or undefined for no bound. */
  value: number | undefined;
  onChange: (days: number | undefined) => void;
}) {
  return (
    <div>
      <label htmlFor={id}>Posted</label>
      <select
        id={id}
        value={value ?? ''}
        onChange={(e) => onChange(e.target.value === '' ? undefined : Number(e.target.value))}
      >
        {/* "Any time" first and selected by default. A list silently showing only today's
            postings would read as an empty market rather than as a filter. */}
        <option value="">Any time</option>
        <option value="1">Today and yesterday</option>
        <option value="3">Last 3 days</option>
        <option value="7">Last week</option>
        <option value="30">Last month</option>
      </select>
    </div>
  );
}
