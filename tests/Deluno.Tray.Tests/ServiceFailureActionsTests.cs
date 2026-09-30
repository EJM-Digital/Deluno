using Deluno.Contracts;
using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>#1080: an installed Windows service restarts itself after it stops on a failure.</summary>
public sealed class ServiceFailureActionsTests
{
    [Fact]
    public void Failure_actions_restart_the_service_after_a_minute_and_count_a_non_zero_exit_as_a_failure()
    {
        var commands = ServiceManager.FailureActionArguments("Deluno");

        Assert.Equal(
            [
                "failure Deluno reset= 86400 actions= restart/60000/restart/60000/restart/60000",
                "failureflag Deluno 1"
            ],
            commands);
    }

    private sealed class FakeSc(params int?[] exits) : ServiceManager.IScRunner
    {
        public List<string> Ran { get; } = [];
        public int? Run(string arguments, TimeSpan timeout)
        {
            Ran.Add(arguments);
            return exits[Math.Min(Ran.Count - 1, exits.Length - 1)];
        }
    }

    private sealed class Reporter : IBackgroundProblemReporter
    {
        public List<(string Key, string Title, string Message, string Reason)> Reported { get; } = [];
        public List<string> Cleared { get; } = [];
        public void Report(string key, string title, string message, string reason) => Reported.Add((key, title, message, reason));
        public void Clear(string key) => Cleared.Add(key);
    }

    [Fact]
    public void When_sc_succeeds_both_settings_are_set_and_no_problem_is_raised()
    {
        var sc = new FakeSc(0);

        Assert.Null(ServiceManager.SetFailureActions(sc, TimeSpan.FromSeconds(1)));
        Assert.Equal(2, sc.Ran.Count);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(1073)]
    public void A_non_zero_sc_exit_code_stops_at_once_and_is_reported(int exit)
    {
        var sc = new FakeSc(exit);

        var problem = ServiceManager.SetFailureActions(sc, TimeSpan.FromSeconds(1));

        Assert.Contains($"code {exit}", problem);
        Assert.Single(sc.Ran);
    }

    [Fact]
    public async Task A_refusal_is_logged_and_shown_on_Health_in_plain_words_and_never_throws()
    {
        var reporter = new Reporter();
        var warnings = new List<string>();

        await ServiceManager.EnsureFailureActionsInBackground(warnings.Add, reporter, new FakeSc(5));

        var problem = Assert.Single(reporter.Reported);
        Assert.Equal(ServiceManager.FailureActionsProblemKey, problem.Key);
        Assert.StartsWith("Windows would not let Deluno set its restart-on-failure settings", problem.Message);
        Assert.Single(warnings);
        Assert.Empty(reporter.Cleared);
    }

    [Fact]
    public async Task A_start_where_the_settings_are_set_clears_an_earlier_note()
    {
        var reporter = new Reporter();

        await ServiceManager.EnsureFailureActionsInBackground(_ => { }, reporter, new FakeSc(0));

        Assert.Equal([ServiceManager.FailureActionsProblemKey], reporter.Cleared);
        Assert.Empty(reporter.Reported);
    }

    [Fact]
    public async Task A_slow_sc_does_not_block_the_caller_that_starts_it()
    {
        var release = new ManualResetEventSlim();
        var slow = new BlockingSc(release);

        var task = ServiceManager.EnsureFailureActionsInBackground(_ => { }, null, slow);

        Assert.False(task.IsCompleted); // returned immediately while sc.exe is still working
        release.Set();
        await task;
    }

    private sealed class BlockingSc(ManualResetEventSlim release) : ServiceManager.IScRunner
    {
        public int? Run(string arguments, TimeSpan timeout)
        {
            release.Wait(TimeSpan.FromSeconds(10));
            return 0;
        }
    }
}
