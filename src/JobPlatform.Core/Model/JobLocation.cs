namespace JobPlatform.Core.Model;

/// <summary>Location split out of JobSpy's <c>"City, REGION, CC"</c> convention.</summary>
public readonly record struct JobLocation(string? City, string? Region, string? Country)
{
    public static JobLocation Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new JobLocation(null, null, null);
        }

        var parts = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        return parts.Length switch
        {
            0 => new JobLocation(null, null, null),
            1 => new JobLocation(parts[0], null, null),
            2 => new JobLocation(parts[0], null, parts[1]),
            _ => new JobLocation(parts[0], parts[1], parts[^1]),
        };
    }

    /// <summary>Display form used to group locations in metrics.</summary>
    public string Display => string.Join(", ", new[] { City, Country }.Where(p => !string.IsNullOrWhiteSpace(p)));
}
