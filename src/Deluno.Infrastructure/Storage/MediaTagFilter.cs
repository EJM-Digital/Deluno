using System.Data.Common;

namespace Deluno.Infrastructure.Storage;

/// <summary>
/// A "narrow to these tags" clause shared by every caller that filters a
/// catalogue-local <c>media_tags</c> join by name — the calendar feed (#786)
/// and the saved-filter tag condition both ask the identical question: does
/// this title carry at least one of the given tags. One builder, so a caller
/// cannot quietly reinvent the match rule (case, whitespace) the other already
/// settled.
/// </summary>
public static class MediaTagFilter
{
    /// <summary>
    /// Builds a SQL boolean expression: <c>1 = 1</c> when no tags were asked
    /// for (nothing to narrow), otherwise an <c>EXISTS</c> matching any of the
    /// given tag names against <paramref name="idColumn"/>. Adds one bound
    /// parameter per name to <paramref name="command"/>.
    /// </summary>
    public static string BuildClause(string idColumn, IReadOnlyList<string>? tagNames, DbCommand command)
    {
        var names = (tagNames ?? [])
            .Select(name => name.Trim())
            .Where(name => name.Length > 0)
            .ToArray();

        if (names.Length == 0)
        {
            return "1 = 1";
        }

        var matches = new List<string>();
        for (var index = 0; index < names.Length; index++)
        {
            var parameterName = $"@calendarTag{index}";
            var parameter = command.CreateParameter();
            parameter.ParameterName = parameterName;
            parameter.Value = names[index].ToLowerInvariant();
            command.Parameters.Add(parameter);
            matches.Add($"lower(trim(mt.tag_name)) = {parameterName}");
        }

        return $"EXISTS (SELECT 1 FROM media_tags mt WHERE mt.media_id = {idColumn} AND ({string.Join(" OR ", matches)}))";
    }
}
