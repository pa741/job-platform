namespace JobPlatform.Core.Enrichment;

/// <summary>
/// What kind of work a posting is for, at the coarsest grain worth grouping by.
/// </summary>
/// <remarks>
/// Coarse on purpose. A finer taxonomy looks more precise and classifies worse: the only
/// evidence available is a job title, and titles do not reliably distinguish, say, a data
/// engineer from an analytics engineer. Anything finer than this belongs in the skill
/// taxonomy, where a posting can carry many values instead of being forced into one.
/// </remarks>
public enum RoleFamily
{
    Unknown = 0,
    Backend,
    Frontend,
    FullStack,
    Mobile,

    /// <summary>Data engineering, analytics, BI, warehousing.</summary>
    Data,

    /// <summary>ML, AI, research, data science.</summary>
    MachineLearning,

    /// <summary>DevOps, SRE, infrastructure, cloud, platform.</summary>
    Platform,

    Security,
    QA,
    Embedded,

    /// <summary>Engineering management, at any level.</summary>
    Management,

    Product,
    Design,
}
