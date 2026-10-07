using System.Text.Json;
using System.Text.Json.Nodes;

namespace Deluno.Infrastructure.Storage;

/// <summary>
/// What a search-history row keeps of the search's plan (#1083).
///
/// <para>A search compares every release the indexers return - 127 in one row on
/// the rig - and each one carries its own download link, matched rules and
/// scoring tree, so a row grew to hundreds of kilobytes and two shows filled
/// 1.8 GB. The screens show far less: the outcome, the chosen release, the first
/// few releases with their verdict and reasons, and how many were considered.
/// This keeps exactly that. The rows written from now on and the rows an upgrade
/// trims go through the same method, so both have one shape.</para>
///
/// <para>Idempotent: a document already trimmed carries <c>CandidateCount</c> and
/// is returned unchanged.</para>
/// </summary>
public static class SearchHistoryDetails
{
    /// <summary>How many releases keep their full verdict. The screens list six.</summary>
    public const int KeptCandidates = 6;

    private const int KeptComparisons = 6;

    private static readonly string[] DroppedCandidateFields =
    [
        "DownloadUrl", "MatchedCustomFormats", "PolicyVersion", "TorrentHashOrItemId",
        "IndexerProtocol", "ReleaseAgeHours", "Leechers", "Grabs", "QualityDelta",
        "CustomFormatScore", "SeederScore", "SizeScore", "EstimatedBitrateMbps"
    ];

    /// <summary>The trimmed document, or the input itself when there is nothing to trim.</summary>
    public static string? Trim(string? detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            return detailsJson;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(detailsJson);
        }
        catch (JsonException)
        {
            return detailsJson;
        }

        if (root is null || !TrimNode(root))
        {
            return detailsJson;
        }

        return root.ToJsonString();
    }

    private static bool TrimNode(JsonNode node)
    {
        var changed = false;
        if (node is JsonObject obj)
        {
            var candidatesKey = obj.Select(pair => pair.Key)
                .FirstOrDefault(key => string.Equals(key, "candidates", StringComparison.OrdinalIgnoreCase));
            if (candidatesKey is not null && obj[candidatesKey] is JsonArray candidates
                && !obj.Any(pair => string.Equals(pair.Key, "candidateCount", StringComparison.OrdinalIgnoreCase)))
            {
                TrimPlan(obj, candidatesKey, candidates);
                changed = true;
            }

            var comparisonsKey = obj.Select(pair => pair.Key)
                .FirstOrDefault(key => string.Equals(key, "replacementComparisons", StringComparison.OrdinalIgnoreCase));
            if (comparisonsKey is not null && obj[comparisonsKey] is JsonArray comparisons && comparisons.Count > KeptComparisons)
            {
                while (comparisons.Count > KeptComparisons)
                {
                    comparisons.RemoveAt(comparisons.Count - 1);
                }

                changed = true;
            }

            foreach (var child in obj.Select(pair => pair.Value).OfType<JsonObject>().ToArray())
            {
                changed |= TrimNode(child);
            }
        }

        return changed;
    }

    private static void TrimPlan(JsonObject plan, string candidatesKey, JsonArray candidates)
    {
        var camel = char.IsLower(candidatesKey[0]);
        var statusCounts = new JsonObject();
        foreach (var item in candidates.OfType<JsonObject>())
        {
            var status = (Read(item, "DecisionStatus") as JsonValue)?.ToString() ?? "unknown";
            statusCounts[status] = (statusCounts[status]?.GetValue<int>() ?? 0) + 1;
        }

        var total = candidates.Count;
        while (candidates.Count > KeptCandidates)
        {
            candidates.RemoveAt(candidates.Count - 1);
        }

        foreach (var item in candidates.OfType<JsonObject>())
        {
            SlimCandidate(item);
        }

        var bestKey = plan.Select(pair => pair.Key)
            .FirstOrDefault(key => string.Equals(key, "bestCandidate", StringComparison.OrdinalIgnoreCase));
        if (bestKey is not null && plan[bestKey] is JsonObject best)
        {
            // The held release's link is kept (#1159): approving it from Needs action sends exactly this release, and
            // a re-search could not be sure to find the same one. One link a row, only for a release not refused.
            var keptLink = Read(best, "DownloadUrl") is JsonValue link && !string.Equals((Read(best, "DecisionStatus") as JsonValue)?.ToString(), "rejected", StringComparison.OrdinalIgnoreCase)
                ? link.DeepClone()
                : null;
            SlimCandidate(best);
            if (keptLink is not null)
            {
                best[camel ? "downloadUrl" : "DownloadUrl"] = keptLink;
            }
        }

        plan[camel ? "candidateCount" : "CandidateCount"] = total;
        plan[camel ? "candidateStatusCounts" : "CandidateStatusCounts"] = statusCounts;
    }

    private static void SlimCandidate(JsonObject candidate)
    {
        foreach (var field in DroppedCandidateFields)
        {
            RemoveIgnoringCase(candidate, field);
        }

        // The screens only ask whether the release was judged against a plan, and how it
        // came out; the per-family scoring tree is the bulk (3 KB a release).
        foreach (var name in new[] { "PreferenceEvaluation", "PreferenceComparison" })
        {
            var key = candidate.Select(pair => pair.Key)
                .FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
            if (key is null || candidate[key] is not JsonObject evaluation)
            {
                continue;
            }

            foreach (var inner in evaluation.Select(pair => pair.Key).ToArray())
            {
                // A comparison carries the two full evaluations it compared (3 KB each).
                if (inner.Equals("Families", StringComparison.OrdinalIgnoreCase)
                    || inner.Equals("PlanVersion", StringComparison.OrdinalIgnoreCase)
                    || inner.Equals("PlanHash", StringComparison.OrdinalIgnoreCase)
                    || inner.Equals("Current", StringComparison.OrdinalIgnoreCase)
                    || inner.Equals("Candidate", StringComparison.OrdinalIgnoreCase))
                {
                    evaluation.Remove(inner);
                }
            }
        }
    }

    private static JsonNode? Read(JsonObject obj, string name)
        => obj.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static void RemoveIgnoringCase(JsonObject obj, string name)
    {
        var key = obj.Select(pair => pair.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        if (key is not null)
        {
            obj.Remove(key);
        }
    }
}
