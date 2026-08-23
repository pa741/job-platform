namespace JobPlatform.Core.Enrichment;

/// <summary>
/// Where the work happens.
/// </summary>
/// <remarks>
/// The three-way answer <c>is_remote</c> cannot give. A boolean has to put hybrid somewhere,
/// and wherever it puts it is wrong: counted as remote it overstates remote work, counted as
/// on-site it understates flexibility. <see cref="Unknown"/> is a real and frequent answer —
/// most postings simply do not say — and must not be collapsed into <see cref="OnSite"/>,
/// which would let silence masquerade as a stated policy.
/// </remarks>
public enum WorkArrangement
{
    Unknown = 0,
    OnSite,
    Hybrid,
    Remote,
}
