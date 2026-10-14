using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Deluno.Worker;

/// <summary>
/// #1279: the one place that says how large a request body may be, and how long each saved text member may be.
///
/// <para><b>Body size.</b> Every API route that takes a body gets <see cref="DefaultBytes"/> (256 KB, far more than any
/// screen sends). The few routes that carry a long list or a file name their own larger limit in <see cref="Rules"/>. A
/// body over its limit is answered 413 with a plain message before the route runs, so nothing is saved and nothing
/// throws.</para>
///
/// <para><b>Text length.</b> Every text member in a JSON body is checked against a limit chosen by the member's name
/// (<see cref="TextLimit"/>) and refused with a plain 400 that names the member. Members that hold a whole document
/// (JSON, YAML, CSV, SQL) are bounded by the body limit alone.</para>
/// </summary>
public static class RequestBodyLimits
{
    public const long DefaultBytes = 256 * 1024;
    public const long LongListBytes = 1024 * 1024;
    public const long SubtitleFileBytes = 5L * 1024 * 1024;
    public const long BackupFileBytes = 256L * 1024 * 1024;

    public const int NameLength = 200;
    public const int DescriptionLength = 4_000;
    public const int PathLength = 4_096;
    public const int UrlLength = 2_048;
    public const int PatternLength = 2_000;
    public const int SecretLength = 1_000;
    public const int OtherTextLength = 4_000;

    /// <summary>Routes that need more than the default, matched by the start (and, for uploads, the end) of the path. First match wins.</summary>
    private static readonly (string Prefix, string? Suffix, long Bytes)[] Rules =
    [
        ("/api/backups/restore", null, BackupFileBytes),
        ("/api/movies/", "/upload", SubtitleFileBytes),
        ("/api/series/", "/upload", SubtitleFileBytes),
        ("/api/filesystem/import/", null, LongListBytes),
        ("/api/integrations/external/import-preview", null, LongListBytes),
        ("/api/custom-formats/import", null, LongListBytes),
        ("/api/v1/guides/trash/", null, LongListBytes),
        ("/api/setup/", null, LongListBytes),
        ("/api/migration/", null, LongListBytes),
        ("/api/quality-model", null, LongListBytes),
        ("/api/release-profiles/order", null, LongListBytes)
    ];

    /// <summary>The largest body this path may carry.</summary>
    public static long LimitFor(PathString path)
    {
        var value = path.Value ?? string.Empty;
        foreach (var (prefix, suffix, bytes) in Rules)
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && (suffix is null || value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            {
                return bytes;
            }
        }

        return DefaultBytes;
    }

    /// <summary>The longest a saved text member of this name may be, or null when it holds a whole document and only the body limit bounds it.</summary>
    public static int? TextLimit(string member)
    {
        var name = member.ToLowerInvariant();
        if (name.EndsWith("json", StringComparison.Ordinal) || name.EndsWith("yaml", StringComparison.Ordinal)
            || name.EndsWith("csv", StringComparison.Ordinal) || name.EndsWith("xml", StringComparison.Ordinal)
            || name is "body" or "raw" or "content" or "text" or "sql" or "data" or "payload" or "document" or "lines")
        {
            return null;
        }

        if (name.Contains("password", StringComparison.Ordinal) || name.Contains("secret", StringComparison.Ordinal)
            || name.Contains("token", StringComparison.Ordinal) || name.Contains("apikey", StringComparison.Ordinal))
        {
            return SecretLength;
        }

        if (name.EndsWith("url", StringComparison.Ordinal) || name.EndsWith("uri", StringComparison.Ordinal)
            || name.EndsWith("link", StringComparison.Ordinal) || name is "host" or "address" or "hostorurl")
        {
            return UrlLength;
        }

        if (name.Contains("path", StringComparison.Ordinal) || name.EndsWith("folder", StringComparison.Ordinal)
            || name.EndsWith("directory", StringComparison.Ordinal) || name is "root" or "dataroot" or "file" or "from" or "to")
        {
            return PathLength;
        }

        if (name.Contains("pattern", StringComparison.Ordinal) || name.Contains("regex", StringComparison.Ordinal)
            || name.Contains("format", StringComparison.Ordinal) || name.Contains("template", StringComparison.Ordinal)
            || name.Contains("expression", StringComparison.Ordinal) || name.Contains("condition", StringComparison.Ordinal)
            || name is "query" or "search" or "term")
        {
            return PatternLength;
        }

        if (name.Contains("description", StringComparison.Ordinal) || name.Contains("overview", StringComparison.Ordinal)
            || name.Contains("summary", StringComparison.Ordinal) || name.Contains("note", StringComparison.Ordinal)
            || name.Contains("message", StringComparison.Ordinal) || name.Contains("reason", StringComparison.Ordinal)
            || name.Contains("comment", StringComparison.Ordinal) || name.Contains("detail", StringComparison.Ordinal)
            || name.Contains("explanation", StringComparison.Ordinal) || name.Contains("subject", StringComparison.Ordinal))
        {
            return DescriptionLength;
        }

        if (name.Contains("name", StringComparison.Ordinal) || name.Contains("title", StringComparison.Ordinal)
            || name.Contains("label", StringComparison.Ordinal) || name is "alias" or "tag" or "group" or "key" or "username"
            || name.EndsWith("id", StringComparison.Ordinal))
        {
            return NameLength;
        }

        return OtherTextLength;
    }

    /// <summary>Walks a JSON value and returns a plain sentence for the first text member that is too long, or null.</summary>
    public static string? FirstTooLong(JsonElement element, string member = "")
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var limit = TextLimit(member);
                if (limit is { } max && element.GetString() is { } text && text.Length > max)
                {
                    var shown = member.Length == 0 ? "That text" : $"The {Spaced(member)}";
                    return $"{shown} is too long. It can be at most {max:N0} characters.";
                }

                return null;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var problem = FirstTooLong(property.Value, property.Name);
                    if (problem is not null) return problem;
                }

                return null;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var problem = FirstTooLong(item, member);
                    if (problem is not null) return problem;
                }

                return null;
            default:
                return null;
        }
    }

    /// <summary>The name of the member <see cref="FirstTooLong"/> found too long, so the refusal can name it (#1280).</summary>
    private static string? FirstTooLongMember(JsonElement element, string member = "")
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return TextLimit(member) is { } max && element.GetString() is { } text && text.Length > max && member.Length > 0 ? member : null;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var found = FirstTooLongMember(property.Value, property.Name);
                    if (found is not null) return found;
                }

                return null;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var found = FirstTooLongMember(item, member);
                    if (found is not null) return found;
                }

                return null;
            default:
                return null;
        }
    }

    private static string Spaced(string member)
    {
        var chars = new List<char>(member.Length + 4);
        foreach (var c in member)
        {
            if (char.IsUpper(c) && chars.Count > 0) chars.Add(' ');
            chars.Add(char.ToLowerInvariant(c));
        }

        return new string(chars.ToArray());
    }

    /// <summary>Applies the limits to every API request that has a body. Added once, after the sign-in gate, by the shared pipeline.</summary>
    public static IApplicationBuilder UseDelunoRequestBodyLimits(this IApplicationBuilder app)
        => app.Use(async (context, next) =>
        {
            var request = context.Request;
            var hasBody = request.ContentLength is > 0 || !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method));
            if (!request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) || !hasBody)
            {
                await next();
                return;
            }

            var limit = LimitFor(request.Path);
            if (request.ContentLength > limit)
            {
                await RefuseAsync(context, StatusCodes.Status413PayloadTooLarge, TooLarge(limit));
                return;
            }

            var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (feature is { IsReadOnly: false })
            {
                feature.MaxRequestBodySize = limit;
            }

            var isJson = request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;
            if (!isJson)
            {
                await next();
                return;
            }

            request.EnableBuffering();
            var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > limit)
                {
                    await RefuseAsync(context, StatusCodes.Status413PayloadTooLarge, TooLarge(limit));
                    return;
                }
            }

            buffer.Position = 0;
            string? tooLong = null;
            string? tooLongMember = null;
            try
            {
                using var document = JsonDocument.Parse(buffer);
                tooLong = FirstTooLong(document.RootElement);
                tooLongMember = tooLong is null ? null : FirstTooLongMember(document.RootElement);
            }
            catch (JsonException)
            {
                // Not valid JSON: the route's own binding answers that with its own 400.
            }

            request.Body.Position = 0;
            if (tooLong is not null)
            {
                await RefuseAsync(context, StatusCodes.Status400BadRequest, tooLong, tooLongMember);
                return;
            }

            await next();
        });

    private static string TooLarge(long limit)
        => limit >= 1024 * 1024
            ? $"That request is too large. The most this can take is {limit / (1024 * 1024):N0} MB."
            : $"That request is too large. The most this can take is {limit / 1024:N0} KB.";

    private static async Task RefuseAsync(HttpContext context, int status, string message, string? member = null)
    {
        // The same shape as every other refusal of a request (DelunoBadRequestAnswers): { message, member }; "error" stays for callers that read it.
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(
            member is null ? new Dictionary<string, string> { ["message"] = message, ["error"] = message }
                           : new Dictionary<string, string> { ["message"] = message, ["error"] = message, ["member"] = member },
            context.RequestAborted);
    }
}
