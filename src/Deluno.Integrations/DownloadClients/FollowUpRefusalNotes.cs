using System.Text.Json;
using System.Text.Json.Nodes;

namespace Deluno.Integrations.DownloadClients;

/// <summary>
/// #1470: a call Deluno sends after a grab that the client refused, and that can stop the import (the category or the save
/// folder), is kept in the dispatch's notes under <c>followUpRefusals</c> (call, what it was for, the client's own words, the
/// fix) so the download's row can say it, and is cleared when a later grab of the same dispatch has the same call taken.
/// </summary>
internal static class FollowUpRefusalNotes
{
    public const string Key = "followUpRefusals";
    public const string FindingKind = "follow-up-refused";

    /// <summary>The refusals worth keeping on the row, as notes: only those that can stop the import.</summary>
    public static string? ToJson(IEnumerable<DownloadClientFollowUpRefusal> refusals, string clientName)
    {
        var stored = refusals.Where(refusal => refusal.CanStopImport).ToArray();
        if (stored.Length == 0) return null;
        var array = new JsonArray();
        foreach (var refusal in stored)
        {
            array.Add(new JsonObject
            {
                ["call"] = refusal.Call,
                ["what"] = refusal.What,
                ["reason"] = refusal.Reason,
                ["fix"] = FixFor(refusal, clientName)
            });
        }

        return array.ToJsonString();
    }

    /// <summary>What to do about it, in plain words: a folder or a category, and then send it again.</summary>
    public static string FixFor(DownloadClientFollowUpRefusal refusal, string clientName)
        => refusal.What.StartsWith("the save folder", StringComparison.Ordinal)
            ? $"Make the folder in {clientName} (or pick one it can write to) or change the library's downloads folder, then send it again."
            : $"Create the category in {clientName} or change the library's category, then send it again.";

    /// <summary>The Health findings a download's notes carry, empty when they carry none.</summary>
    public static IReadOnlyList<DownloadHealthFinding> FindingsFrom(string? notesJson, string clientName)
    {
        if (string.IsNullOrWhiteSpace(notesJson)) return [];
        try
        {
            if (JsonNode.Parse(notesJson) is not JsonObject notes || notes[Key] is not JsonArray array) return [];
            return array.OfType<JsonObject>().Select(entry =>
            {
                var reason = (entry["reason"]?.GetValue<string>() ?? string.Empty).TrimEnd('.', ' ');
                return new DownloadHealthFinding(
                    Severity: "warning",
                    Kind: FindingKind,
                    Summary: $"{clientName} did not take {entry["what"]?.GetValue<string>()}: {reason}.",
                    Evidence: entry["call"]?.GetValue<string>() ?? string.Empty,
                    RecommendedAction: entry["fix"]?.GetValue<string>() ?? string.Empty,
                    CanSafelyRetry: false,
                    CanSafelyRemove: false);
            }).ToArray();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return [];
        }
    }
}
