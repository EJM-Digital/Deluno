using Deluno.Jobs.Data;

namespace Deluno.Persistence.Tests.Jobs;

public sealed class JobLaneSignalTests
{
    [Fact]
    public void Two_notifications_before_a_wait_produce_exactly_one_wake_up()
    {
        var signal = new JobLaneSignal();
        var gate = signal.Register("search", ["library.search"]);

        signal.Notify("library.search");
        signal.Notify("library.search");

        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task WaitAsync_consumes_the_signal_once_then_times_out()
    {
        var signal = new JobLaneSignal();
        var gate = signal.Register("search", ["library.search"]);
        signal.Notify("library.search");

        Assert.True(await gate.WaitAsync(TimeSpan.Zero, CancellationToken.None));
        Assert.False(await gate.WaitAsync(TimeSpan.Zero, CancellationToken.None));
    }

    [Fact]
    public void Notify_on_an_unclaimed_job_type_does_not_throw()
    {
        var signal = new JobLaneSignal();
        signal.Register("search", ["library.search"]);

        var exception = Record.Exception(() => signal.Notify("episode.search"));

        Assert.Null(exception);
    }

    [Fact]
    public void Register_is_case_insensitive_by_job_type()
    {
        var signal = new JobLaneSignal();
        var gate = signal.Register("search", ["Library.Search"]);

        signal.Notify("library.search");

        Assert.Equal(1, gate.CurrentCount);
    }
}
