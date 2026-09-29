namespace Deluno.Contracts;

/// <summary>How a bulk tag operation combines the tags a caller sent with the
/// tags a title already carries. Shared by the movies and series bulk-tag
/// endpoints so "add" cannot silently regress into "replace" in one engine
/// while the other keeps merge semantics.</summary>
public enum BulkTagMode
{
    /// <summary>Keep every existing tag and add the requested ones (default).</summary>
    Add,

    /// <summary>Remove only the requested tags; every other existing tag stays.</summary>
    Remove,

    /// <summary>Replace the full tag set with exactly the requested tags.</summary>
    Replace,
}

/// <summary>
/// Combines a title's current tag assignments with a bulk request's requested
/// tags according to a <see cref="BulkTagMode"/>. Pure and storage-agnostic so
/// the movies and series bulk-tags endpoints can share one implementation
/// instead of each hand-rolling add/remove merge logic.
/// </summary>
public static class MediaTagBulkOperations
{
    public static bool TryParseMode(string? raw, out BulkTagMode mode)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            mode = BulkTagMode.Add;
            return true;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case "add":
                mode = BulkTagMode.Add;
                return true;
            case "remove":
                mode = BulkTagMode.Remove;
                return true;
            case "replace":
                mode = BulkTagMode.Replace;
                return true;
            default:
                mode = default;
                return false;
        }
    }

    public static IReadOnlyList<MediaTagAssignment> Apply(
        BulkTagMode mode,
        IReadOnlyList<MediaTagAssignment> currentAssignments,
        IReadOnlyList<MediaTagAssignment> requestedAssignments)
    {
        switch (mode)
        {
            case BulkTagMode.Replace:
                return requestedAssignments;

            case BulkTagMode.Remove:
                var removedNames = requestedAssignments
                    .Select(item => item.Name.Trim())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                return currentAssignments
                    .Where(item => !removedNames.Contains(item.Name.Trim()))
                    .ToArray();

            case BulkTagMode.Add:
            default:
                // Requested assignments come first so a freshly resolved managed
                // tag id wins over a stale legacy id already on the title.
                return requestedAssignments
                    .Concat(currentAssignments)
                    .GroupBy(item => item.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToArray();
        }
    }
}
