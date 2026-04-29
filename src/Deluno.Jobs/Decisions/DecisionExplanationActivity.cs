using System.Text.Json;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;

namespace Deluno.Jobs.Decisions;

public static class DecisionExplanationActivity
{
    public const string Category = "decision.explained";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static Task<ActivityEventItem> RecordDecisionAsync(
        this IActivityFeedRepository repository,
        DecisionExplanationPayload payload,
        string? relatedJobId,
        string? relatedEntityType,
        string? relatedEntityId,
        CancellationToken cancellationToken)
    {
        var reason = string.IsNullOrWhiteSpace(payload.Reason)
            ? "Deluno made this decision from recorded inputs, but no reason was provided."
            : payload.Reason.Trim();
        var normalized = payload with { Reason = reason };
        return repository.RecordActivityAsync(
            Category,
            $"{normalized.Scope}: {reason}",
            JsonSerializer.Serialize(normalized, JsonOptions),
            relatedJobId,
            relatedEntityType,
            relatedEntityId,
            cancellationToken);
    }

    public static DecisionExplanationItem? FromActivity(ActivityEventItem activity)
    {
        if (!string.Equals(activity.Category, Category, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(activity.DetailsJson))
        {
            return null;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<DecisionExplanationPayload>(activity.DetailsJson, JsonOptions);
            if (payload is null || string.IsNullOrWhiteSpace(payload.Scope) || string.IsNullOrWhiteSpace(payload.Reason))
            {
                return null;
            }

            return new DecisionExplanationItem(
                Id: activity.Id,
                OccurredUtc: activity.CreatedUtc,
                Scope: payload.Scope,
                Status: payload.Status,
                Reason: payload.Reason,
                Inputs: payload.Inputs,
                Outcome: payload.Outcome,
                Alternatives: payload.Alternatives,
                RelatedJobId: activity.RelatedJobId,
                RelatedEntityType: activity.RelatedEntityType,
                RelatedEntityId: activity.RelatedEntityId);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record DecisionExplanationPayload(
    string Scope,
    string Status,
    string Reason,
    IReadOnlyDictionary<string, string?> Inputs,
    string Outcome,
    IReadOnlyList<DecisionAlternativeExplanation> Alternatives);

public sealed record DecisionExplanationItem(
    string Id,
    DateTimeOffset OccurredUtc,
    string Scope,
    string Status,
    string Reason,
    IReadOnlyDictionary<string, string?> Inputs,
    string Outcome,
    IReadOnlyList<DecisionAlternativeExplanation> Alternatives,
    string? RelatedJobId,
    string? RelatedEntityType,
    string? RelatedEntityId);

public sealed record DecisionAlternativeExplanation(
    string Name,
    string Status,
    string Reason,
    int? Score = null);
