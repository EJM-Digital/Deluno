using System.Text.RegularExpressions;

namespace Deluno.Persistence.Tests.Guards;

/// <summary>
/// #1412: a grab status is spelled in one place, <c>GrabStatuses</c>. Spelling "notFound" in one file and
/// "not_found" in another is how the cleanup pass stopped matching the store.
/// </summary>
public sealed class NoCodeSpellsAGrabStatusItselfTests
{
    private static readonly Regex Spelled = new("[\"']((notFound)|(not_found)|(circuitOpen))[\"']", RegexOptions.Compiled);

    private static readonly string[] Allowed =
    [
        "src/Deluno.Contracts/GrabStatuses.cs",
        // The word is an indexer's own health state there, not a grab status.
        "src/Deluno.Integrations/Search/FeedMediaSearchPlanner.cs"
    ];

    [Fact]
    public void No_source_file_compares_or_writes_a_grab_status_as_its_own_string()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Contains("/obj/") || relative.Contains("/bin/") || Allowed.Any(a => relative.StartsWith(a, StringComparison.Ordinal)))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (Spelled.IsMatch(lines[i]))
                {
                    offenders.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "Use GrabStatuses instead of spelling a grab status:\n" + string.Join("\n", offenders));
    }
}
