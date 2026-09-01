using System.Text.RegularExpressions;
using Deluno.Contracts;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// Every scheduled pass is declared where a person can see it.
///
/// <para>The intervals used to be written at their call sites — eight
/// <c>TryClaimScheduledPassAsync("download.state", TimeSpan.FromMinutes(5), …)</c>
/// buried in planner methods. Nothing could list them, so nothing could show
/// what Deluno runs or when it runs next, and each new pass was another line
/// nobody would find. James: <i>"all these scheduled jobs again should system
/// jobs the same as what radarr does, we keep them in one spot"</i>.</para>
///
/// <para>This reads the planner's source, because the failure it catches is a
/// pass added the old way: it would schedule perfectly and simply never appear
/// on the System screen.</para>
/// </summary>
public sealed class SystemTasksAreDeclaredInOneSpotTests
{
    [Fact]
    public void No_pass_is_scheduled_with_an_interval_written_at_its_call_site()
    {
        var source = string.Join("\n", ScheduledSourceFiles());

        var claims = Regex.Matches(source, @"TryClaimScheduledPassAsync\(\s*(?<first>[^,]+),\s*(?<second>[^,]+),");

        Assert.NotEmpty(claims);

        foreach (Match claim in claims)
        {
            var key = claim.Groups["first"].Value.Trim();
            var interval = claim.Groups["second"].Value.Trim();

            // A literal key means the name exists in one more place than it
            // should; a literal interval means the schedule cannot be shown.
            Assert.True(
                key is "scheduleKey" || key.StartsWith("SystemTasks.", StringComparison.Ordinal),
                $"A pass is claimed under the literal {key}. Declare it in SystemTasks and use the constant.");

            Assert.True(
                interval.StartsWith("SystemTasks.IntervalFor(", StringComparison.Ordinal) ||
                interval.StartsWith("SystemTasks.IntervalForHours(", StringComparison.Ordinal),
                $"A pass sets its own interval ({interval}). Move it to SystemTasks so the System screen can show it.");
        }
    }

    /// <summary>
    /// And every declared task is actually claimed by something.
    /// </summary>
    [Fact]
    public void Every_task_on_the_screen_is_a_pass_that_really_runs()
    {
        var source = string.Join("\n", ScheduledSourceFiles());

        foreach (var task in SystemTasks.All)
        {
            // Listing a task nothing claims would put a row on the System
            // screen that never has a last run and never will — indistinguishable
            // from a pass that is broken.
            var constant = task.Key switch
            {
                SystemTasks.Backup => nameof(SystemTasks.Backup),
                SystemTasks.DownloadDispatchPolling => nameof(SystemTasks.DownloadDispatchPolling),
                _ => ConstantName(task.Key)
            };

            Assert.True(
                source.Contains($"SystemTasks.{constant}", StringComparison.Ordinal),
                $"'{task.Name}' is declared but the planner never claims SystemTasks.{constant}.");
        }
    }

    [Fact]
    public void A_pass_that_was_never_declared_is_refused_rather_than_given_a_default()
    {
        // Returning a plausible default would let an undeclared pass schedule
        // itself and stay invisible, which is the whole failure being closed.
        Assert.Throws<KeyNotFoundException>(() => SystemTasks.IntervalFor("something.nobody.declared"));
    }

    /// <summary>Keys are dotted, the constants that name them are not.</summary>
    private static string ConstantName(string key)
        => string.Concat(key.Split('.').Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

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

    private static IEnumerable<string> ScheduledSourceFiles()
    {
        var root = RepositoryRoot();
        var relativePaths = new[]
        {
            Path.Combine("src", "Deluno.Worker", "Services", "WorkPlanner.cs"),
            Path.Combine("src", "Deluno.Api", "Backup", "DelunoBackupService.cs"),
            Path.Combine("src", "Deluno.Integrations", "Search", "RankingModelTrainingHostedService.cs"),
            Path.Combine("src", "Deluno.Jobs", "Data", "DownloadDispatchPollingHostedService.cs"),
            Path.Combine("src", "Deluno.Recovery", "Services", "ImportRecoveryRetentionService.cs")
        };

        return relativePaths.Select(path => File.ReadAllText(Path.Combine(root, path)));
    }
}
