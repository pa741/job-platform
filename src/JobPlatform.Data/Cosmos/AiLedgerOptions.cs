namespace JobPlatform.Data.Cosmos;

/// <summary>
/// What the AI call ledger is allowed to keep.
/// </summary>
public sealed class AiLedgerOptions
{
    public const string SectionName = "AiLedger";

    /// <summary>
    /// Keep the prompt of a call that lost something, so the failure can be replayed.
    /// </summary>
    /// <remarks>
    /// <b>Off by default, and that is not caution for its own sake.</b> The assessor's and the
    /// profile extractor's prompts carry somebody's employment history, contact details and
    /// salary expectations. Turning this on is a decision to store personal data in a
    /// diagnostics container, and it should be made deliberately, per deployment, by somebody
    /// who wants to debug something.
    ///
    /// Even on, the sink keeps a prompt only for a call that did not fully succeed - a success
    /// has nothing to reproduce - and the list endpoint never returns one.
    /// </remarks>
    public bool RecordPrompts { get; set; }
}
