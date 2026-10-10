using System.Reflection;
using System.Text.RegularExpressions;
using Deluno.Contracts;
using Deluno.Platform.Contracts;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// #1086: Activity, System and the film and show History tables printed the
/// raw event code (<c>download.vanished.recovered</c>). Every kind of event
/// now has its plain-words label in <see cref="ActivityEventLabels"/>, and
/// this reads the source of every place that writes an activity event: a kind
/// added to a writer without a label fails here rather than reaching a
/// screen as a code.
/// </summary>
public sealed class EveryActivityKindHasPlainWordsTests
{
    private static readonly string[] Writers =
    [
        "RecordActivityAsync",
        "InsertActivityAsync",
        "CreateRecoveryActivity"
    ];

    // Arguments that hand on an event some other writer already made.
    private static readonly HashSet<string> PassedOn = new(StringComparer.Ordinal)
    {
        "activity", "[activity]", "activities", "recoveryActivities"
    };

    // A category chosen at run time from a value, not written as a string in
    // the call. Each one lists what it can produce; a NEW run-time site fails
    // the test until it is listed here, so it cannot slip past unlabelled.
    private static readonly Dictionary<string, string[]> RunTimeKinds = new(StringComparer.Ordinal)
    {
        // SqliteJobStore.DescribeDispatch
        ["activityCategory"] =
        [
            "download.dispatch.failed", "download.dispatch.recorded",
            "download.dispatch.planned", "download.dispatch.paused"
        ],
        // DownloadDispatchPollingService, following a grab found in the client
        ["category"] = ["download.grab.duplicate", "download.grab.foundElsewhere"],
        // ProcessorOutcomeDelivery.Describe
        ["kind"] =
        [
            "processing.outcome-reported", "processing.outcome-delayed", "processing.outcome-not-reported", "processing.outcome-kept",
            "processing.outcome-never-received", "processing.outcome-superseded"
        ]
    };

    [Fact]
    public void Every_kind_of_event_any_writer_can_record_has_a_plain_words_label()
    {
        var categories = WrittenCategories(out var problems);

        Assert.True(problems.Count == 0, string.Join(" | ", problems));
        Assert.True(categories.Count > 150, $"Only {categories.Count} kinds were found; the scan has stopped reading the writers.");

        var missing = categories.Where(category => !ActivityEventLabels.IsKnown(category)).OrderBy(category => category).ToArray();
        Assert.True(missing.Length == 0, $"These activity kinds have no label in ActivityEventLabels: {string.Join(", ", missing)}");
    }

    [Fact]
    public void Every_label_is_plain_words_and_never_a_code()
    {
        foreach (var category in ActivityEventLabels.KnownCategories)
        {
            var label = ActivityEventLabels.For(category);
            Assert.False(string.IsNullOrWhiteSpace(label), $"{category} has an empty label.");
            Assert.True(char.IsUpper(label[0]), $"{category}'s label '{label}' should start with a capital.");
            Assert.DoesNotContain(".", label.TrimEnd('.'));
        }
    }

    [Fact]
    public void A_kind_nobody_has_labelled_still_reads_as_words()
    {
        Assert.Equal("Download activity", ActivityEventLabels.For("download.something.new"));
        Assert.Equal("Activity", ActivityEventLabels.For("zzz.unknown"));
        Assert.Equal("Activity", ActivityEventLabels.For(null));
    }

    [Fact]
    public void An_activity_event_sent_to_a_screen_carries_its_label()
    {
        var item = new Deluno.Jobs.Contracts.ActivityEventItem(
            "e1", "download.vanished.recovered", "Found the files.", null, null, null, null, DateTimeOffset.UnixEpoch);

        var json = System.Text.Json.JsonSerializer.Serialize(item, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Contains("\"label\":\"Found its files after it left the client\"", json);
        Assert.Contains("\"category\":\"download.vanished.recovered\"", json);
    }

    [Fact]
    public void Labels_match_case_the_way_the_writers_spell_them()
    {
        Assert.Equal("Found its files after it left the client", ActivityEventLabels.For("download.vanished.recovered"));
        Assert.Equal("Waiting for the processor", ActivityEventLabels.For("processing.waiting"));
        Assert.Equal(ActivityEventLabels.For("library.search.queuePause"), ActivityEventLabels.For("LIBRARY.SEARCH.QUEUEPAUSE"));
    }

    private static HashSet<string> WrittenCategories(out List<string> problems)
    {
        problems = [];
        var root = RepositoryRoot();
        var files = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                           !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(path => (Path: path, Text: File.ReadAllText(path)))
            .ToArray();

        var constants = new Dictionary<string, Dictionary<string, string>>();
        foreach (var (path, text) in files)
        {
            var own = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match match in Regex.Matches(text, "(?:const|static readonly) string (?<name>\\w+)\\s*=\\s*\"(?<value>[^\"]*)\""))
            {
                own[match.Groups["name"].Value] = match.Groups["value"].Value;
            }

            constants[path] = own;
        }

        var handoffStatuses = typeof(ProcessorHandoffStatuses)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();

        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, text) in files)
        {
            foreach (var writer in Writers)
            {
                foreach (Match call in Regex.Matches(text, $@"\b{writer}\("))
                {
                    var arguments = SplitArguments(text, call.Index + call.Length);
                    var expression = CategoryArgument(arguments);
                    if (expression is null || PassedOn.Contains(expression) || IsDeclaration(expression))
                    {
                        continue;
                    }

                    var literals = Regex.Matches(expression, "\\$?\"(?<text>[^\"]*)\"")
                        .Select(match => match.Groups["text"].Value)
                        .ToArray();

                    if (literals.Length > 0)
                    {
                        foreach (var literal in literals)
                        {
                            foreach (var expanded in Expand(literal, handoffStatuses))
                            {
                                found.Add(expanded);
                            }
                        }

                        continue;
                    }

                    var name = expression.Split('.')[^1];
                    var qualifier = expression.Contains('.') ? expression.Split('.')[0] : null;
                    var resolved = Resolve(files, constants, path, qualifier, name);
                    if (resolved is not null)
                    {
                        found.Add(resolved);
                    }
                    else if (RunTimeKinds.TryGetValue(expression, out var kinds))
                    {
                        foreach (var kind in kinds)
                        {
                            found.Add(kind);
                        }
                    }
                    else
                    {
                        problems.Add($"{Path.GetFileName(path)} records an activity kind from '{expression}', which this test cannot read. List what it can produce in RunTimeKinds.");
                    }
                }
            }
        }

        return found;
    }

    private static IEnumerable<string> Expand(string literal, string[] handoffStatuses)
    {
        if (!literal.Contains('{'))
        {
            yield return literal;
            yield break;
        }

        if (literal.Contains("{status}", StringComparison.Ordinal))
        {
            foreach (var status in handoffStatuses)
            {
                yield return literal.Replace("{status}", status, StringComparison.Ordinal);
            }

            yield break;
        }

        // {entityType} / {title.EntityType}: a film or a show.
        foreach (var entity in new[] { "movie", "series" })
        {
            yield return Regex.Replace(literal, @"\{[\w.]*[eE]ntityType\}", entity);
        }
    }

    private static string? Resolve(
        (string Path, string Text)[] files,
        Dictionary<string, Dictionary<string, string>> constants,
        string callerPath,
        string? qualifier,
        string name)
    {
        if (qualifier is null)
        {
            return constants[callerPath].TryGetValue(name, out var local) ? local : null;
        }

        foreach (var (path, text) in files)
        {
            if (Regex.IsMatch(text, $@"\bclass {Regex.Escape(qualifier)}\b") && constants[path].TryGetValue(name, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool IsDeclaration(string expression) =>
        Regex.IsMatch(expression, @"^[\w<>\[\]?.]+\s+\w+$");

    /// <summary>The argument that carries the category: named, or the first (after any connection and transaction).</summary>
    private static string? CategoryArgument(List<string> arguments)
    {
        foreach (var argument in arguments)
        {
            if (argument.StartsWith("category:", StringComparison.Ordinal))
            {
                return argument["category:".Length..].Trim();
            }
        }

        if (arguments.Count == 0)
        {
            return null;
        }

        var first = arguments[0];
        if (first == "connection" || first.EndsWith(" connection", StringComparison.Ordinal))
        {
            return arguments.Count > 2 ? arguments[2] : null;
        }

        return first;
    }

    /// <summary>Balanced-parenthesis argument split; string literals may hold commas.</summary>
    private static List<string> SplitArguments(string text, int start)
    {
        var arguments = new List<string>();
        var depth = 0;
        var inString = false;
        var from = start;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
            }
            else if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0)
                {
                    arguments.Add(Regex.Replace(text[from..i], @"\s+", " ").Trim());
                    break;
                }

                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                arguments.Add(Regex.Replace(text[from..i], @"\s+", " ").Trim());
                from = i + 1;
                if (arguments.Count >= 3)
                {
                    break;
                }
            }
        }

        return arguments;
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
