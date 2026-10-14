using System.Diagnostics;
using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>
/// <see cref="RealProcessRunner"/> is what runs a vendor's own installer for
/// a ticked bundled component (#640). A timeout must not just make Deluno
/// stop waiting -- it found the process still running, detached, well after
/// Deluno had already reported the component as failed.
/// </summary>
public sealed class RealProcessRunnerTests
{
    [Fact]
    public async Task A_timed_out_process_is_killed_rather_than_left_running()
    {
        var runner = new RealProcessRunner();

        // A process that would otherwise run far longer than the timeout below.
        var result = await runner.RunAsync(
            "ping",
            "-n 60 127.0.0.1",
            TimeSpan.FromMilliseconds(500),
            CancellationToken.None);

        Assert.Null(result);

        // Windows finishes tearing the tree down a moment later: wait until no "PING"
        // is left, up to 10 s (ten times what teardown takes), then confirm.
        SpinWait.SpinUntil(() =>
        {
            var left = Process.GetProcessesByName("PING");
            try { return left.Length == 0; }
            finally { foreach (var p in left) p.Dispose(); }
        }, TimeSpan.FromSeconds(10));
        var survivors = Process.GetProcessesByName("PING");
        try
        {
            Assert.Empty(survivors);
        }
        finally
        {
            foreach (var p in survivors)
            {
                p.Dispose();
            }
        }
    }
}
