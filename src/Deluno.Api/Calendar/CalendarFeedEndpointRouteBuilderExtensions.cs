using Deluno.Contracts;
using Deluno.Movies.Data;
using Deluno.Platform.Data;
using Deluno.Security;
using Deluno.Security.Data;
using Deluno.Series.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Api.Calendar;

public static class CalendarFeedEndpointRouteBuilderExtensions
{
    /// <summary>How far back and forward the feed reaches, unless asked otherwise.</summary>
    private const int DefaultPastDays = 30;
    private const int DefaultFutureDays = 90;
    private const int MaxWindowDays = 400;

    /// <summary>
    /// A subscribable iCalendar feed of upcoming episodes and film releases —
    /// the Sonarr/Radarr staple Deluno was missing (#260).
    /// </summary>
    /// <remarks>
    /// Calendar clients cannot send an <c>X-Api-Key</c> header, so this one
    /// endpoint accepts the key as a query parameter. That is deliberately not
    /// how the rest of the API authenticates: a key in a URL is visible to
    /// proxies, server logs and browser history, so it stays scoped to this
    /// read-only feed rather than being taught to <c>UserAuthorization</c>
    /// where every endpoint would inherit it. The key must still carry the
    /// read scope, and the response is never cached.
    /// </remarks>
    public static IEndpointRouteBuilder MapDelunoCalendarFeedEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/calendar/feed.ics", async (
            HttpContext httpContext,
            string? apikey,
            int? past,
            int? future,
            ISecurityRepository securityRepository,
            ISeriesCatalogRepository seriesRepository,
            IMovieCatalogRepository movieRepository,
            IPlatformSettingsRepository platformSettingsRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            // A signed-in browser session works too, so the Schedule page can
            // preview the feed without minting a key first.
            var authorized = await IsAuthorizedAsync(httpContext, apikey, securityRepository, cancellationToken);
            if (!authorized)
            {
                return Results.Unauthorized();
            }

            var now = timeProvider.GetUtcNow();
            var pastDays = Math.Clamp(past ?? DefaultPastDays, 0, MaxWindowDays);
            var futureDays = Math.Clamp(future ?? DefaultFutureDays, 1, MaxWindowDays);
            var start = now.AddDays(-pastDays);
            var end = now.AddDays(futureDays);

            var episodes = await seriesRepository.ListCalendarEpisodesAsync(start, end, 2000, cancellationToken);
            var movies = await movieRepository.ListCalendarMoviesAsync(
                DateOnly.FromDateTime(start.UtcDateTime),
                DateOnly.FromDateTime(end.UtcDateTime),
                2000,
                cancellationToken);

            var settings = await platformSettingsRepository.GetAsync(cancellationToken);
            var name = string.IsNullOrWhiteSpace(settings.AppInstanceName) ? "Deluno" : settings.AppInstanceName;
            var body = CalendarFeedBuilder.Build(episodes, movies, now, name);

            httpContext.Response.Headers.CacheControl = "no-store";
            return Results.Text(body, "text/calendar; charset=utf-8");
        })
            .AllowAnonymous()
            .WithMetadata(new DelunoPublicEndpointAttribute());

        return endpoints;
    }

    private static async Task<bool> IsAuthorizedAsync(
        HttpContext httpContext,
        string? apikey,
        ISecurityRepository securityRepository,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(apikey))
        {
            var key = await securityRepository.ValidateApiKeyAsync(apikey.Trim(), cancellationToken);
            return key is not null && UserAuthorization.ApiKeyHasAnyScope(key, "read");
        }

        return await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken) is null;
    }
}
