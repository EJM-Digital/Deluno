using System.Text.Json;
using System.Text.Json.Serialization;

namespace Deluno.Retention;

/// <summary>
/// One line of a rule (#494, DESIGN-008): a condition, or a named block of
/// lines that works as brackets.
/// </summary>
/// <param name="Type"><c>condition</c> or <c>block</c>.</param>
/// <param name="Op">
/// <c>keep</c> ("Keep only…", AND): narrows the titles so far to those this holds
/// for. <c>include</c> ("Also include…", OR): adds the titles from the whole
/// library this holds for.
/// </param>
/// <param name="Condition">A condition's <c>field:operator:value</c>, the same shape the library filters use.</param>
/// <param name="Name">A block's name, shown on its bracket.</param>
/// <param name="Steps">A block's own lines, run from the whole library.</param>
public sealed record RetentionStep(
    string Type,
    string Op,
    string? Condition = null,
    string? Name = null,
    IReadOnlyList<RetentionStep>? Steps = null)
{
    public const string ConditionType = "condition";
    public const string BlockType = "block";
    public const string Keep = "keep";
    public const string Include = "include";

    public static RetentionStep KeepOnly(string condition) => new(ConditionType, Keep, condition);
}

public static class RetentionStepList
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(IReadOnlyList<RetentionStep> steps) => JsonSerializer.Serialize(steps, Json);

    public static IReadOnlyList<RetentionStep> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<RetentionStep>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// A first-version rule's flat conditions, as the steps that mean the same:
    /// every one had to hold, which is a run of Keep only.
    /// </summary>
    public static IReadOnlyList<RetentionStep> FromConditions(IEnumerable<string> conditions)
        => conditions.Where(condition => !string.IsNullOrWhiteSpace(condition)).Select(condition => RetentionStep.KeepOnly(condition.Trim())).ToArray();

    /// <summary>Every condition in the rule, blocks opened up.</summary>
    public static IEnumerable<string> Conditions(IEnumerable<RetentionStep> steps)
    {
        foreach (var step in steps)
        {
            if (step.Type == RetentionStep.BlockType)
            {
                foreach (var inner in Conditions(step.Steps ?? []))
                {
                    yield return inner;
                }
            }
            else if (!string.IsNullOrWhiteSpace(step.Condition))
            {
                yield return step.Condition;
            }
        }
    }

    /// <summary>The shape problems a person can fix: empty rules, empty blocks, unknown words.</summary>
    public static IEnumerable<string> ShapeProblems(IReadOnlyList<RetentionStep> steps, string where = "The rule")
    {
        if (steps.Count == 0)
        {
            yield return $"{where} has no steps. A rule with none would select everything.";
            yield break;
        }

        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            if (step.Op is not (RetentionStep.Keep or RetentionStep.Include))
            {
                yield return $"Step {index + 1} of {where.ToLowerInvariant()} must say Keep only or Also include.";
            }

            if (step.Type == RetentionStep.BlockType)
            {
                var name = string.IsNullOrWhiteSpace(step.Name) ? $"Block {index + 1}" : $"The block “{step.Name}”";
                foreach (var problem in ShapeProblems(step.Steps ?? [], name))
                {
                    yield return problem;
                }
            }
            else if (step.Type != RetentionStep.ConditionType)
            {
                yield return $"Step {index + 1} of {where.ToLowerInvariant()} is neither a condition nor a block.";
            }
            else if (string.IsNullOrWhiteSpace(step.Condition))
            {
                yield return $"Step {index + 1} of {where.ToLowerInvariant()} has no condition.";
            }
        }
    }
}
