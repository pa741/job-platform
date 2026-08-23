namespace JobPlatform.Core.Enrichment;

/// <summary>
/// How senior a posting reads, on one ordinal ladder.
/// </summary>
/// <remarks>
/// Deliberately one ladder rather than a separate individual-contributor and management
/// scale. The question an analysis asks of this column is "how senior", and two scales make
/// that unanswerable without a join. What kind of work a role is — including whether it is a
/// management role — is <see cref="RoleFamily"/>'s job, so nothing is lost by folding
/// "Engineering Manager" onto <see cref="Lead"/> here and onto
/// <see cref="RoleFamily.Management"/> there.
///
/// The values are ordered and their numbers are meaningful: <c>&gt;= Senior</c> is a valid
/// filter. <see cref="Unknown"/> is zero so it sorts below everything, and it is common —
/// Indeed publishes no seniority at all, so for those rows this is inferred from the title
/// or not at all.
/// </remarks>
public enum Seniority
{
    Unknown = 0,

    /// <summary>Intern, placement, apprentice, work experience.</summary>
    Intern = 1,

    /// <summary>Junior, graduate, entry level, trainee.</summary>
    Junior = 2,

    /// <summary>Mid level, intermediate, associate.</summary>
    Mid = 3,

    Senior = 4,

    /// <summary>Lead, staff, team lead, engineering manager.</summary>
    Lead = 5,

    /// <summary>Principal, distinguished, architect, head of, director.</summary>
    Principal = 6,

    /// <summary>VP, chief, C-level.</summary>
    Executive = 7,
}
