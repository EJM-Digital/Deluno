namespace Deluno.Jobs.Contracts;

public sealed record ActivityEventItem(
    string Id,
    string Category,
    string Message,
    string? DetailsJson,
    string? RelatedJobId,
    string? RelatedEntityType,
    string? RelatedEntityId,
    DateTimeOffset CreatedUtc)
{
    /// <summary>What happened. A job type an older row still carries in it is worded as the job (#1086).</summary>
    public string Message { get; init; } = Deluno.Contracts.PlainFailure.Rewrite(Deluno.Contracts.JobTypeLabels.RewriteCodes(WithoutDecisionScope(Category, Message)), CreatedUtc);

    /// <summary>The event kind in plain words (#1086); screens show this, never <see cref="Category"/>.</summary>
    public string Label => Deluno.Contracts.ActivityEventLabels.For(Category);

    /// <summary>
    /// A decision explanation written before #1086 starts with its scope code
    /// ("series.season-search: Best feed candidate is ..."); the reason is what
    /// the row means, so the code is left off when it is read.
    /// </summary>
    private static string WithoutDecisionScope(string category, string message)
    {
        if (!string.Equals(category, "decision.explained", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(message))
        {
            return message;
        }

        var match = DecisionScopePrefix.Match(message);
        return match.Success ? message[match.Length..] : message;
    }

    private static readonly System.Text.RegularExpressions.Regex DecisionScopePrefix =
        new(@"^[a-z][a-z0-9_-]*(\.[a-z0-9_-]+)+: ", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}
