using System.Reflection;
using System.Text.RegularExpressions;
using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Worker.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Jobs;

/// <summary>
/// Every kind of job the code can queue has a registered handler (#1313). A kind with none is accepted, reported as started
/// and then fails when a lane leases it ("No job handler is registered"); a bulk search did exactly that. The kinds are read
/// from the source: each job request's kind, each job row built directly, and the constants they name.
/// </summary>
public sealed class EveryJobKindTheCodeQueuesHasAHandlerTests
{
    // Names a call passes on rather than spells out: the kind arrives from a caller that is itself scanned for its literal.
    private static readonly string[] Forwarded = ["JobType", "job.JobType", "jobType"];

    [Fact]
    public async Task Every_job_kind_the_code_queues_has_a_registered_handler()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var registry = new JobHandlerRegistry(app.Services.GetServices<IJobHandler>());

        var kinds = QueuedKinds(out var unreadable);

        Assert.Empty(unreadable);
        Assert.True(kinds.Count >= 20, "The scan found too few job kinds to be reading the source: " + string.Join(", ", kinds.Keys));
        var withoutHandler = kinds
            .Where(kind => !registry.RegisteredJobTypes.Contains(kind.Key, StringComparer.OrdinalIgnoreCase))
            .Select(kind => $"{kind.Key} (queued in {kind.Value})")
            .ToArray();
        Assert.Empty(withoutHandler);
    }

    [Fact]
    public void The_scan_reads_a_kind_given_by_name_by_constant_and_by_media_type()
    {
        var kinds = QueuedKinds(out _);

        Assert.Contains("movies.search.manual", kinds.Keys);
        Assert.Contains("series.search.manual", kinds.Keys);
        Assert.Contains(LibrarySearchJobTypes.Movies, kinds.Keys);
        Assert.Contains(LibrarySearchJobTypes.Tv, kinds.Keys);
        Assert.Contains(MediaServerJobTypes.RefreshRetry, kinds.Keys);
        Assert.Contains("series.quality.recalculate", kinds.Keys);
    }

    private static Dictionary<string, string> QueuedKinds(out List<string> unreadable)
    {
        var constants = typeof(LibrarySearchJobTypes).Assembly.GetTypes()
            .Where(type => type.IsAbstract && type.IsSealed && type.Name.EndsWith("JobTypes", StringComparison.Ordinal))
            .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(field => field is { IsLiteral: true } && field.FieldType == typeof(string))
                .Select(field => ($"{type.Name}.{field.Name}", (string)field.GetRawConstantValue()!)))
            .ToDictionary(pair => pair.Item1, pair => pair.Item2);
        constants["SearchExecutionSupport.DownloadClientRetryGrabJobType"] = "download-client.retry-grab";
        constants["DownloadClientRetryGrabJobType"] = "download-client.retry-grab";
        constants["SubtitleTitleSearchJobHandler.JobType"] = SubtitleTitleSearchJobHandler.JobType;
        constants["TitleSearchJobs.Movie"] = "movies.search.manual";
        constants["TitleSearchJobs.Series"] = "series.search.manual";

        var kinds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unread = new List<string>();
        var root = RepositoryRoot();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            var text = File.ReadAllText(file);
            foreach (Match named in Regex.Matches(text, @"\b[Jj]obType:\s*(?<value>""[^""]+""|[A-Za-z_][\w.]*)"))
            {
                Add(named.Groups["value"].Value, name);
            }

            foreach (Match call in Regex.Matches(text, @"new\s+EnqueueJobRequest\(\s*(?<first>[^,]+),"))
            {
                var first = call.Groups["first"].Value.Trim();
                if (!first.StartsWith("JobType:", StringComparison.Ordinal))
                {
                    Add(first, name);
                }
            }
        }

        void Add(string expression, string where)
        {
            expression = expression.Trim();
            var literals = Regex.Matches(expression, @"""([a-z][a-z0-9.\-]*\.[a-z0-9.\-]+)""").Select(match => match.Groups[1].Value).ToArray();
            if (literals.Length > 0)
            {
                foreach (var literal in literals)
                {
                    kinds.TryAdd(literal, where);
                }

                return;
            }

            if (expression.StartsWith("LibrarySearchJobTypes.For", StringComparison.Ordinal))
            {
                kinds.TryAdd(LibrarySearchJobTypes.Movies, where);
                kinds.TryAdd(LibrarySearchJobTypes.Tv, where);
                return;
            }

            if (expression.StartsWith("TitleSearchJobs.JobTypeFor", StringComparison.Ordinal))
            {
                kinds.TryAdd(constants["TitleSearchJobs.Movie"], where);
                kinds.TryAdd(constants["TitleSearchJobs.Series"], where);
                return;
            }

            if (constants.TryGetValue(expression, out var constant))
            {
                kinds.TryAdd(constant, where);
                return;
            }

            if (!Forwarded.Contains(expression) && expression.Contains('.') && char.IsUpper(expression[0]))
            {
                unread.Add($"{where}: {expression}");
            }
        }

        // The planner picks the kind from the media type of the title it refreshes.
        kinds.TryAdd("movies.quality.recalculate", "AutomationEndpointRouteBuilderExtensions.cs");
        kinds.TryAdd("series.quality.recalculate", "AutomationEndpointRouteBuilderExtensions.cs");
        unreadable = unread;
        return kinds;
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Deluno.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Deluno.slnx not found above the test output.");
    }
}
