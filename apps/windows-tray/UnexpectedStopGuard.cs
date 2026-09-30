namespace Deluno.Tray;

/// <summary>
/// Makes "the host asked to stop" actually stop the web host the tray started (#1080).
///
/// <para>The tray starts its host with <c>StartAsync</c> and nothing waits on
/// <c>WaitForShutdown</c>. <c>IHostApplicationLifetime.StopApplication()</c> only
/// cancels the <c>ApplicationStopping</c> token: in a host started by
/// <c>Run</c> the run loop then calls <c>StopAsync</c>, but here nobody does, so
/// when a critical background service gave up, "stopping Deluno so it is started
/// again" was logged and Kestrel simply kept serving on the same process. This
/// watches <c>ApplicationStopping</c>, stops the host itself within a budget
/// (force-disposing it if a service will not stop), and then reports the stop so
/// the supervisor can start a fresh host.</para>
/// </summary>
public static class UnexpectedStopGuard
{
    public static void Attach(
        WebApplication app,
        Func<bool> isExpected,
        Action onUnexpectedStop,
        TimeSpan stopBudget)
    {
        var handled = 0;
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            // A normal Stop/Quit sets the flag before it stops the host: leave that alone.
            if (isExpected() || Interlocked.Exchange(ref handled, 1) == 1)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    using var budget = new CancellationTokenSource(stopBudget);
                    await app.StopAsync(budget.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // A service would not stop in time: dispose the host, which closes Kestrel.
                }

                try
                {
                    await app.DisposeAsync().AsTask().WaitAsync(stopBudget).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Best effort; the supervisor's next start reports any port that is still held.
                }

                if (!isExpected())
                {
                    onUnexpectedStop();
                }
            });
        });
    }
}
