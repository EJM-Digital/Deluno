using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Deluno.Worker.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The Dashboard's processor cadences (#1282, #1261) against what Weir 1.0.0-rc.13 really answered to the capabilities call
/// (tests/fixtures/recorded): the real watcher, the real connection service and the real stored health, on a clock the test
/// moves. Weir answers its recorded "capabilities" while it is well, and the recorded 401 it gave for a wrong secret while it is not.
/// </summary>
public sealed class ProcessorHealthWatcherAgainstRecordedWeirTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private sealed class Rig(ApplicationTestHost app, RecordedAppServer weir, FakeTimeProvider clock, ProcessorHealthWatcher watcher, StrongBox<bool> refusing)
    {
        public ApplicationTestHost App => app;
        public RecordedAppServer Weir => weir;
        public bool Refusing { get => refusing.Value; set => refusing.Value = value; }
        public int Looks => weir.Requests.Count(request => request.PathAndQuery.Contains("/intake/capabilities", StringComparison.Ordinal));

        public async Task StepAsync(int seconds)
        {
            clock.Advance(TimeSpan.FromSeconds(seconds));
            await Task.WhenAll(await watcher.TickAsync(CancellationToken.None));
        }

        public async Task<string?> StoredAsync(string id)
        {
            var connections = await app.Client.GetFromJsonAsync<JsonElement>("/api/integrations/processors/connections");
            return connections.EnumerateArray().Single(item => item.GetProperty("id").GetString() == id).GetProperty("healthStatus").GetString();
        }
    }

    private sealed class StrongBox<T>(T value) { public T Value = value; }

    private static async Task<Rig> StartAsync()
    {
        var refusing = new StrongBox<bool>(false);
        var refusal = RecordedReplies.Weir("capabilities-wrong-secret").Single.Reply;
        var weir = await ProcessorRoutesAgainstRecordedWeirTests.WeirAsync((_, _) => refusing.Value ? refusal.ToResponse() : null, "capabilities");
        // The host's own watcher runs on the real clock; it is left out so that the only looks are the ones this test makes.
        var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
            services.RemoveAll<IHostedService>());
        var clock = new FakeTimeProvider(Start);
        var watcher = new ProcessorHealthWatcher(NullLogger<ProcessorHealthWatcher>.Instance, app.Services.GetRequiredService<IServiceScopeFactory>(), clock);
        return new Rig(app, weir, clock, watcher, refusing);
    }

    [Fact]
    public async Task A_well_processor_is_looked_at_every_thirty_seconds_a_poorly_one_every_fifteen_and_the_return_is_seen_at_the_next_look()
    {
        var rig = await StartAsync();
        await using var _ = rig.Weir; await using var __ = rig.App;
        var connection = await ProcessorRoutesAgainstRecordedWeirTests.ConnectAsync(rig.App, rig.Weir);
        var id = connection.GetProperty("id").GetString()!;

        // A new processor is asked at once, and Weir's recorded answer makes it healthy.
        var start = rig.Looks;
        await rig.StepAsync(0);
        Assert.Equal(start + 1, rig.Looks);
        Assert.Equal("healthy", await rig.StoredAsync(id));

        // Well: not again until thirty seconds on the schedule have passed.
        await rig.StepAsync(29);
        Assert.Equal(start + 1, rig.Looks);
        await rig.StepAsync(1);
        Assert.Equal(start + 2, rig.Looks);

        // Weir starts refusing: the next look, thirty seconds on, stores it as not well.
        rig.Refusing = true;
        await rig.StepAsync(30);
        Assert.Equal(start + 3, rig.Looks);
        Assert.Equal("degraded", await rig.StoredAsync(id));

        // Not well: looked at every fifteen seconds, not thirty.
        await rig.StepAsync(14);
        Assert.Equal(start + 3, rig.Looks);
        await rig.StepAsync(1);
        Assert.Equal(start + 4, rig.Looks);
        await rig.StepAsync(15);
        Assert.Equal(start + 5, rig.Looks);

        // Weir answers again: the very next look (inside fifteen seconds) stores it healthy, and the slow cadence returns.
        rig.Refusing = false;
        await rig.StepAsync(15);
        Assert.Equal(start + 6, rig.Looks);
        Assert.Equal("healthy", await rig.StoredAsync(id));
        await rig.StepAsync(15);
        Assert.Equal(start + 6, rig.Looks);
        await rig.StepAsync(15);
        Assert.Equal(start + 7, rig.Looks);
    }

    [Fact]
    public async Task A_processor_that_is_switched_off_is_never_asked_and_a_new_one_is_asked_at_once()
    {
        var rig = await StartAsync();
        await using var _ = rig.Weir; await using var __ = rig.App;
        var off = (await ProcessorRoutesAgainstRecordedWeirTests.ConnectAsync(rig.App, rig.Weir)).GetProperty("id").GetString()!;
        var switchedOff = await rig.App.Client.PutAsJsonAsync($"/api/integrations/processors/connections/{off}", new { provider = "weir", submissionUrl = rig.Weir.BaseUrl + "/api/v1/intake/webhook/deluno", authHeaderName = "X-Webhook-Secret", secret = (string?)null, isEnabled = false });
        Assert.True(switchedOff.IsSuccessStatusCode, await switchedOff.Content.ReadAsStringAsync());
        var start = rig.Looks;

        await rig.StepAsync(0);
        await rig.StepAsync(60);
        await rig.StepAsync(60);
        Assert.Equal(start, rig.Looks);

        var added = await rig.App.Client.PostAsJsonAsync("/api/integrations/processors/connections", new { provider = "weir", submissionUrl = rig.Weir.BaseUrl + "/api/v1/intake/webhook/deluno", authHeaderName = "X-Webhook-Secret", secret = "secret", isEnabled = true, name = "A second Weir" });
        Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync());
        var before = rig.Looks;

        await rig.StepAsync(0);

        Assert.Equal(before + 1, rig.Looks);
    }
}
