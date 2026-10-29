using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Media;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The list of import problems for films and for shows keeps only the four key fields of each case's details, found at the top
/// or inside the case's retry request, so a case and its failed hand-off show once; the full details come only when asked for.
/// </summary>
public sealed class TheRecoveryListKeepsOnlyTheKeyFieldsTests
{
    public static TheoryData<string> Collections => new() { "movies", "series" };

    private const string Details =
        "{\"sourcePath\":\"C:/Downloads/Film.mkv\",\"secretNote\":\"not for the list\",\"dispatchId\":\"d1\",\"RetryRequest\":{\"HandoffId\":\"h1\",\"ClientId\":\"c1\",\"QueueItemId\":\"q1\",\"other\":\"x\"}}";

    [Theory]
    [MemberData(nameof(Collections))]
    public async Task The_list_carries_the_dispatch_hand_off_client_and_queue_ids_wherever_they_sit_and_nothing_else(string collection)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var made = await app.Client.PostAsJsonAsync($"/api/{collection}/import-recovery", new
        {
            title = "Same Film", failureKind = "importFailed", summary = "It did not import.", recommendedAction = "Try again.", detailsJson = Details
        });
        Assert.True(made.IsSuccessStatusCode);

        var listed = await app.Client.GetFromJsonAsync<JsonElement>($"/api/{collection}/import-recovery");
        var full = await app.Client.GetFromJsonAsync<JsonElement>($"/api/{collection}/import-recovery?includeDetails=true");

        var trimmed = JsonDocument.Parse(Assert.Single(listed.GetProperty("recentCases").EnumerateArray()).GetProperty("detailsJson").GetString()!).RootElement;
        Assert.Equal(
            ["clientId=c1", "dispatchId=d1", "handoffId=h1", "queueItemId=q1"],
            trimmed.EnumerateObject().Select(property => $"{property.Name}={property.Value.GetString()}").Order());
        Assert.Contains("secretNote", Assert.Single(full.GetProperty("recentCases").EnumerateArray()).GetProperty("detailsJson").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Details_that_name_none_of_the_four_or_are_not_an_object_trim_to_nothing()
    {
        Assert.Null(ImportRecoveryKeyDetails.Trim(null));
        Assert.Null(ImportRecoveryKeyDetails.Trim("  "));
        Assert.Null(ImportRecoveryKeyDetails.Trim("{\"sourcePath\":\"C:/x.mkv\"}"));
        Assert.Null(ImportRecoveryKeyDetails.Trim("[1,2]"));
        Assert.Null(ImportRecoveryKeyDetails.Trim("not json"));
        Assert.Null(ImportRecoveryKeyDetails.Trim("{\"dispatchId\":\"\"}"));
        Assert.Equal("{\"dispatchId\":\"d9\"}", ImportRecoveryKeyDetails.Trim("{\"DispatchId\":\"d9\",\"x\":1}"));
    }
}
