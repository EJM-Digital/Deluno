using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Deluno.Infrastructure.Http;

/// <summary>
/// JSON request bodies that say how long they are (#605).
///
/// <para><c>PostAsJsonAsync</c> and <c>JsonContent.Create</c> stream the body
/// with chunked transfer encoding and no <c>Content-Length</c>. NZBGet's
/// embedded web server needs the length: it logged "content length is 0" for
/// every call and Deluno could not reach it at all. Small embedded servers are
/// exactly what Deluno talks to - download clients, processors, media
/// servers - so every outbound JSON body is sent sized, through here.</para>
///
/// <para>Serialized the way <c>PostAsJsonAsync</c> did (web defaults, the
/// payload's runtime type), so nothing on the wire changes but the framing.</para>
/// </summary>
public static class SizedJson
{
    private static readonly JsonSerializerOptions WebDefaults = new(JsonSerializerDefaults.Web);

    public static HttpContent Create(object? value, JsonSerializerOptions? options = null)
    {
        var json = JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object), options ?? WebDefaults);
        var content = new StringContent(json, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    public static Task<HttpResponseMessage> PostSizedJsonAsync(this HttpClient http, Uri uri, object? value, CancellationToken cancellationToken = default)
        => http.PostAsync(uri, Create(value), cancellationToken);

    public static Task<HttpResponseMessage> PostSizedJsonAsync(this HttpClient http, string uri, object? value, CancellationToken cancellationToken = default)
        => http.PostAsync(uri, Create(value), cancellationToken);
}
