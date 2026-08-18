using System.Globalization;

namespace Deluno.Infrastructure.Storage;

/// <summary>
/// Parameter binding and value normalisation shared by every SQLite-backed
/// repository. These were private statics on
/// <c>SqlitePlatformSettingsRepository</c>; ADR-001 splits that class across
/// six bounded contexts, and all six need this plumbing. Import with
/// <c>using static</c> so call sites stay unqualified.
/// </summary>
public static class SqliteRecordHelpers
{
    public static void AddParameter(System.Data.Common.DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    public static DateTimeOffset ParseTimestamp(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public static string? NormalizeName(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    public static string NormalizeCsv(string? value)
    {
        return string.Join(
            ", ",
            (value ?? string.Empty)
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase));
    }
}
