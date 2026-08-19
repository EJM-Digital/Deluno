namespace Deluno.Contracts;

/// <summary>
/// Page-request options shared across list endpoints: a page size and an
/// opaque continuation token from a previous response's <c>NextPageToken</c>.
/// </summary>
public sealed class PageOptions
{
    public int PageSize { get; set; } = 50;
    public string? PageToken { get; set; }
}

/// <summary>
/// Generalises the offset-token pagination shape <c>SqliteDownloadDispatchesRepository</c>
/// used first: fetch one page, hand back a token that resolves to the next
/// offset. Not true keyset (seek) pagination -- the token is the encoded
/// offset -- but it gives callers a stable, reusable page/continuation
/// contract without requiring a sort key per entity.
/// </summary>
public static class DelunoPaging
{
    public const int MaxPageSize = 500;

    public static (IReadOnlyList<T> Items, string? NextPageToken) Paginate<T>(
        IReadOnlyList<T> items,
        PageOptions options)
    {
        var pageSize = Math.Clamp(options.PageSize <= 0 ? 50 : options.PageSize, 1, MaxPageSize);
        var offset = 0;
        if (!string.IsNullOrEmpty(options.PageToken) && int.TryParse(options.PageToken, out var decodedOffset) && decodedOffset > 0)
        {
            offset = decodedOffset;
        }

        var page = items.Skip(offset).Take(pageSize).ToList();
        var nextOffset = offset + page.Count;
        string? nextPageToken = nextOffset < items.Count ? nextOffset.ToString() : null;
        return (page, nextPageToken);
    }
}
