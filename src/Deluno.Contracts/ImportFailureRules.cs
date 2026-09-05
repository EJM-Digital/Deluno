namespace Deluno.Contracts;

/// <summary>
/// How the seventeen failure kinds are grouped on the rules screen.
///
/// <para>The grouping is not decoration. Printed alphabetically, a list of
/// seventeen codes reads as an inventory and gives the reader no way to decide
/// anything; grouped by <em>whose fault it was</em>, it reads as the argument
/// the decisions were actually made from — and the argument is what tells
/// somebody whether "refuse immediately" is reasonable for that row.</para>
///
/// <para>It lives beside the decision table rather than in the screen, because
/// it is the same classification <see cref="ImportFailurePolicy.BlockFor"/>
/// already switches on. Two copies of that judgement would drift.</para>
/// </summary>
public static class FailureCategories
{
    /// <summary>Deluno read the file and it was not what was wanted.</summary>
    public const string BadFile = "badFile";

    /// <summary>It failed, and Deluno cannot say whose fault that was.</summary>
    public const string CannotSay = "cannotSay";

    /// <summary>Something about this installation, not about the release.</summary>
    public const string YourSetup = "yourSetup";

    /// <summary>Deluno working correctly, filed as a failure by history.</summary>
    public const string NotAFailure = "notAFailure";
}

/// <summary>
/// One row of the rules screen: what Deluno does with a failure of this kind,
/// what it would do if nobody had said otherwise, and whether somebody has.
///
/// <para>DESIGN-007, on all sixteen decisions at once: <i>"I think all these
/// things we decided need to have configuration toggles to set them on and off
/// in a management / blocklist console."</i> So every row of the shipped table
/// is a default, and this record is a default with the user's answer on top of
/// it.</para>
/// </summary>
/// <param name="Decision">What happens now — the override if there is one.</param>
/// <param name="DefaultDecision">
/// What Deluno ships with. Sent alongside so the screen can say "back to
/// default" and mean something specific, rather than offering a reset whose
/// result the reader has to guess.
/// </param>
public sealed record ImportFailureRule(
    string ReasonCode,
    string Category,
    BlockDecision Decision,
    BlockDecision DefaultDecision)
{
    public bool IsOverridden => Decision != DefaultDecision;
}

/// <summary>The body of a rule change: one field, because there is one choice.</summary>
public sealed record SetImportFailureRuleRequest(BlockDecision Decision);
