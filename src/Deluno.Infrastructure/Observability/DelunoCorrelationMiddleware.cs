using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Deluno.Infrastructure.Observability;

public static class DelunoCorrelationMiddleware
{
    public static IApplicationBuilder UseDelunoCorrelation(this IApplicationBuilder app)
        => app.Use(async (context, next) =>
        {
            var traceId = ResolveTraceId(context);
            context.TraceIdentifier = traceId;
            context.Response.Headers[DelunoObservability.TraceHeaderName] = traceId;

            var logger = context.RequestServices
                .GetService(typeof(ILoggerFactory)) is ILoggerFactory loggerFactory
                ? loggerFactory.CreateLogger("Deluno.Request")
                : null;

            using var scope = logger?.BeginScope(new Dictionary<string, object>
            {
                ["TraceId"] = traceId,
                ["Method"] = context.Request.Method,
                ["Path"] = context.Request.Path.Value ?? string.Empty
            });

            await next();
        });

    private static string ResolveTraceId(HttpContext context)
    {
        var incoming = context.Request.Headers[DelunoObservability.TraceHeaderName].FirstOrDefault();
        return IsSafeTraceId(incoming) ? incoming! : DelunoObservability.CreateTraceId();
    }

    private static bool IsSafeTraceId(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           value.Length <= 128 &&
           value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.');
}
