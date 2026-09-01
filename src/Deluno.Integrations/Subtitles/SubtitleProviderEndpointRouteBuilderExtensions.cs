using System.Diagnostics;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Integrations.Subtitles;

/// <summary>
/// Where subtitles come from.
///
/// <para>These live here rather than in <c>Deluno.Connections</c> for a
/// dependency reason worth writing down: the registry of what Deluno ships is in
/// <c>Deluno.Integrations</c>, and Integrations already references Connections.
/// Putting the routes in Connections would need the arrow to point both ways.
/// The <i>storage</i> is still Connections', which is the part DESIGN-002 rule 4
/// was about.</para>
///
/// <para><b>One list, always complete.</b> The listing is every provider Deluno
/// ships with its configured row attached where there is one — not just the ones
/// somebody has added. A screen that only shows what you already have cannot
/// tell you what you are missing, and "which sources exist" is a fact about the
/// build rather than about your install.</para>
/// </summary>
public static class SubtitleProviderEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoSubtitleProviders(this IEndpointRouteBuilder endpoints)
    {
        // Credentials in, credentials out — the same policy the indexers group
        // carries, and the reason `EndpointAuthorizationCoverageTests` refuses
        // to let an endpoint ship without saying which one it is on.
        var providers = endpoints.MapGroup("/api/subtitle-providers")
            .RequireAuthorization(DelunoAuthorizationPolicies.Write);

        providers.MapGet(string.Empty, async (
            [FromServices] ISubtitleProviderRegistry registry,
            [FromServices] ISubtitleProviderRepository repository,
            CancellationToken cancellationToken) =>
        {
            var configured = await repository.ListAsync(cancellationToken);
            return Results.Ok(registry.All.Select(provider => Describe(provider, configured)).ToArray());
        });

        providers.MapPut("{key}", async (
            string key,
            [FromBody] SaveSubtitleProviderRequest request,
            [FromServices] ISubtitleProviderRegistry registry,
            [FromServices] ISubtitleProviderRepository repository,
            CancellationToken cancellationToken) =>
        {
            var provider = registry.Find(key);
            if (provider is null)
            {
                return Results.NotFound();
            }

            // Validated against what will actually be stored, not against what
            // the form sent: the screen cannot show a saved password, so it
            // sends blank when it was not touched. Refusing that would mean a
            // provider could never be re-enabled without retyping its account.
            var stored = (await repository.ListAsync(cancellationToken))
                .FirstOrDefault(item => string.Equals(item.ProviderKey, provider.Key, StringComparison.OrdinalIgnoreCase));

            var errors = Validate(provider, request, stored);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var saved = await repository.SaveAsync(provider.Key, provider.DisplayName, request, cancellationToken);
            return Results.Ok(Describe(provider, [saved]));
        });

        providers.MapDelete("{key}", async (
            string key,
            [FromServices] ISubtitleProviderRepository repository,
            CancellationToken cancellationToken)
            => await repository.DeleteAsync(key, cancellationToken) ? Results.NoContent() : Results.NotFound());

        /*
            The test.

            It runs a real search for a title everybody's provider has heard of,
            with the credentials being offered rather than the ones already
            stored — so somebody can find out whether a key works *before*
            committing it, which is the whole reason a test button exists.

            What it reports is what came back, not whether a socket opened. A
            provider answering 200 with an empty list because the key is wrong is
            the failure people actually hit, and "connected" would be a lie about
            it.
        */
        providers.MapPost("{key}/test", async (
            string key,
            [FromBody] SaveSubtitleProviderRequest request,
            [FromServices] ISubtitleProviderRegistry registry,
            [FromServices] ISubtitleProviderRepository repository,
            CancellationToken cancellationToken) =>
        {
            var provider = registry.Find(key);
            if (provider is null)
            {
                return Results.NotFound();
            }

            var stored = (await repository.ListAsync(cancellationToken))
                .FirstOrDefault(item => string.Equals(item.ProviderKey, provider.Key, StringComparison.OrdinalIgnoreCase));

            var credentials = new SubtitleProviderCredentials(
                Username: string.IsNullOrWhiteSpace(request.Username) ? stored?.Username : request.Username.Trim(),
                Password: string.IsNullOrWhiteSpace(request.Secret) ? stored?.Secret : request.Secret.Trim(),
                ApiKey: string.IsNullOrWhiteSpace(request.ApiKey) ? stored?.ApiKey : request.ApiKey.Trim());

            var result = await TestAsync(provider, credentials, cancellationToken);

            if (stored is not null)
            {
                await repository.RecordHealthAsync(
                    provider.Key,
                    result.Status,
                    result.Message,
                    result.LatencyMs,
                    result.Ok,
                    rateLimitedUntilUtc: null,
                    cancellationToken,
                    result.Failure);
            }

            return Results.Ok(result);
        });

        return endpoints;
    }

    /// <summary>
    /// A search a working provider cannot fail to answer.
    ///
    /// <para>A film for the ones that do films and an episode for Gestdown,
    /// because a TV-only source asked about a film correctly returns nothing and
    /// that must not read as broken.</para>
    /// </summary>
    private static async Task<SubtitleProviderTestResult> TestAsync(
        ISubtitleProvider provider,
        SubtitleProviderCredentials credentials,
        CancellationToken cancellationToken)
    {
        if (provider.RequiredCredentials != SubtitleCredentialFields.None
            && !provider.CredentialsOptional
            && !credentials.HasAny)
        {
            return new SubtitleProviderTestResult(
                Ok: false,
                Status: "untested",
                Message: $"{provider.DisplayName} needs its account details before it can be tested.",
                LatencyMs: null,
                ResultCount: null,
                Failure: IntegrationFailureFactory.FromLegacy(
                    "subtitle",
                    provider.Key,
                    provider.DisplayName,
                    "test",
                    "configuration",
                    $"{provider.DisplayName} needs its account details before it can be tested."));
        }

        var request = provider.Scope == SubtitleProviderScope.TvOnly
            ? new SubtitleSearchRequest("Breaking Bad", null, 1, 1, null, null, null, ["en"], IsEpisode: true)
            : new SubtitleSearchRequest("Inception", 2010, null, null, null, null, null, ["en"], IsEpisode: false);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var results = await provider.SearchAsync(request, credentials, cancellationToken);
            stopwatch.Stop();

            return results.Count > 0
                ? new SubtitleProviderTestResult(
                    Ok: true,
                    Status: "healthy",
                    Message: $"{provider.DisplayName} answered with {results.Count} subtitle(s) for the test title.",
                    LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                    ResultCount: results.Count)
                : new SubtitleProviderTestResult(
                    Ok: false,
                    Status: "degraded",
                    // Said plainly, because this is the failure people actually
                    // hit and "connected" would be a lie about it.
                    Message: $"{provider.DisplayName} answered but found nothing for a title it should have. That is usually wrong or expired credentials.",
                    LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                    ResultCount: 0);
        }
        catch (SubtitleProviderRateLimitedException rateLimited)
        {
            stopwatch.Stop();
            var until = DateTimeOffset.UtcNow.Add(rateLimited.RetryAfter ?? TimeSpan.FromHours(1));
            return new SubtitleProviderTestResult(
                Ok: true,
                Status: "rate-limited",
                // Working, and asked to be left alone. Two different things, and
                // an indexer already draws the same distinction.
                Message: $"{provider.DisplayName} is working and is rate limiting Deluno. Nothing is wrong; it will be asked again later.",
                LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                ResultCount: null,
                Failure: IntegrationFailureFactory.FromLegacy(
                    "subtitle",
                    provider.Key,
                    provider.DisplayName,
                    "test",
                    "rate-limited",
                    $"{provider.DisplayName} is working and is rate limiting Deluno.",
                    retryAfterUtc: until));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            stopwatch.Stop();
            return new SubtitleProviderTestResult(
                Ok: false,
                Status: "failed",
                Message: $"{provider.DisplayName} could not be reached: {exception.Message}",
                LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                ResultCount: null,
                Failure: IntegrationFailureFactory.FromException(
                    "subtitle",
                    provider.Key,
                    provider.DisplayName,
                    "test",
                    exception,
                    retryScheduled: true));
        }
    }

    private static SubtitleProviderOption Describe(
        ISubtitleProvider provider,
        IReadOnlyList<SubtitleProviderConnection> configured)
        => new(
            Key: provider.Key,
            DisplayName: provider.DisplayName,
            Description: provider.Description,
            Scope: provider.Scope switch
            {
                SubtitleProviderScope.MoviesOnly => "movies",
                SubtitleProviderScope.TvOnly => "tv",
                _ => "both"
            },
            NeedsUsername: provider.RequiredCredentials.HasFlag(SubtitleCredentialFields.Username),
            NeedsPassword: provider.RequiredCredentials.HasFlag(SubtitleCredentialFields.Password),
            NeedsApiKey: provider.RequiredCredentials.HasFlag(SubtitleCredentialFields.ApiKey),
            CredentialsOptional: provider.CredentialsOptional,
            Configured: configured.FirstOrDefault(item =>
                string.Equals(item.ProviderKey, provider.Key, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Refuses to save a source that cannot work.
    ///
    /// <para>Only when it is being turned *on*: saving a disabled provider with
    /// half its details filled in is somebody part-way through, and refusing that
    /// would mean losing what they had typed.</para>
    /// </summary>
    private static Dictionary<string, string[]> Validate(
        ISubtitleProvider provider,
        SaveSubtitleProviderRequest request,
        SubtitleProviderConnection? stored)
    {
        var errors = new Dictionary<string, string[]>();

        if (!request.IsEnabled || provider.CredentialsOptional)
        {
            return errors;
        }

        bool Missing(string? sent, string? kept) => string.IsNullOrWhiteSpace(sent) && string.IsNullOrWhiteSpace(kept);

        if (provider.RequiredCredentials.HasFlag(SubtitleCredentialFields.ApiKey)
            && Missing(request.ApiKey, stored?.ApiKey))
        {
            errors["apiKey"] = [$"{provider.DisplayName} will not answer without an API key."];
        }

        if (provider.RequiredCredentials.HasFlag(SubtitleCredentialFields.Username)
            && Missing(request.Username, stored?.Username))
        {
            errors["username"] = [$"{provider.DisplayName} counts your downloads against an account, so it needs the username."];
        }

        if (provider.RequiredCredentials.HasFlag(SubtitleCredentialFields.Password)
            && Missing(request.Secret, stored?.Secret))
        {
            errors["secret"] = [$"{provider.DisplayName} needs the password for that account."];
        }

        return errors;
    }
}
