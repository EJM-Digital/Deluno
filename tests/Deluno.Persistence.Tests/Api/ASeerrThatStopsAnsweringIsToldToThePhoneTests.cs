using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// A Seerr connection going bad and coming back, told to a phone (register: notifications, "health degraded / restored for ... Seerr"): the
/// Seerr is a loopback server answering as the real Seerr 3.5.0 did (tests/fixtures/recorded/seerr: its status and settings, and the 403 it gives a
/// key it does not know), the phone a push service answering as the real ntfy 2.34.0 did. The Test click records the connection's health; the
/// health pass (the real runner) tells the problem once and the recovery once, and a pass that changes nothing says nothing.
/// </summary>
public sealed class ASeerrThatStopsAnsweringIsToldToThePhoneTests
{
    [Fact]
    public async Task A_Seerr_that_refuses_its_key_is_told_once_when_it_starts_and_once_when_it_is_fine_again_and_a_pass_that_changes_nothing_says_nothing()
    {
        var refusing = false;
        await using var seerr = await RecordedAppServer.StartAsync(
            "seerr",
            [RecordedReplies.Seerr("status"), RecordedReplies.Seerr("settings-main")],
            (_, _) => Volatile.Read(ref refusing) ? RecordedReplies.Seerr("wrong-key").Exchanges[0].Reply.ToResponse() : null);
        await using var phone = await RecordedAppServer.StartAsync("ntfy", [], (request, _) => request.Method == HttpMethod.Post ? RecordedReplies.Ntfy("delivered").Exchanges[0].Reply.ToResponse() : null);
        await using var app = await ApplicationTestHost.StartAsync();
        Assert.True((await app.Client.PatchAsJsonAsync("/api/settings", new { enableNotifications = true })).IsSuccessStatusCode);
        Assert.True((await app.Client.PostAsJsonAsync("/api/notification-webhooks", new { name = "Anything", url = phone.BaseUrl + "/health", eventFilters = "health", isEnabled = true, provider = "generic", includeHealthWarnings = true })).IsSuccessStatusCode);
        // A connection that did not ask for health warnings never hears about Seerr: it is an advisory check, not one that stops Deluno working.
        Assert.True((await app.Client.PostAsJsonAsync("/api/notification-webhooks", new { name = "Quiet", url = phone.BaseUrl + "/quiet", eventFilters = "health", isEnabled = true, provider = "generic" })).IsSuccessStatusCode);
        var created = await app.Client.PostAsJsonAsync("/api/seerr-servers", new { name = "Lab Seerr", baseUrl = seerr.BaseUrl, apiKey = "key-1", isEnabled = true });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        async Task<string> TestAsync()
        {
            var response = await app.Client.PostAsync($"/api/seerr-servers/{id}/test", null);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString()!;
        }

        async Task PassAsync()
        {
            using var scope = app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<Deluno.Worker.HealthChecks.HealthCheckRunner>().RunCategoriesAsync([HealthCheckCategories.MediaServer], CancellationToken.None);
        }

        string[] Said() => phone.Requests.Where(request => request.PathAndQuery == "/health")
            .Select(request => JsonNode.Parse(request.Body)!["eventCategory"]!.GetValue<string>()).ToArray();

        Assert.Equal("healthy", await TestAsync());
        await PassAsync();
        Assert.Empty(Said());

        Volatile.Write(ref refusing, true);
        Assert.NotEqual("healthy", await TestAsync());
        await PassAsync();
        await PassAsync();
        Assert.Equal(["health.problem.advisory"], Said().Where(said => said.StartsWith("health.problem", StringComparison.Ordinal)));
        var problem = Assert.Single(phone.Requests, request => request.PathAndQuery == "/health" && JsonNode.Parse(request.Body)!["eventCategory"]!.GetValue<string>().StartsWith("health.problem", StringComparison.Ordinal));
        Assert.Contains("Seerr", JsonNode.Parse(problem.Body)!["title"]!.GetValue<string>(), StringComparison.Ordinal);

        Volatile.Write(ref refusing, false);
        Assert.Equal("healthy", await TestAsync());
        await PassAsync();
        await PassAsync();
        var resolved = Assert.Single(phone.Requests, request => request.PathAndQuery == "/health" && JsonNode.Parse(request.Body)!["eventCategory"]!.GetValue<string>().StartsWith("health.resolved", StringComparison.Ordinal));
        Assert.EndsWith("is fine again", JsonNode.Parse(resolved.Body)!["title"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(2, Said().Length);
        Assert.DoesNotContain(phone.Requests, request => request.PathAndQuery == "/quiet");
    }
}
