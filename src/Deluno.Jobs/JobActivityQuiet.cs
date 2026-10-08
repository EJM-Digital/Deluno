using System.Text.Json;

namespace Deluno.Jobs;

/// <summary>
/// A job whose work is told as one line of its own, by the work itself, keeps the generic lines about the job
/// (brought back, finished) out of Activity (#1155). Today that is the second try at importing a processor's
/// output, which says <c>retriedBecause</c> in its request.
/// </summary>
public static class JobActivityQuiet
{
    public static bool IsQuiet(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson) || !payloadJson.Contains("etriedBecause", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.EnumerateObject().Any(property =>
                    string.Equals(property.Name, "retriedBecause", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(property.Value.GetString()));
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
