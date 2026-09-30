using System.Text.RegularExpressions;
using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Worker.Jobs;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// #1086: System and Activity printed the raw job type (<c>series.search.season</c>)
/// under, or instead of, a name. Every job type Deluno can enqueue now has its
/// plain-words label in <see cref="JobTypeLabels"/>; this reads the source of
/// every enqueue site and every handler, so a job type added without a label
/// fails here rather than reaching a screen as a code.
/// </summary>
public sealed class EveryJobTypeHasPlainWordsTests
{
    [Fact]
    public void Every_job_type_Deluno_can_enqueue_has_a_plain_words_label()
    {
        var types = EnqueuedJobTypes();
        Assert.True(types.Count >= 20, $"Only {types.Count} job types were found; the scan has stopped reading the source.");

        var missing = types.Where(type => !JobTypeLabels.IsKnown(type)).OrderBy(type => type).ToArray();
        Assert.True(missing.Length == 0, $"These job types have no label in JobTypeLabels: {string.Join(", ", missing)}");
    }

    [Fact]
    public void Every_job_handler_and_every_named_job_type_has_a_label()
    {
        var handlerTypes = typeof(SubtitleSyncJobHandler).Assembly
            .GetTypes()
            .Where(type => typeof(IJobHandler).IsAssignableFrom(type) && type is { IsAbstract: false, IsInterface: false })
            .Select(type => ((IJobHandler)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type)).JobType)
            .Concat(SqliteJobStore.JobTypeWords.Keys)
            .Distinct()
            .Where(type => !JobTypeLabels.IsKnown(type))
            .ToArray();

        Assert.True(handlerTypes.Length == 0, $"No label for: {string.Join(", ", handlerTypes)}");
    }

    [Fact]
    public void Every_label_is_plain_words_and_never_a_code()
    {
        foreach (var type in JobTypeLabels.KnownTypes)
        {
            var label = JobTypeLabels.For(type);
            Assert.True(char.IsUpper(label[0]), $"{type}'s label '{label}' should start with a capital.");
            Assert.DoesNotContain(".", label);
            Assert.DoesNotContain(type, label, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void A_job_type_nobody_has_labelled_reads_as_words_not_the_code()
    {
        Assert.Equal("Background task", JobTypeLabels.For("zzz.brand-new"));
        Assert.Equal("Background task", JobTypeLabels.For(null));
    }

    [Fact]
    public void A_job_sent_to_a_screen_carries_its_label()
    {
        var now = DateTimeOffset.UnixEpoch;
        var job = new JobQueueItem("j1", "series.search.season", "automation", "queued", null, 0, now, now, null, null, null, null, null, null, null, null, null, 3, null, null);

        var json = System.Text.Json.JsonSerializer.Serialize(job, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Contains("\"label\":\"Search for a season\"", json);
    }

    [Fact]
    public void An_old_message_that_starts_with_a_job_type_is_read_with_its_label()
    {
        Assert.Equal(
            "Search for a season: Best feed candidate is X.",
            JobTypeLabels.RewriteCodes("series.search.season: Best feed candidate is X."));
        Assert.Equal(
            "Search the TV library: released after this worker restarted.",
            JobTypeLabels.RewriteCodes("library.search.tv: released after this worker restarted."));
        Assert.Equal(
            "Retrying search for an episode, which had previously given up.",
            JobTypeLabels.RewriteCodes("Retrying episode.search, which had previously given up."));
        // The longer type wins over the shorter one it starts with, and text that is not a job type is left alone.
        Assert.Equal("Search a library: ok", JobTypeLabels.RewriteCodes("library.search: ok"));
        Assert.Equal("Grabbed file.search.txt", JobTypeLabels.RewriteCodes("Grabbed file.search.txt"));
    }

    [Fact]
    public void An_activity_event_read_from_an_old_row_shows_the_label_not_the_code()
    {
        var item = new ActivityEventItem(
            "e1", "job.completed", "movies.metadata.refresh: done.", null, null, null, null, DateTimeOffset.UnixEpoch);

        Assert.Equal("Refresh film details: done.", item.Message);
    }

    private static HashSet<string> EnqueuedJobTypes()
    {
        var root = RepositoryRoot();
        var found = new HashSet<string>(StringComparer.Ordinal);
        var files = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                           !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                           !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"));

        foreach (var path in files)
        {
            var text = File.ReadAllText(path);
            // JobType: "x", JobType => "x", const string JobType = "x", and any constant in a *JobTypes class.
            foreach (Match match in Regex.Matches(text, @"JobType\s*(?::|=>|=)\s*""(?<v>[a-z][a-z0-9.\-]*)"""))
            {
                found.Add(match.Groups["v"].Value);
            }

            foreach (Match match in Regex.Matches(text, @"const string \w*JobType\w*\s*=\s*""(?<v>[a-z][a-z0-9.\-]*)"""))
            {
                found.Add(match.Groups["v"].Value);
            }

            if (Regex.IsMatch(text, @"static class \w*JobTypes\b"))
            {
                foreach (Match match in Regex.Matches(text, @"const string \w+\s*=\s*""(?<v>[a-z][a-z0-9.\-]*)"""))
                {
                    found.Add(match.Groups["v"].Value);
                }
            }

            // new EnqueueJobRequest("x", ...) with the type as the first positional argument.
            foreach (Match match in Regex.Matches(text, @"new EnqueueJobRequest\(\s*""(?<v>[a-z][a-z0-9.\-]*)"""))
            {
                found.Add(match.Groups["v"].Value);
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
