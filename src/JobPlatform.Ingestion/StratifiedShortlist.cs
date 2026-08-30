namespace JobPlatform.Ingestion;

/// <summary>
/// Merges the shortlist the candidate benefits from with the sample the measurement needs.
/// </summary>
/// <remarks>
/// <b>Pure and free of Azure and of the data types, for the reason <c>BoundedWalk</c> is.</b> The
/// interesting part here is an interleave with a deduplication in it, which is exactly the kind of
/// thing that looks obviously right and is off by one - and it is only assertable exactly while
/// running it needs nothing but lists.
///
/// <b>Why it exists.</b> The nightly sweep spends its model budget top-down by score, which is
/// right for the product: the judgement is worth most where the arithmetic is most hopeful, and
/// those are the rows somebody actually reads. But it means every label the system has ever
/// produced describes the top of the score range - three consecutive nights returned 92-100, then
/// 89-100 - and no amount of that can answer whether the ranking works anywhere else. It is the
/// same pooling bias that made the deterministic score look anti-correlated at -0.198 when it is
/// really +0.31, now built into the mechanism that produces the evidence.
///
/// The two purposes genuinely conflict, so the budget is split rather than the question decided.
/// The measurement rows are merged into the same batches as the shortlist ones, which is what
/// makes this free: the assessor sends the candidate's profile once per batch and the profile is
/// the larger half of the prompt, so ten rows riding along in existing batches cost ten rows'
/// worth of advert text and nothing else. A separate pass would have paid for the profile again.
/// </remarks>
public static class StratifiedShortlist
{
    /// <summary>
    /// The top-down shortlist first, then one row from each band in turn until the budget is full.
    /// </summary>
    /// <remarks>
    /// <b>Round-robin rather than band-by-band, so a short budget still spans the range.</b>
    /// Concatenating the bands would spend a remainder of two entirely on 45-59 and never reach
    /// 80-89; taking one from each in turn means the sample degrades evenly when there is not
    /// enough room for all of it.
    ///
    /// <b>The shortlist wins every collision.</b> A posting can legitimately appear in both - the
    /// bands are drawn by posting id and the shortlist by score, so a high-scoring row can be the
    /// first id in its band - and assessing it twice would pay twice for one answer. It stays in
    /// the shortlist, because that is the half a person is going to read.
    ///
    /// <b>A band that collides advances rather than forfeiting its turn, and that is not a
    /// detail.</b> Collisions are not spread evenly across the bands: the shortlist is the
    /// highest-scoring unassessed rows, so it is the <i>top</i> band that overlaps with it, and
    /// letting a duplicate consume that band's slot would quietly under-sample 80-89 - the band
    /// nearest the region the ranking actually acts on. Each band therefore keeps a cursor and
    /// contributes its next unseen row when its turn comes.
    /// </remarks>
    /// <param name="topDown">The rows the model budget exists to judge, best first.</param>
    /// <param name="bands">One list per score band, each already ordered as it should be sampled.</param>
    /// <param name="limit">Total rows to return, shortlist included.</param>
    /// <param name="id">How to tell two rows apart.</param>
    public static IReadOnlyList<T> Combine<T>(
        IReadOnlyList<T> topDown,
        IReadOnlyList<IReadOnlyList<T>> bands,
        int limit,
        Func<T, long> id)
    {
        ArgumentNullException.ThrowIfNull(topDown);
        ArgumentNullException.ThrowIfNull(bands);
        ArgumentNullException.ThrowIfNull(id);

        if (limit <= 0)
        {
            return [];
        }

        var combined = new List<T>(limit);
        var seen = new HashSet<long>();

        foreach (var row in topDown)
        {
            if (combined.Count >= limit)
            {
                break;
            }

            if (seen.Add(id(row)))
            {
                combined.Add(row);
            }
        }

        // One cursor per band rather than one shared index, so a band whose next row is already
        // in the shortlist skips that row instead of skipping its turn.
        var cursors = new int[bands.Count];

        for (var progressed = true; progressed && combined.Count < limit;)
        {
            progressed = false;

            for (var b = 0; b < bands.Count && combined.Count < limit; b++)
            {
                var band = bands[b];

                if (band is null)
                {
                    continue;
                }

                while (cursors[b] < band.Count && !seen.Add(id(band[cursors[b]])))
                {
                    cursors[b]++;
                }

                if (cursors[b] < band.Count)
                {
                    combined.Add(band[cursors[b]]);
                    cursors[b]++;
                    progressed = true;
                }
            }
        }

        return combined;
    }
}
