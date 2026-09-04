using System.Net.Http.Json;
using System.Text.Json;

namespace Deluno.Persistence.Tests.Support;

/// <summary>
/// Reading ids back out of Deluno's API, for tests that care what a route did
/// rather than what shape it said it in.
///
/// <para>Collections come back three ways across the product - a bare array, an
/// array wrapped in an envelope, and an object holding the created item - and a
/// test asserting "it is gone from the list" should not have to know which. The
/// alternative is this logic copied into every test class that removes
/// something, which is how the first two of them were written.</para>
/// </summary>
internal static class ApiPayload
{
    /// <summary>POSTs a body and returns the id of whatever was created.</summary>
    public static async Task<string> CreateAsync(HttpClient client, string route, object body)
    {
        var response = await client.PostAsJsonAsync(route, body);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"POST {route} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return ReadId(document.RootElement);
    }

    /// <summary>Every id a collection route returns, whatever it wraps them in.</summary>
    public static async Task<IReadOnlyList<string>> ListIdsAsync(HttpClient client, string route)
    {
        var response = await client.GetAsync(route);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"GET {route} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return ReadIds(document.RootElement);
    }

    private static IReadOnlyList<string> ReadIds(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Array)
                {
                    root = property.Value;
                    break;
                }
            }
        }

        return root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().Select(ReadId).ToArray()
            : Array.Empty<string>();
    }

    public static string ReadId(JsonElement element)
    {
        foreach (var name in new[] { "id", "Id" })
        {
            if (element.TryGetProperty(name, out var value))
            {
                return value.ValueKind == JsonValueKind.String ? value.GetString()! : value.ToString();
            }
        }

        // The create routes that answer with an envelope rather than the item.
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object &&
                property.Value.TryGetProperty("id", out var nested))
            {
                return nested.GetString()!;
            }
        }

        throw new InvalidOperationException($"No id in {element}");
    }
}
