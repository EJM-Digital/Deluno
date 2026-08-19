using Microsoft.AspNetCore.SignalR;

namespace Deluno.Realtime.Hubs;

public sealed class ActivityHub(IRealtimeResumeSource resumeSource) : Hub
{
    /// <summary>
    /// Called by the client right after connecting or reconnecting with the
    /// last sequence number it saw. Inside the resume window this replays
    /// what was missed; beyond it, the client is told to resync from REST.
    /// </summary>
    public RealtimeResumeResult Resume(long lastSeq) => resumeSource.Resume(lastSeq);
}

