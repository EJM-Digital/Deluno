using System.Diagnostics.Metrics;
using System.Text.Json;

namespace Deluno.Infrastructure.Observability;

public static class DelunoObservability
{
    public const string ServiceName = "Deluno";
    public const string TraceHeaderName = "X-Deluno-Trace-Id";

    public static readonly Meter Meter = new(ServiceName, "1.0.0");

    public static readonly Counter<long> JobsQueued = Meter.CreateCounter<long>("deluno.jobs.queued");
    public static readonly Counter<long> JobsCompleted = Meter.CreateCounter<long>("deluno.jobs.completed");
    public static readonly Counter<long> JobsFailed = Meter.CreateCounter<long>("deluno.jobs.failed");
    public static readonly Counter<long> JobRetries = Meter.CreateCounter<long>("deluno.jobs.retries");
    public static readonly Counter<long> RealtimeEventsDropped = Meter.CreateCounter<long>("deluno.realtime.events.dropped");
    public static readonly Counter<long> ImportCompleted = Meter.CreateCounter<long>("deluno.imports.completed");
    public static readonly Counter<long> ImportFailed = Meter.CreateCounter<long>("deluno.imports.failed");
    public static readonly Counter<long> DecisionOutcomes = Meter.CreateCounter<long>("deluno.decisions.outcomes");
    public static readonly Counter<long> IntegrationFailures = Meter.CreateCounter<long>("deluno.integrations.failures");
    public static readonly Counter<long> IntegrationRetries = Meter.CreateCounter<long>("deluno.integrations.retries");
    public static readonly Counter<long> IntegrationCircuitOpened = Meter.CreateCounter<long>("deluno.integrations.circuit_opened");

    public static string CreateTraceId()
        => Guid.CreateVersion7().ToString("N");
}

public static class SensitiveDataRedactor
{
    private static readonly string[] SensitiveKeyFragments =
    [
        "apikey",
        "api_key",
        "password",
        "secret",
        "token",
        "authorization",
        "cookie"
    ];

    public static string RedactJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteRedactedElement(writer, document.RootElement, propertyName: null);
            }

            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return RedactScalar(json);
        }
    }

    public static string RedactScalar(string value)
        => LooksSensitive(value) ? "[redacted]" : value;

    private static void WriteRedactedElement(
        Utf8JsonWriter writer,
        JsonElement element,
        string? propertyName)
    {
        if (propertyName is not null && IsSensitiveKey(propertyName))
        {
            writer.WriteStringValue("[redacted]");
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteRedactedElement(writer, property.Value, property.Name);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var child in element.EnumerateArray())
                {
                    WriteRedactedElement(writer, child, propertyName: null);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                element.WriteTo(writer);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;
        }
    }

    private static bool IsSensitiveKey(string key)
    {
        var normalized = key.Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal);
        return SensitiveKeyFragments.Any(fragment =>
            normalized.Contains(fragment.Replace("_", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksSensitive(string value)
        => value.Contains("Bearer ", StringComparison.OrdinalIgnoreCase) ||
           value.Contains("api_key=", StringComparison.OrdinalIgnoreCase) ||
           value.Contains("apikey=", StringComparison.OrdinalIgnoreCase) ||
           value.Contains("password=", StringComparison.OrdinalIgnoreCase) ||
           value.Contains("token=", StringComparison.OrdinalIgnoreCase);
}
