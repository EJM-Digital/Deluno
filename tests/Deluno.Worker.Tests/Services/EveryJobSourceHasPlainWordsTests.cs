using System.Text.RegularExpressions;
using Deluno.Contracts;
using Deluno.Jobs.Contracts;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// #1086: System and Activity printed the raw job source (<c>grab-rejected</c>,
/// <c>queue-derived</c>) under "Started by". Every source an enqueue site passes
/// now has its plain-words label in <see cref="JobSourceLabels"/>; this reads the
/// source of every enqueue site, so a source added without a label fails here
/// rather than reaching a screen as a code.
/// </summary>
public sealed class EveryJobSourceHasPlainWordsTests
{
    [Fact]
    public void Every_source_an_enqueue_site_passes_has_a_plain_words_label()
    {
        var sources = EnqueuedSources();
        Assert.True(sources.Count >= 25, $"Only {sources.Count} job sources were found; the scan has stopped reading the source.");

        var missing = sources.Where(source => !JobSourceLabels.IsKnown(source)).OrderBy(source => source).ToArray();
        Assert.True(missing.Length == 0, $"These job sources have no label in JobSourceLabels: {string.Join(", ", missing)}");
    }

    [Fact]
    public void Every_label_is_plain_words_and_never_the_code()
    {
        foreach (var source in JobSourceLabels.KnownSources)
        {
            var label = JobSourceLabels.For(source);
            Assert.True(char.IsUpper(label[0]), $"{source}'s label '{label}' should start with a capital.");
            if (source.Contains('-') || source.Contains('.') || source.Contains('_'))
            {
                Assert.DoesNotContain(source, label, StringComparison.OrdinalIgnoreCase);
            }

            Assert.DoesNotMatch(@"[a-z]+[-_.][a-z]+", label);
        }
    }

    [Fact]
    public void The_common_sources_read_the_way_the_screens_promise()
    {
        Assert.Equal("You", JobSourceLabels.For("manual"));
        Assert.Equal("Deluno", JobSourceLabels.For("system"));
        Assert.Equal("An outside app", JobSourceLabels.For("webhook"));
        Assert.Equal("A refused release", JobSourceLabels.For("grab-rejected"));
        Assert.Equal("The download health check", JobSourceLabels.For("download-health"));
    }

    [Fact]
    public void A_source_nobody_has_labelled_reads_Deluno_not_the_code()
    {
        Assert.Equal("Deluno", JobSourceLabels.For("zzz-brand-new"));
        Assert.Equal("Deluno", JobSourceLabels.For(null));
        Assert.Equal("Deluno", JobSourceLabels.For(" "));
    }

    [Fact]
    public void A_job_sent_to_a_screen_carries_its_source_label()
    {
        var now = DateTimeOffset.UnixEpoch;
        var job = new JobQueueItem("j1", "series.search.season", "grab-rejected", "queued", null, 0, now, now, null, null, null, null, null, null, null, null, null, 3, null, null);

        var json = System.Text.Json.JsonSerializer.Serialize(job, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Contains("\"sourceLabel\":\"A refused release\"", json);
    }

    private static HashSet<string> EnqueuedSources()
    {
        var root = RepositoryRoot();
        var found = new HashSet<string>(StringComparer.Ordinal);
        const string Code = "(?<v>[a-z][a-z0-9._-]*)";
        var files = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                           !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                           !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"));

        foreach (var path in files)
        {
            var text = File.ReadAllText(path);

            foreach (Match request in Regex.Matches(text, @"new (?:EnqueueJobRequest|JobQueueItem)\("))
            {
                var window = text.Substring(request.Index + request.Length, Math.Min(500, text.Length - request.Index - request.Length));

                // Named: Source: "x"
                var named = Regex.Match(window, $@"\bSource:\s*""{Code}""");
                if (named.Success)
                {
                    found.Add(named.Groups["v"].Value);
                    continue;
                }

                // A conditional such as Source: kind == MediaKind.Series ? "tv" : "movies"
                var conditional = Regex.Match(window, @"\bSource:\s*[^,\n]*\?\s*""(?<a>[a-z0-9._-]+)""\s*:\s*""(?<b>[a-z0-9._-]+)""");
                if (conditional.Success)
                {
                    found.Add(conditional.Groups["a"].Value);
                    found.Add(conditional.Groups["b"].Value);
                    continue;
                }

                // Positional: the second argument, when it is a plain string.
                var positional = Regex.Match(window, $@"^\s*[^,\n]+,\s*""{Code}""\s*,");
                if (positional.Success)
                {
                    found.Add(positional.Groups["v"].Value);
                }
            }

            // AnotherCopySearch.EnqueueAsync(scheduler, dispatch, libraryName, "who", key, ct) and QueueAsync(library, handoff, path, "who", ct)
            foreach (Match match in Regex.Matches(text, $@"AnotherCopySearch\.EnqueueAsync\([^;]*?,\s*""{Code}"",\s*[^,;]+,\s*cancellationToken"))
            {
                found.Add(match.Groups["v"].Value);
            }

            foreach (Match match in Regex.Matches(text, $@"\.QueueAsync\(library, handoff, outputPath, ""{Code}"""))
            {
                found.Add(match.Groups["v"].Value);
            }
        }

        // Libraries hand their own media type over as the source (movies / tv), and the season searches say who asked.
        found.UnionWith(["movies", "tv", "manual-season", "automatic-season", "manual", "system", "webhook", "queue-derived", "existing-library-import", "library-media-probe"]);
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
