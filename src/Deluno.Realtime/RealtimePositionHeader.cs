using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Deluno.Realtime;

/// <summary>
/// Tells a screen where the realtime stream stood when its data was read (#1191). Every API answer carries the sequence
/// of the newest event at the moment the request arrived. A page keeps the first one it sees; when its connection opens
/// it asks the hub to replay what was published since, so a change made between its first read and its first join of a
/// subject is not lost until the next change.
/// </summary>
public static class RealtimePositionHeader
{
    public const string Name = "X-Deluno-Event-Seq";

    /// <summary>Which server run the sequence belongs to; it restarts at 0 on every start (#1315).</summary>
    public const string InstanceName = "X-Deluno-Event-Instance";

    public static IApplicationBuilder UseDelunoRealtimePosition(this IApplicationBuilder app)
    {
        return app.Use((context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
                && context.RequestServices.GetService(typeof(IRealtimeResumeSource)) is IRealtimeResumeSource source)
            {
                var position = source.CurrentSequence;
                var instance = source.InstanceId;
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers[Name] = position.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    context.Response.Headers[InstanceName] = instance;
                    return Task.CompletedTask;
                });
            }

            return next(context);
        });
    }
}
