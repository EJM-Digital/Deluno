using System.Diagnostics;
using Deluno.Infrastructure.Processes;

namespace Deluno.Platform.Tests.Infrastructure;

public sealed class ChildProcessEdgeTests
{
    [Fact]
    public async Task Output_is_kept_up_to_the_limit_and_the_rest_is_read_and_dropped()
    {
        var text = new string('x', ChildProcess.MaxKeptCharacters + 5000);

        var kept = await ChildProcess.DrainAsync(new StringReader(text));

        Assert.Equal(ChildProcess.MaxKeptCharacters, kept.Length);
    }

    [Fact]
    public async Task A_pipe_that_closes_under_the_read_ends_it_with_what_was_read()
    {
        Assert.Equal("ab", await ChildProcess.DrainAsync(new BreakingReader("ab", new IOException())));
        Assert.Equal("ab", await ChildProcess.DrainAsync(new BreakingReader("ab", new ObjectDisposedException("pipe"))));
        Assert.Equal("ab", await ChildProcess.DrainAsync(new BreakingReader("ab", new InvalidOperationException())));
    }

    [Fact]
    public async Task A_failure_of_another_kind_is_not_hidden()
        => await Assert.ThrowsAsync<FormatException>(() => ChildProcess.DrainAsync(new BreakingReader("ab", new FormatException())));

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(System.ComponentModel.Win32Exception))]
    [InlineData(typeof(NotSupportedException))]
    public void A_program_that_is_already_gone_or_not_ours_to_stop_is_left_alone(Type failure)
        => ChildProcess.TryKill(() => throw (Exception)Activator.CreateInstance(failure)!);

    [Fact]
    public void Stopping_a_program_runs_the_stop()
    {
        var stopped = false;

        ChildProcess.TryKill(() => stopped = true);

        Assert.True(stopped);
    }

    [Fact]
    public void Any_other_failure_while_stopping_is_not_hidden()
        => Assert.Throws<FormatException>(() => ChildProcess.TryKill(() => throw new FormatException()));

    [Fact]
    public async Task A_program_that_leaves_a_helper_holding_its_pipes_open_is_reported_with_what_it_said_so_far()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // reason: the shell syntax that leaves a helper behind differs; the Unix form is what CI runs
        }

        var start = new ProcessStartInfo("sh") { ArgumentList = { "-c", "sleep 8 & exit 0" } };
        var watch = Stopwatch.StartNew();

        var result = await ChildProcess.RunAsync(start, TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.True(result.Started);
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Equal(string.Empty, result.Output);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task A_program_that_cannot_start_says_why()
    {
        var result = await ChildProcess.RunAsync(new ProcessStartInfo("/definitely/not/a/program-" + Guid.NewGuid().ToString("N")), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(result.Started);
        Assert.False(string.IsNullOrEmpty(result.Error));
    }

    private sealed class BreakingReader(string first, Exception failure) : TextReader
    {
        private bool _given;

        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            if (_given)
            {
                throw failure;
            }

            _given = true;
            first.CopyTo(buffer.Span);
            return ValueTask.FromResult(first.Length);
        }
    }
}
