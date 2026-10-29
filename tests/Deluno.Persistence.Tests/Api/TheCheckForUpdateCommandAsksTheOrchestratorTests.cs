using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Api.Updates;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The CheckForUpdate command (also answering to ApplicationUpdate): it asks the update orchestrator once, and the command it
/// returns is completed with the orchestrator's own wording, whether that is "up to date" or the feed's own error sentence.
/// </summary>
public sealed class TheCheckForUpdateCommandAsksTheOrchestratorTests
{
    private sealed class Orchestrator(string message, string state, bool updateAvailable) : IUpdateOrchestrator
    {
        public int Checks { get; private set; }

        private UpdateStatusResponse Status() => new("1.0.0", "stable", "windows", "manual", true, true, true, true, updateAvailable, null, state, null, false, null, null,
            message, null, []);

        public Task<UpdateStatusResponse> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(Status());

        public Task<UpdateStatusResponse> CheckForUpdatesAsync(CancellationToken cancellationToken)
        {
            Checks++;
            return Task.FromResult(Status());
        }

        public Task<UpdateStatusResponse> DownloadUpdatesAsync(CancellationToken cancellationToken) => Task.FromResult(Status());

        public Task<UpdateStatusResponse> ApplyAndRestartNowAsync(CancellationToken cancellationToken) => Task.FromResult(Status());

        public Task<UpdatePreferencesResponse> GetPreferencesAsync(CancellationToken cancellationToken)
            => Task.FromResult(new UpdatePreferencesResponse(UpdateModes.NotifyOnly, "stable", false));

        public Task<UpdatePreferencesResponse> SavePreferencesAsync(UpdatePreferencesRequest request, CancellationToken cancellationToken) => GetPreferencesAsync(cancellationToken);
    }

    [Theory]
    [InlineData("CheckForUpdate")]
    [InlineData("ApplicationUpdate")]
    public async Task The_command_asks_the_orchestrator_once_and_comes_back_completed_with_its_wording(string name)
    {
        var orchestrator = new Orchestrator("Couldn't check for updates: the update feed isn't reachable.", "error", false);
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.RemoveAll<IUpdateOrchestrator>();
            services.AddSingleton<IUpdateOrchestrator>(orchestrator);
        });

        using var response = await app.Client.PostAsJsonAsync("/api/command", new { name });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var command = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("CheckForUpdate", command.GetProperty("name").GetString());
        Assert.Equal("completed", command.GetProperty("status").GetString());
        Assert.Contains("Couldn't check for updates: the update feed isn't reachable.", command.GetRawText());
        Assert.Equal(1, orchestrator.Checks);
    }
}
