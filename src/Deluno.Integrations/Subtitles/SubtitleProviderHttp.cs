using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

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
/// The request handling every subtitle provider needs, written once.
///
/// <para>Weir had this as a module the eight clients imported, and that was
/// the right shape — it is the same here. The one rule worth stating: <b>a
/// provider that cannot answer returns nothing rather than throwing.</b> Eight
/// sources are asked in turn and one of them being down, rate limited or serving
/// an error page must cost the next one nothing. The exception is a 429, which
/// is a fact about that source the caller has to record.</para>
/// </summary>
public static class SubtitleProviderHttp
{
    public const string ClientName = "subtitle-providers";

    /// <summary>
    /// Identifying Deluno honestly. Several of these providers rate limit or
    /// block by user agent, and pretending to be a browser is how a source
    /// decides to block the whole application later.
    /// </summary>
    public const string UserAgent = "Deluno/1.0 (+https://github.com/jampat000/Deluno)";

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

    /// <summary>A GET that answers null for anything that is not a usable body.</summary>
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

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException or NotSupportedException)
        {
            return null;
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
            return response.IsSuccessStatusCode
                ? await response.Content.ReadAsStringAsync(cancellationToken)
                : string.Empty;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return string.Empty;
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

        var retryAfter = response.Headers.RetryAfter?.Delta
            ?? (response.Headers.RetryAfter?.Date is { } date
                ? date - DateTimeOffset.UtcNow
                : null);

        throw new SubtitleProviderRateLimitedException(providerKey, retryAfter);
    }
}
