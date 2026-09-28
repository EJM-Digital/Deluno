using System.Net.Http.Json;
using Deluno.Notifications;
using Deluno.Notifications.Contracts;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #966: forced import (a title's own "force a re-download" button, written
/// to Activity as "acquisition.override") and strikes purged
/// ("download.health.strikes-purged") already landed in Activity but never
/// reached the outbound path. Each gets its own preference, off by default
/// (#936), the same shape as the four categories #858 already covered.
/// </summary>
public sealed class ForcedImportAndStrikesPurgedNotificationTests
{
    [Fact]
    public async Task Forcing_a_redownload_dispatches_acquisition_override_when_the_toggle_is_on()
    {
        var outbound = new RecordingOutboundNotificationService();
        await using var app = await ApplicationTestHost.StartAsync(
            replaceServices: services => services.AddSingleton<IOutboundNotificationService>(outbound));
        await SetPreferenceAsync(app, prefs => prefs.ForcedImportEnabled = true);
        var id = await AddMovieAsync(app);

        var response = await app.Client.PostAsync($"/api/movies/{id}/force-redownload", null);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        Assert.Contains("acquisition.override", outbound.DispatchedCategories);
    }

    [Fact]
    public async Task Forcing_a_redownload_stays_silent_when_the_toggle_is_off()
    {
        var outbound = new RecordingOutboundNotificationService();
        await using var app = await ApplicationTestHost.StartAsync(
            replaceServices: services => services.AddSingleton<IOutboundNotificationService>(outbound));
        var id = await AddMovieAsync(app);

        var response = await app.Client.PostAsync($"/api/movies/{id}/force-redownload", null);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        Assert.DoesNotContain("acquisition.override", outbound.DispatchedCategories);
    }

    [Fact]
    public async Task Purging_strikes_dispatches_strikes_purged_when_the_toggle_is_on()
    {
        var outbound = new RecordingOutboundNotificationService();
        await using var app = await ApplicationTestHost.StartAsync(
            replaceServices: services => services.AddSingleton<IOutboundNotificationService>(outbound));
        await SetPreferenceAsync(app, prefs => prefs.StrikesPurgedEnabled = true);

        var response = await app.Client.PostAsync("/api/download-health/purge-strikes", null);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        Assert.Contains("download.health.strikes-purged", outbound.DispatchedCategories);
    }

    [Fact]
    public async Task Purging_strikes_stays_silent_when_the_toggle_is_off()
    {
        var outbound = new RecordingOutboundNotificationService();
        await using var app = await ApplicationTestHost.StartAsync(
            replaceServices: services => services.AddSingleton<IOutboundNotificationService>(outbound));

        var response = await app.Client.PostAsync("/api/download-health/purge-strikes", null);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        Assert.DoesNotContain("download.health.strikes-purged", outbound.DispatchedCategories);
    }

    private static async Task<string> AddMovieAsync(ApplicationTestHost app)
    {
        var response = await app.Client.PostAsJsonAsync("/api/movies", new { title = "Arrival", releaseYear = 2016, monitored = true });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return body.GetProperty("id").GetString()!;
    }

    private static async Task SetPreferenceAsync(ApplicationTestHost app, Action<NotificationPreferences> mutate)
    {
        var service = app.Services.GetRequiredService<INotificationService>();
        var current = await service.GetPreferencesAsync(CancellationToken.None);
        mutate(current);
        await service.UpdatePreferencesAsync(current, CancellationToken.None);
    }

    private sealed class RecordingOutboundNotificationService : IOutboundNotificationService
    {
        public List<string> DispatchedCategories { get; } = [];

        public Task DispatchAsync(string eventCategory, string title, string message, string? detailsJson, CancellationToken cancellationToken)
        {
            DispatchedCategories.Add(eventCategory);
            return Task.CompletedTask;
        }

        public Task<NotificationWebhookDeliveryResult?> DispatchToWebhookAsync(string webhookId, string eventCategory, string title, string message, string? detailsJson, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<NotificationWebhookDeliveryResult?> ReplayAsync(string deliveryId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
