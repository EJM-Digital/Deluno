using System.Text;
using System.Text.Json;

namespace Deluno.Contracts;

/// <summary>
/// A bounded request for one list page. The continuation token is opaque to
/// callers: repositories choose the stable sort keys that make their cursor a
/// seek rather than an increasingly expensive offset.
/// </summary>
public sealed record PageRequest(int PageSize = 50, string? PageToken = null)
{
    public const int MaximumPageSize = 500;

    public int BoundedPageSize => Math.Clamp(PageSize <= 0 ? 50 : PageSize, 1, MaximumPageSize);
}

/// <summary>
/// A list response that tells callers whether they received the whole answer.
/// </summary>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextPageToken, bool HasMore)
{
    public static Page<T> Of(IReadOnlyList<T> items, string? nextPageToken)
        => new(items, nextPageToken, !string.IsNullOrEmpty(nextPageToken));
}

/// <summary>
/// Serializes repository-owned keyset values into an opaque URL-safe token.
/// Invalid tokens deliberately restart at the first page; they cannot turn
/// into unsafe SQL or an unbounded offset.
/// </summary>
public static class DelunoPageToken
{
    public static string Encode(params string?[] values)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values)));

    public static string?[]? Decode(string? token, int expectedValues)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        try
        {
            var values = JsonSerializer.Deserialize<string?[]>(Encoding.UTF8.GetString(Convert.FromBase64String(token)));
            return values is { Length: var length } && length == expectedValues ? values : null;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or DecoderFallbackException)
        {
            return null;
        }
    }
}
