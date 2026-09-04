using Deluno.Persistence.Tests.Support;
using System.Net;
using System.Net.Http.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// A bad import body is a bad request, not a broken Deluno.
///
/// <para><b>Found by sending one.</b> <c>ImportExecuteRequest</c> carries a
/// non-nullable <c>Preview</c>, and the JSON binder does not enforce that. A
/// body that omits it — or that sends the preview's fields flattened onto the
/// top level, which is the natural mistake and the one made here — bound
/// <c>null</c>, and the pipeline dereferenced it. The caller got
/// <c>500 An unexpected error occurred</c>.</para>
///
/// <para>That is the wrong answer twice over: it blames Deluno for the caller's
/// mistake, and it says nothing about how to fix it. A 500 also reads as an
/// outage to anything watching health, which is how a typo becomes an
/// incident.</para>
/// </summary>
public sealed class ImportBodyIsCheckedTests
{
    public static TheoryData<string> ImportRoutes() =>
    [
        "/api/filesystem/import/execute",
        "/api/filesystem/import/jobs"
    ];

    [Theory]
    [MemberData(nameof(ImportRoutes))]
    public async Task An_import_without_a_preview_is_refused_and_says_why(string route)
    {
        await using var app = await ApplicationTestHost.StartAsync();

        // The preview's fields, flattened — what a caller writes when they have
        // not noticed the request nests them.
        var response = await app.Client.PostAsJsonAsync(route, new
        {
            sourcePath = @"C:\Media\Arrival (2016)\Arrival.mkv",
            mediaType = "movies",
            title = "Arrival",
            year = 2016
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("preview", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unexpected error", body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(ImportRoutes))]
    public async Task An_empty_import_body_is_refused_the_same_way(string route)
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync(route, new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(
            "unexpected error",
            await response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }
}
