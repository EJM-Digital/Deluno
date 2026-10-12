namespace Deluno.Integrations.DownloadClients;

/// <summary>What a client said when asked about one job's pause (#1251).</summary>
/// <param name="Has">Whether the client has the job at all.</param>
/// <param name="Paused">When it has the job: whether the job is paused.</param>
internal readonly record struct ClientPauseReading(ClientHas Has, bool Paused);

/// <summary>
/// One rule for every client that pauses or resumes a single download (#1251, after #1212's removal): the pause or resume
/// counts as done only when the client had the job before the call and shows it in the asked state after it. SABnzbd answers
/// <c>{"status":true}</c> to a pause or a resume of an id it has never heard of, and a client's "yes" says nothing about what
/// it did, so the client is looked at, not believed: asked before the call (a job it does not have is a plain failure, and
/// nothing is sent) and asked again after (a job that is not in the asked state is a failure, whatever the call said).
///
/// <para>A job already in the asked state needs no call and is done. Like a removal, a client that lags a moment (a torrent
/// client's list) is looked at again for a short while after it has said yes; a client that says no or errors is a failure at
/// once and is never waited for.</para>
/// </summary>
internal static class ClientPauseGuard
{
    public static readonly TimeSpan SettleWindow = TimeSpan.FromSeconds(2);

    public static readonly TimeSpan SettlePoll = TimeSpan.FromMilliseconds(250);

    public static async Task<DownloadClientActionResult> RunAsync(
        Deluno.Connections.Contracts.DownloadClientItem client,
        string queueItemId,
        string action,
        string clientName,
        Func<Task<ClientPauseReading>> read,
        Func<Task<DownloadClientActionResult>> send,
        TimeSpan settleWindow = default,
        TimeSpan settlePoll = default,
        CancellationToken cancellationToken = default)
    {
        var pausing = string.Equals(action, DownloadClientActions.Pause, StringComparison.OrdinalIgnoreCase);
        var verb = pausing ? "paused" : "resumed";
        var before = await read();
        switch (before.Has)
        {
            case ClientHas.No:
                return DownloadClientHelpers.ActionFailure(client, queueItemId, action,
                    $"{clientName} has no download with that id, so nothing was {verb}. It may already have been removed there.");
            case ClientHas.Unknown:
                return DownloadClientHelpers.ActionFailure(client, queueItemId, action,
                    $"{clientName} could not be asked about this download, so nothing was changed.", category: "unreachable");
        }

        if (before.Paused == pausing)
        {
            return DownloadClientHelpers.ActionSuccess(client, queueItemId, action,
                pausing ? $"{clientName} already has this download paused." : $"{clientName} already has this download running.");
        }

        var result = await send();
        if (!result.Succeeded)
        {
            return result;
        }

        var after = await read();
        var waited = TimeSpan.Zero;
        var pause = settlePoll > TimeSpan.Zero ? settlePoll : SettlePoll;
        var window = settleWindow > TimeSpan.Zero ? settleWindow : SettleWindow;
        while (after.Has == ClientHas.Yes && after.Paused != pausing && waited < window)
        {
            var step = pause < window - waited ? pause : window - waited;
            await Task.Delay(step, cancellationToken);
            waited += step;
            after = await read();
        }

        return after.Has switch
        {
            ClientHas.Yes when after.Paused == pausing => result,
            ClientHas.Yes => DownloadClientHelpers.ActionFailure(client, queueItemId, action,
                pausing
                    ? $"{clientName} still has this download running after being told to pause it, so it was not paused."
                    : $"{clientName} still has this download paused after being told to resume it, so it was not resumed."),
            ClientHas.No => DownloadClientHelpers.ActionFailure(client, queueItemId, action,
                $"{clientName} no longer lists this download, so Deluno cannot say it was {verb}."),
            _ => DownloadClientHelpers.ActionFailure(client, queueItemId, action,
                $"{clientName} did not confirm the change, so Deluno cannot say the download was {verb}.", category: "unreachable")
        };
    }
}
