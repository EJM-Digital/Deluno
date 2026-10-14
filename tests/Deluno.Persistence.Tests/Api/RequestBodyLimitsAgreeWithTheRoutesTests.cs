using System.Text.Json;
using System.Text.RegularExpressions;
using Deluno.Platform.Processing;
using Deluno.Worker;
using Microsoft.AspNetCore.Http;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1279 and the processor contract: the body-limit middleware runs before every route, so a limit lower than a route's own
/// length check refuses a legitimate request the route would have taken. CI run 38551697502 found one: a 4,050-character
/// output file path (the contract allows 4,096) was refused at 4,000. These tests pin the path limit for path-shaped members
/// and sweep every handler's own length check against the middleware's limit for the same member.
/// </summary>
public sealed class RequestBodyLimitsAgreeWithTheRoutesTests
{
    private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement;

    [Theory]
    [InlineData("outputPath")]
    [InlineData("outputFiles")]
    [InlineData("sourcePaths")]
    [InlineData("downloadsFolder")]
    [InlineData("libraryFolders")]
    [InlineData("watchedDirectory")]
    [InlineData("scanDirectories")]
    [InlineData("rootFolder")]
    [InlineData("libraryRoots")]
    [InlineData("subtitleFile")]
    [InlineData("File")]
    public void A_path_shaped_member_may_hold_the_contracts_longest_path(string member)
        => Assert.Equal(RequestBodyLimits.PathLength, RequestBodyLimits.TextLimit(member));

    [Theory]
    [InlineData("profile")]
    [InlineData("qualityProfile")]
    public void A_name_that_only_ends_in_file_letters_is_not_a_path(string member)
        => Assert.NotEqual(RequestBodyLimits.PathLength, RequestBodyLimits.TextLimit(member));

    [Fact]
    public void A_4050_character_path_in_a_list_of_files_is_not_refused_and_one_over_the_limit_is_named()
    {
        var fine = new string('x', 4_050);
        Assert.Null(RequestBodyLimits.FirstTooLong(Body($"{{\"outputFiles\":[\"{fine}\"]}}")));

        var message = RequestBodyLimits.FirstTooLong(Body($"{{\"outputFiles\":[\"{new string('x', 4_097)}\"]}}"));
        Assert.NotNull(message);
        Assert.Contains("4096", message);
        Assert.Contains("Each output file can be at most 4096 characters", message);
    }

    [Fact]
    public void A_single_path_that_is_too_long_reads_naturally()
    {
        var message = RequestBodyLimits.FirstTooLong(Body($"{{\"outputPath\":\"{new string('x', 4_097)}\"}}"));
        Assert.Equal("The output path is too long. It can be at most 4096 characters.", message);
    }

    [Fact]
    public void The_processor_events_route_answers_its_own_files_check_and_the_middleware_leaves_that_member_to_it()
    {
        var tooLong = $"{{\"status\":\"completed\",\"outputFiles\":[\"{new string('x', ProcessorEventLimits.MaxPathLength + 1)}\"]}}";
        var events = new PathString("/api/integrations/processors/events");

        Assert.Null(RequestBodyLimits.FirstTooLong(Body(tooLong), events));
        // Any other route still has the limit applied to the same member.
        Assert.NotNull(RequestBodyLimits.FirstTooLong(Body(tooLong), new PathString("/api/something/else")));
        // The path the report names for the whole output is not the route's own check, so the middleware keeps it.
        var outputPath = $"{{\"outputPath\":\"{new string('x', ProcessorEventLimits.MaxPathLength + 1)}\"}}";
        Assert.NotNull(RequestBodyLimits.FirstTooLong(Body(outputPath), events));
    }

    [Fact]
    public void A_member_a_route_checks_itself_is_never_limited_lower_in_the_middleware()
    {
        foreach (var (path, member) in RequestBodyLimits.RouteOwnedLengths)
        {
            var limit = RequestBodyLimits.TextLimit(member);
            Assert.True(limit is null || limit >= ProcessorEventLimits.MaxPathLength, $"{path} {member}");
        }
    }

    /// <summary>
    /// Text members a handler checks for length itself, found by reading the source. Each is a request member whose own limit
    /// is the number shown; the middleware's limit for the same member name must be no lower, or the middleware refuses what the
    /// route accepts. A new length check in any handler fails the sweep until it is listed here (or in <see cref="NotRequestMembers"/>).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, int> RouteChecks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["Category"] = 200,
        ["PreferIndexerFlags"] = 500,
        ["RequiredWords"] = 500,
        ["IgnoredWords"] = 500,
        ["MultiLanguages"] = 200,
        ["ReleaseName"] = 500,
        ["CurrentReleaseName"] = 500,
        ["tagName"] = 100,
        ["tag"] = 100,
        ["Term"] = 100,
        ["name"] = 100,
        ["detailsJson"] = 65_536,
        ["file"] = ProcessorEventLimits.MaxPathLength
    };

    /// <summary>Length checks that are not on a text member of a request body (a helper's argument, a header, a downloaded file, a count).</summary>
    private static readonly string[] NotRequestMembers =
    [
        "value", "text", "entry", "yaml", "page", "historyItems", "key", "line", "single", "body", "id", "segment", "path", "source", "watched"
    ];

    private static readonly Regex Check = new(
        @"\b(?<member>\w+)\??\.(?:Trim\(\)\.)?Length\s*>\s*(?<limit>[\w.]+)|(?<member2>\w+) is \{ Length: > (?<limit2>[\w.]+) \}",
        RegexOptions.Compiled);

    [Fact]
    public void No_route_checks_a_text_member_for_more_characters_than_the_middleware_lets_through()
    {
        var root = RepositoryRoot();
        var constants = new Dictionary<string, int>(StringComparer.Ordinal);
        var files = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.EndsWith("RequestBodyLimits.cs", StringComparison.Ordinal))
            .ToArray();
        foreach (var file in files)
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"const int (?<name>\w+)\s*=\s*(?<value>[\d_]+);"))
            {
                constants[m.Groups["name"].Value] = int.Parse(m.Groups["value"].Value.Replace("_", ""));
            }
        }

        var conflicts = new List<string>();
        var unknown = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            foreach (Match m in Check.Matches(File.ReadAllText(file)))
            {
                var member = m.Groups["member"].Success ? m.Groups["member"].Value : m.Groups["member2"].Value;
                var limitText = m.Groups["limit"].Success ? m.Groups["limit"].Value : m.Groups["limit2"].Value;
                if (!TryNumber(limitText, constants, out var limit))
                {
                    continue;
                }

                if (limit < 10 || NotRequestMembers.Contains(member, StringComparer.Ordinal))
                {
                    continue;
                }

                seen.Add(member);
                if (!RouteChecks.ContainsKey(member))
                {
                    unknown.Add($"{relative}: {member} > {limit}");
                    continue;
                }

                var middleware = RequestBodyLimits.TextLimit(member);
                if (middleware is { } max && max < limit)
                {
                    conflicts.Add($"{relative}: {member} is checked at {limit} but the middleware refuses it at {max}");
                }
            }
        }

        Assert.True(unknown.Count == 0, "A handler has a length check this test does not know. Add it to RouteChecks (a request member) or NotRequestMembers (anything else):\n" + string.Join("\n", unknown));
        Assert.True(conflicts.Count == 0, "The middleware refuses what a route accepts:\n" + string.Join("\n", conflicts));
        Assert.Contains("ReleaseName", seen, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Category", seen, StringComparer.OrdinalIgnoreCase);

        // The listed limits are the ones the middleware must honour, whatever the source says today.
        foreach (var (member, routeLimit) in RouteChecks)
        {
            var middleware = RequestBodyLimits.TextLimit(member);
            Assert.True(middleware is null || middleware >= routeLimit, $"{member}: route {routeLimit}, middleware {middleware}");
        }
    }

    private static bool TryNumber(string text, Dictionary<string, int> constants, out int value)
    {
        if (int.TryParse(text.Replace("_", ""), out value)) return true;
        return constants.TryGetValue(text[(text.LastIndexOf('.') + 1)..], out value);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Deluno.slnx"))) return directory.FullName;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
