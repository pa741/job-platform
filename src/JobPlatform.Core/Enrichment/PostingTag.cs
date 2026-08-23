namespace JobPlatform.Core.Enrichment;

/// <summary>
/// A sparse fact about a posting: a name, and optionally a value.
/// </summary>
/// <remarks>
/// A key/value bag rather than columns because the tail is long and mostly null. The handful
/// worth filtering on directly — visa sponsorship, IR35 — are promoted to real columns on the
/// posting as well; everything else lives only here, where a new tag costs no migration.
///
/// Tags are for facts about the <i>offer</i>. Anything an employer asks the candidate to
/// <i>have</i> is a concept, not a tag: security clearances and degrees were tags in an
/// earlier draft and are now <c>type.clearance</c> and <c>type.degree</c> concepts, which say
/// which one rather than merely that there was one. Keeping both would have meant two places
/// to look and two answers that could disagree.
/// </remarks>
/// <param name="Name">One of <see cref="PostingTagNames"/>.</param>
/// <param name="Value">
/// The detail, where there is one: <c>"25"</c> for holiday days, <c>"5"</c> for a pension
/// percentage. Null means the tag is a bare flag — its presence is the fact.
/// </param>
public readonly record struct PostingTag(string Name, string? Value = null);

/// <summary>
/// The tag vocabulary. Constants rather than strings at the call site, because a typo in a
/// tag name produces no error — just a category that quietly never matches anything.
/// </summary>
public static class PostingTagNames
{
    public const string VisaSponsorship = "visa-sponsorship";
    public const string Ir35 = "ir35";
    public const string Equity = "equity";
    public const string PensionPercent = "pension-percent";
    public const string HolidayDays = "holiday-days";
    public const string OnCall = "on-call";
    public const string ShiftWork = "shift-work";
    public const string RelocationSupport = "relocation-support";
    public const string SignOnBonus = "sign-on-bonus";
    public const string FourDayWeek = "four-day-week";
    public const string TravelRequired = "travel-required";
}
