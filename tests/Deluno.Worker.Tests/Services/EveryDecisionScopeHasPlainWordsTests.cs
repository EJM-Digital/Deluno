using System.Text.RegularExpressions;
using Deluno.Contracts;
using Deluno.Jobs.Decisions;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// #1086: the decision lists on the film and show pages printed each decision's
/// raw scope (<c>series.season-search</c>), and Activity's decision rows began
/// with it. Every scope a writer records now has its plain-words label in
/// <see cref="DecisionScopeLabels"/>; this reads every place a decision is
/// recorded, so a scope added without a label fails here rather than reaching a
/// screen as a code.
/// </summary>
public sealed class EveryDecisionScopeHasPlainWordsTests
{
    [Fact]
    public void Every_scope_a_decision_is_recorded_with_has_a_plain_words_label()
    {
        var scopes = RecordedScopes();
        Assert.True(scopes.Count >= 8, $"Only {scopes.Count} decision scopes were found; the scan has stopped reading the source.");

        var missing = scopes.Where(scope => !DecisionScopeLabels.IsKnown(scope)).OrderBy(scope => scope).ToArray();
        Assert.True(missing.Length == 0, $"These decision scopes have no label in DecisionScopeLabels: {string.Join(", ", missing)}");
    }

    [Fact]
    public void Every_label_is_plain_words_and_never_the_code()
    {
        foreach (var scope in DecisionScopeLabels.KnownScopes)
        {
            var label = DecisionScopeLabels.For(scope);
            Assert.True(char.IsUpper(label[0]), $"{scope}'s label '{label}' should start with a capital.");
            Assert.DoesNotMatch(@"[a-z]+[-_.][a-z]+", label);
        }

        Assert.Equal("A decision", DecisionScopeLabels.For("zzz.brand-new"));
    }

    [Fact]
    public void A_decision_item_carries_its_scope_in_words()
    {
        var item = new DecisionExplanationItem(
            "d1", DateTimeOffset.UnixEpoch, "series.season-search", "matched", "Best feed candidate is X.",
            new Dictionary<string, string?>(), "Grabbed.", [], null, "series", "s1");

        Assert.Equal("Searching for a season", item.ScopeLabel);
        var json = System.Text.Json.JsonSerializer.Serialize(item, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Contains("\"scopeLabel\":\"Searching for a season\"", json);
    }

    private static HashSet<string> RecordedScopes()
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var files = Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                           !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            if (!text.Contains("DecisionExplanationPayload(", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match match in Regex.Matches(text, "Scope: \"(?<v>[a-z][a-z0-9._-]*)\""))
            {
                found.Add(match.Groups["v"].Value);
            }

            // Built from the entity type ("movie" or "series"): "{entityType}.grab.force" and ".grab.manual".
            // Only on a Scope: line; the same file also builds Activity kinds this way.
            foreach (var line in text.Split('\n').Where(line => line.Contains("Scope:", StringComparison.Ordinal)))
            {
                foreach (Match match in Regex.Matches(line, "\\$\"\\{entityType\\}(?<v>\\.[a-z0-9._-]+)\""))
                {
                    found.Add("movie" + match.Groups["v"].Value);
                    found.Add("series" + match.Groups["v"].Value);
                }
            }
        }

        return found;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Deluno.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
