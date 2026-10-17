using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The local broker-compatible endpoints (docs/metadata-broker-contract.md) on an install with no TMDb key: they answer in the
/// contract's shape and say plainly that a key is missing, and they need a signed-in caller. What a configured install answers
/// needs TMDb's real replies, which are not recorded yet (tests/fixtures/recorded/MISSING.md).
/// </summary>
public sealed class MetadataBrokerEndpointsTests
{
    [Fact]
    public async Task Status_on_an_install_with_no_key_says_it_is_available_but_cannot_return_metadata_yet()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var status = await app.Client.GetFromJsonAsync<JsonElement>("/api/metadata/broker/status");

        Assert.Equal("deluno-broker", status.GetProperty("provider").GetString());
        Assert.Equal("local-direct", status.GetProperty("mode").GetString());
        Assert.False(status.GetProperty("isConfigured").GetBoolean());
        Assert.Contains("needs a key", status.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Search_on_an_install_with_no_key_answers_the_contract_shape_with_no_results_instead_of_an_error()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.GetAsync("/api/metadata/broker/search?query=The%20Matrix&mediaType=movies&year=1999");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("deluno-broker", body.GetProperty("provider").GetString());
        Assert.Equal("local-direct", body.GetProperty("mode").GetString());
        Assert.Equal(0, body.GetProperty("resultCount").GetInt32());
        Assert.Equal(0, body.GetProperty("results").GetArrayLength());
    }

    [Fact]
    public async Task Both_broker_endpoints_refuse_a_caller_who_is_not_signed_in()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var anonymous = app.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/metadata/broker/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/metadata/broker/search?query=x")).StatusCode);
    }
}
