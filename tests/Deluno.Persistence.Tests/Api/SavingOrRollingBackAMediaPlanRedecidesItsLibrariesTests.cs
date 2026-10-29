using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Quality;
using Deluno.Quality.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Creating, saving and rolling back a media plan (coverage register, engines): each one asks the re-evaluator to re-decide the
/// libraries that use the plan, and a save that is refused asks for nothing. The whole application runs; only the re-evaluator,
/// which would otherwise touch titles, is a recorder, so the route's own call is what is seen.
/// </summary>
public sealed class SavingOrRollingBackAMediaPlanRedecidesItsLibrariesTests
{
    private sealed class Recorder : IProfileChangeReevaluator
    {
        public List<string> PolicySets { get; } = [];

        public Task<int> ReevaluateForProfileAsync(string profileId, CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<int> ReevaluateForLibraryAsync(string libraryId, CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<int> ReevaluateForPolicySetAsync(string policySetId, CancellationToken cancellationToken)
        {
            lock (PolicySets)
            {
                PolicySets.Add(policySetId);
            }

            return Task.FromResult(0);
        }

        public Task ReportProfileDeletedAsync(QualityProfileRemoval removal, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static async Task<string> CreateAsync(ApplicationTestHost app, string path, object body)
    {
        using var response = await app.Client.PostAsJsonAsync(path, body);
        Assert.True(response.IsSuccessStatusCode, path + ": " + await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static object Plan(string name, string profileId, bool upgradeUntilCutoff = true)
        => new { name, mediaType = "movies", qualityProfileId = profileId, customFormatIds = string.Empty, upgradeUntilCutoff, isEnabled = true };

    [Fact]
    public async Task Creating_saving_and_rolling_back_a_plan_each_re_decide_its_libraries_and_a_refused_save_asks_for_nothing()
    {
        var recorder = new Recorder();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            foreach (var existing in services.Where(service => service.ServiceType == typeof(IProfileChangeReevaluator)).ToArray())
            {
                services.Remove(existing);
            }

            services.AddSingleton<IProfileChangeReevaluator>(recorder);
        });
        var tiers = (await app.Client.GetFromJsonAsync<JsonElement>("/api/quality-model")).GetProperty("tiers")
            .EnumerateArray().Select(tier => tier.GetProperty("name").GetString()!).ToArray();
        var profile = await CreateAsync(app, "/api/quality-profiles", new { name = "Films profile", mediaType = "movies", cutoffQuality = tiers[0], allowedQualities = string.Join(",", tiers), customFormatIds = string.Empty, upgradeUntilCutoff = true });

        var plan = await CreateAsync(app, "/api/policy-sets", Plan("Films plan", profile));
        Assert.Equal([plan], recorder.PolicySets);

        using var saved = await app.Client.PutAsJsonAsync($"/api/policy-sets/{plan}", Plan("Films plan", profile, upgradeUntilCutoff: false));
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        Assert.Equal([plan, plan], recorder.PolicySets);

        using var rolledBack = await app.Client.PostAsJsonAsync($"/api/policy-sets/{plan}/rollback", new { version = 1 });
        Assert.True(rolledBack.IsSuccessStatusCode, await rolledBack.Content.ReadAsStringAsync());
        Assert.Equal([plan, plan, plan], recorder.PolicySets);

        using var refused = await app.Client.PutAsJsonAsync($"/api/policy-sets/{plan}", Plan("Films plan", "no-such-profile"));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(3, recorder.PolicySets.Count);
    }
}
