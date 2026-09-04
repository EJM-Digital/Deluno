using Deluno.Persistence.Tests.Support;
using System.Net;

namespace Deluno.Persistence.Tests.Api;

public sealed class ApplicationTestHostSmokeTests
{
    [Fact]
    public async Task The_whole_application_starts_and_answers_an_authenticated_request()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.GetAsync("/api/tags");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
