using Deluno.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Api;

/// <summary>
/// Tells every open Settings screen when a change to its area was saved (#1179), so a page left open
/// in a second tab or window shows the change with no reload. After any successful write to a
/// Settings area it pushes <c>ConfigChanged</c> naming the area. One rule for every endpoint of the
/// area, so a new endpoint cannot be forgotten and an existing one is never edited to add it.
/// </summary>
public static class ConfigChangedPush
{
    // The first segment after /api/ that belongs to each Settings area.
    private static readonly (string Prefix, string Area)[] Areas =
    [
        ("tags", ConfigAreas.Tags),
        ("auto-tag-rules", ConfigAreas.AutoTagging),
        ("custom-formats", ConfigAreas.CustomFormats),
        ("quality-model", ConfigAreas.QualityModel),
        ("notification-preferences", ConfigAreas.Notifications),
        ("notification-webhooks", ConfigAreas.Notifications),
        ("retention", ConfigAreas.Retention),
        ("media-servers", ConfigAreas.MediaServers),
        ("seerr-servers", ConfigAreas.MediaServers),
        ("subtitle-providers", ConfigAreas.Subtitles),
        ("subtitle-languages", ConfigAreas.Subtitles),
        ("subtitles", ConfigAreas.Subtitles),
        ("security", ConfigAreas.Security),
        ("api-keys", ConfigAreas.Security),
        ("release-profiles", ConfigAreas.ReleaseRules),
        ("destination-rules", ConfigAreas.DestinationRules),
        ("download-health", ConfigAreas.DownloadHealth),
        ("failure-rules", ConfigAreas.DownloadHealth),
        ("exclusions", ConfigAreas.DownloadHealth),
        ("blocked-releases", ConfigAreas.DownloadHealth),
        ("integrations/processors", ConfigAreas.Processors),
        ("v1/guides/trash", ConfigAreas.CustomFormats),
        ("v1/release-preferences", ConfigAreas.ReleaseRules),
        ("trakt", ConfigAreas.Lists)
    ];

    // Routes whose screen follows an event of its own that the route's service does not publish, so the write publishes it here
    // instead of a Settings-area one. (The backup, update and recycle-bin settings publish theirs in their own services.)
    private static readonly (string Prefix, Func<IRealtimeEventPublisher?, CancellationToken, Task> Publish)[] OwnEvents =
    [
        // The Collections screen follows MovieChanged; a collection write moves the movies it groups.
        ("movie-collections", (publisher, token) => publisher is null ? Task.CompletedTask : publisher.PublishEntityChangedAsync("Movie", "collections", token))
    ];

    private static bool Matches(ReadOnlySpan<char> rest, string prefix)
        => rest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && (rest.Length == prefix.Length || rest[prefix.Length] == '/');

    /// <summary>True when a write to this path publishes the event of a screen that follows its own event.</summary>
    public static bool HasOwnEvent(PathString path)
        => path.Value is { } value && value.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            && OwnEvents.Any(item => Matches(value.AsSpan(5), item.Prefix));

    /// <summary>The Settings area a request path writes to, or null when it is not one.</summary>
    public static string? AreaFor(PathString path)
    {
        var value = path.Value;
        if (value is null || !value.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = value.AsSpan(5);
        foreach (var (prefix, area) in Areas)
        {
            // A prefix names the first segment or several (v1/guides/trash), and matches whole segments only.
            if (Matches(rest, prefix))
            {
                return area;
            }
        }

        return null;
    }

    public static IApplicationBuilder UseDelunoConfigChangedPush(this IApplicationBuilder app)
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

            var publisher = context.RequestServices.GetService<IRealtimeEventPublisher>();
            if (context.Request.Path.Value is { } value && value.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var (prefix, publish) in OwnEvents)
                {
                    if (Matches(value.AsSpan(5), prefix))
                    {
                        await publish(publisher, CancellationToken.None);
                        return;
                    }
                }
            }

            var area = AreaFor(context.Request.Path);
            if (area is null)
            {
                return;
            }

            await publisher.PublishConfigChangedAsync(area, CancellationToken.None);
        });
}
