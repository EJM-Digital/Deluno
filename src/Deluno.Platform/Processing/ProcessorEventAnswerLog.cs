using System.Text.Json;
using Deluno.Platform.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Deluno.Platform.Processing;

/// <summary>
/// Writes what Deluno answered to every processor report (#1242): the status, the hand-off it was about and the reason,
/// at Information. A 409 that Weir logs as "returned HTTP 409" used to leave nothing in Deluno's own log to say which
/// branch answered or why. The line names <c>processors/events</c> so the golden-path log collection finds it.
/// </summary>
public static class ProcessorEventAnswerLog
{
    public const string Category = "Deluno.Platform.ProcessorEvents";

    public static async ValueTask<object?> LogAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var result = await next(context);
        try
        {
            var request = context.Arguments.OfType<ProcessorEventRequest>().FirstOrDefault();
            var logger = context.HttpContext.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(Category);
            logger?.LogInformation("{Line}", Describe(request, result));
        }
        catch
        {
            // A log line never changes the answer.
        }

        return result;
    }

    /// <summary>The one line: "POST /api/integrations/processors/events answered 409 for hand-off X (completed): reason".</summary>
    public static string Describe(ProcessorEventRequest? request, object? result)
    {
        var status = result is IStatusCodeHttpResult { StatusCode: { } code } ? code : 200;
        var body = TryRead(result);
        var handoff = FirstString(body, "handoffId") ?? request?.HandoffId ?? "none";
        var reason = FirstString(body, "message") ?? FirstString(body, "title") ?? FirstString(body, "status");
        if (body is { ValueKind: JsonValueKind.Object } &&
            body.Value.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
        {
            reason = string.Join(" ", errors.EnumerateObject().Select(error => $"{error.Name}: {error.Value}"));
        }

        var handled = body is { ValueKind: JsonValueKind.Object } && body.Value.TryGetProperty("alreadyHandled", out var already) && already.ValueKind == JsonValueKind.True;
        return $"POST /api/integrations/processors/events answered {status}{(handled ? " (already handled)" : string.Empty)} for hand-off {handoff}, report {request?.Status ?? "unknown"}"
            + (string.IsNullOrWhiteSpace(reason) ? string.Empty : $": {reason}");
    }

    private static JsonElement? TryRead(object? result)
    {
        if (result is not IValueHttpResult { Value: { } value })
        {
            return null;
        }

        try
        {
            return JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
        }
        catch (Exception exception) when (exception is NotSupportedException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? FirstString(JsonElement? body, string name)
        => body is { ValueKind: JsonValueKind.Object } element
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()
                : null;
}
