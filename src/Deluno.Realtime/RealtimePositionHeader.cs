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

    public static IApplicationBuilder UseDelunoRealtimePosition(this IApplicationBuilder app)
    {
        return app.Use((context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
                && context.RequestServices.GetService(typeof(IRealtimeResumeSource)) is IRealtimeResumeSource source)
            {
                var position = source.CurrentSequence;
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers[Name] = position.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return Task.CompletedTask;
                });
            }

            return next(context);
        });
    }
}
