using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deluno.Infrastructure.Resilience;

namespace Deluno.Integrations.Subtitles;

/// <summary>
/// Raised when a provider says it has had enough of us.
///
/// <para>Distinct from a provider simply not answering, because the two want
/// opposite responses: a failure is a health problem to be counted and reported,
/// and a 429 is a source that is working and needs leaving alone for a while.
/// The Connection's <c>RateLimitedUntilUtc</c> already exists for the second and
/// is what an indexer uses.</para>
/// </summary>
public sealed class SubtitleProviderRateLimitedException(string providerKey, TimeSpan? retryAfter)
    : Exception($"{providerKey} is rate limiting Deluno.")
{
    public string ProviderKey { get; } = providerKey;

    /// <summary>What the provider asked for, where it said. Null means it did not.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>
/// Raised when a provider says an account-level allowance is what stopped
/// it, not the request — OpenSubtitles' daily download quota (HTTP 406) is
/// the one example so far (#946).
///
/// <para>Distinct from <see cref="SubtitleProviderRateLimitedException"/>: a
/// 429 clears itself in minutes and says nothing about the account; a used-up
/// quota does not come back until the daily allowance resets, and the message
/// needs to say so rather than reading as a broken source.</para>
/// </summary>
public sealed class SubtitleProviderQuotaExhaustedException(string providerKey, string message, DateTimeOffset? resetUtc = null)
    : Exception(message)
{
    public string ProviderKey { get; } = providerKey;

    /// <summary>When the provider said the allowance resets. Null when it did not say.</summary>
    public DateTimeOffset? ResetUtc { get; } = resetUtc;
}

/// <summary>
/// The request handling every subtitle provider needs, written once.
///
/// <para>Weir had this as a module the eight clients imported, and that was
/// the right shape — it is the same here.</para>
///
/// <para><b>Three outcomes, and they are not the same.</b> A provider that
/// has no matching resource — a 404, or a body that is not the JSON it
/// promised — returns nothing, because seven sources are asked in turn and
/// one having no result must cost the next one nothing. HTTP errors such as
/// authentication failures and server outages throw with their status so the
/// caller can retain a typed failure. A provider that could not be
/// <i>reached</i> throws, and so does a 429.</para>
///
/// <para>That distinction was learnt on the rig rather than reasoned out.
/// Podnapisi's host stopped resolving; the search swallowed the DNS failure and
/// returned an empty list; and the screen said <i>"answered but found nothing for
/// a title it should have — that is usually wrong or expired credentials"</i>,
/// which is a confident wrong diagnosis of a site being down. A source that
/// cannot be reached and a source that has nothing are different facts and have
/// to read differently.</para>
/// </summary>
public static class SubtitleProviderHttp
{
    public const string ClientName = "subtitle-providers";

    /// <summary>
    /// Identifying Deluno honestly. Several of these providers rate limit or
    /// block by user agent, and pretending to be a browser is how a source
    /// decides to block the whole application later.
    /// </summary>
    public const string UserAgent = Deluno.Contracts.ProjectRepositories.DelunoUserAgent;

    public static HttpClient Create(
        IHttpClientFactory factory,
        SubtitleProviderCredentials? credentials = null,
        string? apiKeyHeader = null,
        bool basicAuth = false)
    {
        var client = factory.CreateClient(ClientName);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        if (credentials is not null)
        {
            if (apiKeyHeader is not null && !string.IsNullOrWhiteSpace(credentials.ApiKey))
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation(apiKeyHeader, credentials.ApiKey.Trim());
            }

            if (basicAuth
                && !string.IsNullOrWhiteSpace(credentials.Username)
                && !string.IsNullOrWhiteSpace(credentials.Password))
            {
                var token = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{credentials.Username.Trim()}:{credentials.Password.Trim()}"));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
            }
        }

        return client;
    }

    /// <summary>
    /// A GET. Null when the provider has no matching resource or answered with
    /// malformed JSON; throws for typed HTTP errors and when it could not be
    /// reached — see the type summary for why those are different.
    /// </summary>
    public static Task<T?> GetJsonAsync<T>(
        HttpClient client,
        string url,
        string providerKey,
        CancellationToken cancellationToken)
        where T : class
        => SendJsonAsync<T>(client, new HttpRequestMessage(HttpMethod.Get, url), providerKey, cancellationToken);

    public static async Task<T?> SendJsonAsync<T>(
        HttpClient client,
        HttpRequestMessage request,
        string providerKey,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            ThrowIfRateLimited(response, providerKey);
            await ThrowIfQuotaExhaustedAsync(response, providerKey, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            ThrowIfHttpFailure(response, providerKey);

            return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            // It answered with something that is not the JSON it promised.
            // Unhelpful, not unreachable.
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException($"{providerKey} did not answer in time.");
        }
    }

    /// <summary>A GET returning the raw body, for a page a provider only serves as HTML.</summary>
    public static async Task<string> GetTextAsync(
        HttpClient client,
        string url,
        string providerKey,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(url, cancellationToken);
            ThrowIfRateLimited(response, providerKey);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return string.Empty;
            }

            ThrowIfHttpFailure(response, providerKey);
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A timeout. Recorded as unreachable, which is what it is — the two
            // scrapers are the slowest of the seven, and a site that has gone
            // quiet is not a site that has nothing.
            throw new HttpRequestException($"{providerKey} did not answer in time.");
        }
    }

    /// <summary>
    /// The bytes at a download link.
    ///
    /// <para>This one *does* throw, unlike the searches: a download that fails is
    /// the end of an attempt Deluno has already committed to, and the caller
    /// needs to record why against the title rather than write nothing and move
    /// on silently.</para>
    /// </summary>
    public static async Task<byte[]> GetBytesAsync(
        HttpClient client,
        string url,
        string providerKey,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(url, cancellationToken);
        ThrowIfRateLimited(response, providerKey);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private static void ThrowIfRateLimited(HttpResponseMessage response, string providerKey)
    {
        if (response.StatusCode != HttpStatusCode.TooManyRequests)
        {
            return;
        }

        throw new SubtitleProviderRateLimitedException(providerKey, IntegrationResiliencePolicy.TryGetRetryAfter(response));
    }

    /// <summary>
    /// OpenSubtitles answers a spent daily download allowance with HTTP 406
    /// on <c>/download</c>, with a body naming when the quota resets (#946).
    /// The reset time is read here, generically, so any provider using the
    /// same shape gets the same treatment rather than a second copy of this
    /// parsing living in one provider's own file.
    /// </summary>
    private static async Task ThrowIfQuotaExhaustedAsync(
        HttpResponseMessage response, string providerKey, CancellationToken cancellationToken)
    {
        if (response.StatusCode != HttpStatusCode.NotAcceptable)
        {
            return;
        }

        DateTimeOffset? resetUtc = null;
        try
        {
            var body = await response.Content.ReadFromJsonAsync<QuotaExhaustedBody>(cancellationToken);
            resetUtc = body?.ResetTimeUtc;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            // The allowance is still spent even without a body Deluno can
            // read; the caller falls back to a sensible guess for when.
        }

        throw new SubtitleProviderQuotaExhaustedException(
            providerKey, "The daily download allowance is used up for today.", resetUtc);
    }

    private sealed record QuotaExhaustedBody(
        [property: JsonPropertyName("reset_time_utc")] DateTimeOffset? ResetTimeUtc);

    private static void ThrowIfHttpFailure(HttpResponseMessage response, string providerKey)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw new HttpRequestException(
            $"{providerKey} returned HTTP {(int)response.StatusCode} ({response.StatusCode}).",
            inner: null,
            statusCode: response.StatusCode);
    }
}
