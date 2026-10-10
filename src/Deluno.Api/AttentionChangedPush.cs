using System.Text.RegularExpressions;
using Deluno.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Api;

/// <summary>
/// Tells every open bell when a write changed what it counts or lists (#1179), so a dismiss made in another tab or
/// on another device moves the open bell with no reload and no timer. After any successful write to one of the
/// routes below it asks for <c>AttentionChanged</c> on the dashboard subject; a burst of writes is sent as one push.
/// One rule for every route, so a new endpoint of these areas cannot be forgotten. Changes made by the server itself
/// (a search ending with no release, a case opening) push from where they are saved.
/// </summary>
public static class AttentionChangedPush
{
    // Every write route behind a bell button (apps/web/src/lib/alert-actions.ts) and the Keep and Remove of leftovers,
    // with the word the bell uses to read only the lists that can have moved.
    private static readonly (Regex Route, string Reason)[] Routes =
    [
        (Route(@"^/api/attention/no-release/"), "noRelease"),
        (Route(@"^/api/attention/"), "heldReleases"),
        (Route(@"^/api/(movies|series)/import-recovery/"), "recovery"),
        (Route(@"^/api/jobs/(retry-failed|[^/]+/(dismiss|retry))$"), "jobs"),
        (Route(@"^/api/integrations/processors/handoffs/"), "handoff"),
        (Route(@"^/api/download-clients/orphans/"), "orphans"),
        (Route(@"^/api/download-clients/[^/]+/queue/[^/]+/(remove|health/)"), "client"),
        (Route(@"^/api/download-health/ignore-list"), "client"),
        (Route(@"^/api/download-health/keep-seeding/problems/"), "health"),
        (Route(@"^/api/connections/[^/]+/[^/]+/version/dismiss$"), "update"),
        (Route(@"^/api/system/health/"), "health"),
        (Route(@"^/api/system/component-recovery/"), "health")
    ];

    private static Regex Route(string pattern)
        => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>What a write to this path moves in the bell, or null when it moves nothing.</summary>
    public static string? ReasonFor(PathString path)
    {
        var value = path.Value;
        if (value is null)
        {
            return null;
        }

        foreach (var (route, reason) in Routes)
        {
            if (route.IsMatch(value))
            {
                return reason;
            }
        }

        return null;
    }

    public static IApplicationBuilder UseDelunoAttentionChangedPush(this IApplicationBuilder app)
        => app.Use(async (context, next) =>
        {
            await next();

            if (HttpMethods.IsGet(context.Request.Method)
                || HttpMethods.IsHead(context.Request.Method)
                || HttpMethods.IsOptions(context.Request.Method)
                || context.Response.StatusCode is < 200 or >= 300)
            {
                return;
            }

            var reason = ReasonFor(context.Request.Path);
            if (reason is not null)
            {
                context.RequestServices.GetService<IRealtimeEventPublisher>().RequestAttentionChanged(reason);
            }
        });
}
