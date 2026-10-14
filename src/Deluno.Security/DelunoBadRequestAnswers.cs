using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Deluno.Security;

/// <summary>
/// One answer for every request the framework could not bind (#1280): a body that is missing, is not JSON, has a member of the
/// wrong kind, or arrives in a format the route does not take. Each is a 4xx whose body is <c>{ "message": "...", "member": "..." }</c>,
/// the shape the web app and the SDK already read first, with the plain words in <c>message</c> and, when one member is at fault,
/// its name in <c>member</c>.
///
/// <para>Without this the framework answers a binding failure with an empty 400, so a caller cannot tell what was wrong. It works
/// from two parts that must stay together: <c>RouteHandlerOptions.ThrowOnBadRequest</c> (set in
/// <see cref="SecurityServiceCollectionExtensions.AddDelunoSecurityModule"/>) makes the framework throw instead of answering
/// itself, and <see cref="UseDelunoBadRequestAnswers"/> turns the throw into the answer. A route that reads its body by hand uses
/// <see cref="Describe(JsonException)"/> for the same words.</para>
/// </summary>
public static class DelunoBadRequestAnswers
{
    /// <summary>The name the routing step gives the endpoint it substitutes when no route accepts the request's content type.</summary>
    private const string UnsupportedMediaTypeEndpointName = "415 HTTP Unsupported Media Type";

    private const string WrongFormatMessage =
        "This address does not take that kind of request. Send JSON as application/json, or a file as a multipart/form-data upload.";

    private static readonly Regex JsonPath = new(@"Path:\s*(?<path>[^|]+?)\s*(?:\||$)", RegexOptions.Compiled);
    private static readonly Regex ConvertedTo = new(@"could not be converted to (?<type>\S+?)\.?\s*(?:Path:|$)", RegexOptions.Compiled);
    private static readonly Regex MissingRequired = new(@"missing required properties including: (?<names>.+?)\.?\s*(?:Path:|$)", RegexOptions.Compiled);
    private static readonly Regex FailedToBind = new("Failed to bind parameter \"(?:[^\"]*\\s)?(?<name>[^\\s\"]+)\"", RegexOptions.Compiled);

    /// <summary>Goes after authentication and in front of the endpoints. A caller who is not signed in is told so before anything about the request's format.</summary>
    public static IApplicationBuilder UseDelunoBadRequestAnswers(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            if (string.Equals(context.GetEndpoint()?.DisplayName, UnsupportedMediaTypeEndpointName, StringComparison.Ordinal))
            {
                // The routing step has swapped the real route for its 415 answer, so the route's own sign-in never runs:
                // ask for the sign-in here, so a caller who is not signed in is told that and not about the format.
                if (DelunoSignInGate.RequiresSignIn(context))
                {
                    var denied = await UserAuthorization.RequireAuthenticatedAsync(context, context.RequestAborted);
                    if (denied is not null)
                    {
                        await denied.ExecuteAsync(context);
                        return;
                    }
                }

                await WriteAsync(context, StatusCodes.Status415UnsupportedMediaType, WrongFormatMessage, member: null);
                return;
            }

            try
            {
                await next();
            }
            catch (BadHttpRequestException exception) when (!context.Response.HasStarted)
            {
                var (message, member) = Describe(exception);
                await WriteAsync(context, exception.StatusCode, message, member);
            }
        });
    }

    public static async Task WriteAsync(HttpContext context, int status, string message, string? member)
    {
        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(member is null ? new Dictionary<string, string> { ["message"] = message } : new Dictionary<string, string> { ["message"] = message, ["member"] = member }));
    }

    public static (string Message, string? Member) Describe(BadHttpRequestException exception)
    {
        if (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return ("That request is larger than Deluno accepts.", null);
        }

        if (exception.StatusCode == StatusCodes.Status415UnsupportedMediaType)
        {
            return (WrongFormatMessage, null);
        }

        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is JsonException json)
            {
                return Describe(json);
            }
        }

        var text = exception.Message;
        if (text.Contains("no body was provided", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Expected request with Body", StringComparison.OrdinalIgnoreCase))
        {
            return ("This request needs a body, and none was sent.", null);
        }

        var bind = FailedToBind.Match(text);
        if (bind.Success)
        {
            var name = JsonNamingPolicy.CamelCase.ConvertName(bind.Groups["name"].Value);
            return ($"The \"{name}\" value in the address is not valid.", name);
        }

        return ("That request was not understood.", null);
    }

    /// <summary>The plain words for a body the JSON reader refused: the member at fault and what it needed.</summary>
    public static (string Message, string? Member) Describe(JsonException exception)
    {
        var text = exception.Message;

        var missing = MissingRequired.Match(text);
        if (missing.Success)
        {
            var names = missing.Groups["names"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(name => name.Trim('\'', ' ', '.'))
                .Where(name => name.Length > 0)
                .ToArray();
            if (names.Length > 0)
            {
                return (names.Length == 1
                    ? $"The \"{names[0]}\" field is required."
                    : $"These fields are required: {string.Join(", ", names.Select(name => $"\"{name}\""))}.", names[0]);
            }
        }

        var member = MemberOf(exception);
        var converted = ConvertedTo.Match(text);
        if (converted.Success)
        {
            var kind = KindOf(converted.Groups["type"].Value);
            return (member is null
                ? $"The request body must be {kind}."
                : $"The \"{member}\" field must be {kind}.", member);
        }

        if (text.Contains("could not be mapped", StringComparison.OrdinalIgnoreCase) && member is not null)
        {
            return ($"The \"{member}\" field is not one this request takes.", member);
        }

        return (member is null
            ? "The request body is not valid JSON."
            : $"The request body is not valid JSON, at \"{member}\".", member);
    }

    private static string? MemberOf(JsonException exception)
    {
        var path = exception.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            var found = JsonPath.Match(exception.Message);
            path = found.Success ? found.Groups["path"].Value : null;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        path = path.Trim();
        if (path.StartsWith("$.", StringComparison.Ordinal))
        {
            path = path[2..];
        }

        return path is "$" or "" ? null : path;
    }

    private static string KindOf(string typeName)
    {
        var type = typeName.Trim('\'', '"');
        return type switch
        {
            _ when type.Contains("Int", StringComparison.Ordinal) || type.Contains("Byte", StringComparison.Ordinal) => "a whole number",
            _ when type.Contains("Double", StringComparison.Ordinal) || type.Contains("Single", StringComparison.Ordinal) || type.Contains("Decimal", StringComparison.Ordinal) => "a number",
            _ when type.Contains("Boolean", StringComparison.Ordinal) => "true or false",
            _ when type.Contains("DateTime", StringComparison.Ordinal) || type.Contains("DateOnly", StringComparison.Ordinal) || type.Contains("TimeOnly", StringComparison.Ordinal) || type.Contains("TimeSpan", StringComparison.Ordinal) => "a date or time",
            _ when type.Contains("Guid", StringComparison.Ordinal) => "an id",
            _ when type.Contains("String", StringComparison.Ordinal) => "text",
            _ when type.Contains("List", StringComparison.Ordinal) || type.Contains("[]", StringComparison.Ordinal) || type.Contains("IEnumerable", StringComparison.Ordinal) || type.Contains("Collection", StringComparison.Ordinal) || type.Contains("Array", StringComparison.Ordinal) => "a list",
            _ when type.Contains("Dictionary", StringComparison.Ordinal) || type.Contains("JsonObject", StringComparison.Ordinal) => "an object",
            _ => "a different kind of value"
        };
    }
}
