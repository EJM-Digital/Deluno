using Deluno.Contracts;

namespace Deluno.Platform;

/// <summary>
/// What Activity says after you import files you picked on the Review screen.
///
/// <para>The counts come straight from the import result. <c>ImportedCount</c>
/// is titles new to Deluno; everything else you selected was either already
/// tracked (and refreshed) or set aside, and the sentence says so rather than
/// calling all of it "added".</para>
/// </summary>
public static class ExistingLibraryImportActivity
{
    public static string DescribeSelected(string? libraryName, ExistingLibraryImportResult result)
    {
        var library = string.IsNullOrWhiteSpace(libraryName) ? "this library" : libraryName.Trim();
        var requested = result.RequestedCount;
        var imported = result.ImportedCount;
        var rest = Math.Max(0, requested - imported);

        var sentence = imported == requested
            ? $"Deluno added {imported} {Noun(imported, "title", "titles")} you selected from {library}."
            : imported > 0
                ? $"Deluno added {imported} of the {requested} items you selected from {library}; {rest} {Noun(rest, "was", "were")} already in Deluno or set aside."
                : $"Deluno checked the {requested} {Noun(requested, "item", "items")} you selected from {library}; nothing new was added, because {Noun(requested, "it was", "they were")} already in Deluno or set aside.";

        return result.Issues.Count == 0
            ? sentence
            : $"{sentence} {result.Issues.Count} {Noun(result.Issues.Count, "needs", "need")} a look in Review existing files.";
    }

    private static string Noun(int count, string one, string many) => count == 1 ? one : many;
}
