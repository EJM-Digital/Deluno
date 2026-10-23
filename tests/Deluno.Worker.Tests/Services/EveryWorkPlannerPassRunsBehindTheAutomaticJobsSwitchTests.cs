using System.Reflection;
using Deluno.Worker.Services;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// Every pass of the work planner is switched off by the automatic-jobs switch (coverage register, engines), because the only place
/// that runs one is a job lane, and a lane leaves before it plans anything while the switch is off. This reads the code that makes
/// that so: no other file calls a pass, and in the lane every call comes after the line that returns early with the switch off. The
/// running worker doing nothing on a paused install is proven in ThePausedWorkerDoesNothingTests. Refs #1296.
/// </summary>
public sealed class EveryWorkPlannerPassRunsBehindTheAutomaticJobsSwitchTests
{
    [Fact]
    public void No_pass_is_called_from_anywhere_but_the_lane_and_the_lane_calls_every_pass_after_it_has_checked_the_switch()
    {
        var passes = typeof(WorkPlanner)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .Where(name => name.StartsWith("Run", StringComparison.Ordinal) || name.StartsWith("Plan", StringComparison.Ordinal))
            .Distinct()
            .ToArray();
        Assert.True(passes.Length >= 15, $"Only {passes.Length} passes were found; the scan is broken.");

        var root = RepositoryRoot();
        var lanePath = Path.Combine(root, "src", "Deluno.Worker", "Services", "DelunoHeartbeatWorker.cs");
        var plannerPath = Path.Combine(root, "src", "Deluno.Worker", "Services", "WorkPlanner.cs");
        var sources = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(path => !SamePath(path, lanePath) && !SamePath(path, plannerPath))
            .ToDictionary(path => path, File.ReadAllText);

        var strays = (from pass in passes
                      from source in sources
                      where source.Value.Contains($".{pass}(", StringComparison.Ordinal)
                      select $"{Path.GetFileName(source.Key)} calls {pass}").ToArray();
        Assert.True(strays.Length == 0, "A pass is run from outside the job lane: " + string.Join("; ", strays));

        var lane = File.ReadAllText(lanePath);
        var gate = lane.IndexOf("if (!settings.AutoStartJobs)", StringComparison.Ordinal);
        Assert.True(gate > 0, "The lane no longer checks the automatic-jobs switch.");
        var early = lane.IndexOf("return LaneTickResult.Empty", gate, StringComparison.Ordinal);
        Assert.InRange(early, gate, gate + 400);

        var called = 0;
        foreach (var pass in passes)
        {
            var first = lane.IndexOf($".{pass}(", StringComparison.Ordinal);
            if (first < 0)
            {
                continue;
            }

            called++;
            Assert.True(first > early, $"{pass} is called in the lane before it has checked the automatic-jobs switch.");
        }

        Assert.True(called >= 15, $"Only {called} passes are called from the lane; the scan is broken.");
    }

    private static bool SamePath(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Deluno.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
