using System.Net;
using System.Net.Http.Json;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Notifications;
using Deluno.Notifications.Contracts;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #856: beyond import-failed, the only other outbound trigger was health
/// (#859). This covers the two new triggers this issue adds — On Grab and
/// On Title Added — each defaulting off so nothing new fires unannounced,
/// and each firing (or staying silent) per its own toggle.
/// </summary>
public sealed class LifecycleNotificationTriggerTests
{
    private const string Release = "Wanted.Film.2011.1080p.WEB-DL-DELUNO";

    [Fact]
    public async Task A_sent_grab_dispatches_grab_when_the_toggle_is_on()
    {
        var outbound = new RecordingOutboundNotificationService();
        await using var app = await ApplicationTestHost.StartAsync(
            replaceServices: services => services.AddSingleton<IOutboundNotificationService>(outbound));
        await SetPreferenceAsync(app, prefs => prefs.GrabEnabled = true);
        var library = await CreateLibraryAsync(app);

        await app.Services.GetRequiredService<IJobQueueRepository>().RecordDownloadDispatchAsync(
            library.Id, "movies", "movie", "title-wanted", Release, "Fixture", "client-1", "qBittorrent", "sent", null,
            cancellationToken: CancellationToken.None);

        Assert.Contains("grab", outbound.DispatchedCategories);
    }

    [Fact]
    public async Task A_sent_grab_stays_silent_when_the_toggle_is_off()
    {
        var outbound = new RecordingOutboundNotificationService();
        await using var app = await ApplicationTestHost.StartAsync(
            replaceServices: services => services.AddSingleton<IOutboundNotificationService>(outbound));
        var library = await CreateLibraryAsync(app);

        await app.Services.GetRequiredService<IJobQueueRepository>().RecordDownloadDispatchAsync(
            library.Id, "movies", "movie", "title-wanted", Release, "Fixture", "client-1", "qBittorrent", "sent", null,
            cancellationToken: CancellationToken.None);

        Assert.DoesNotContain("grab", outbound.DispatchedCategories);
    }

    [Fact]
    public async Task A_failed_grab_never_dispatches_grab()
    {
        var outbound = new RecordingOutboundNotificationService();
        await using var app = await ApplicationTestHost.StartAsync(
            replaceServices: services => services.AddSingleton<IOutboundNotificationService>(outbound));
        await SetPreferenceAsync(app, prefs => prefs.GrabEnabled = true);
        var library = await CreateLibraryAsync(app);

        await app.Services.GetRequiredService<IJobQueueRepository>().RecordDownloadDispatchAsync(
            library.Id, "movies", "movie", "title-wanted", Release, "Fixture", "client-1", "qBittorrent", "failed", null,
            cancellationToken: CancellationToken.None);

        Assert.DoesNotContain("grab", outbound.DispatchedCategories);
    }

    [Fact]
    public async Task Adding_a_new_movie_dispatches_title_added_when_the_toggle_is_on()
    {
        var outbound = new RecordingOutboundNotificationService();
        await using var app = await ApplicationTestHost.StartAsync(
            replaceServices: services => services.AddSingleton<IOutboundNotificationService>(outbound));
        await SetPreferenceAsync(app, prefs => prefs.AddedEnabled = true);

        var response = await app.Client.PostAsJsonAsync("/api/movies", new { title = "A Brand New Film", releaseYear = 2024, monitored = true });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Contains("title.added", outbound.DispatchedCategories);
    }

    [Fact]
    public async Task Re_adding_the_same_movie_does_not_dispatch_title_added_again()
    {
        var outbound = new RecordingOutboundNotificationService();
        await using var app = await ApplicationTestHost.StartAsync(
            replaceServices: services => services.AddSingleton<IOutboundNotificationService>(outbound));
        await SetPreferenceAsync(app, prefs => prefs.AddedEnabled = true);

        await app.Client.PostAsJsonAsync("/api/movies", new { title = "A Repeated Film", releaseYear = 2024, monitored = true });
        outbound.DispatchedCategories.Clear();

        var response = await app.Client.PostAsJsonAsync("/api/movies", new { title = "A Repeated Film", releaseYear = 2024, monitored = true });

        Assert.True(response.IsSuccessStatusCode);
        Assert.DoesNotContain("title.added", outbound.DispatchedCategories);
    }

    [Fact]
    public async Task Adding_a_movie_stays_silent_when_the_toggle_is_off()
    {
        var outbound = new RecordingOutboundNotificationService();
        await using var app = await ApplicationTestHost.StartAsync(
            replaceServices: services => services.AddSingleton<IOutboundNotificationService>(outbound));

        var response = await app.Client.PostAsJsonAsync("/api/movies", new { title = "A Quiet Film", releaseYear = 2024, monitored = true });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.DoesNotContain("title.added", outbound.DispatchedCategories);
    }

    private static async Task<LibraryItem> CreateLibraryAsync(ApplicationTestHost app)
        => await app.Services.GetRequiredService<ILibrariesRepository>().CreateLibraryAsync(
            new CreateLibraryRequest(
                "Films", "movies", "library", Path.Combine(app.DataRoot, "movies"), Path.Combine(app.DataRoot, "downloads"), null,
                "direct-import", null, null, null, null, true, true, true, 12, 36, 25),
            CancellationToken.None);

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

        public Task DispatchAsync(string eventCategory, string title, string message, string? detailsJson, CancellationToken cancellationToken, IReadOnlyList<string>? titleTags = null)
        {
            DispatchedCategories.Add(eventCategory);
            return Task.CompletedTask;
        }

        public Task<Notifications.Contracts.NotificationWebhookDeliveryResult?> DispatchToWebhookAsync(string webhookId, string eventCategory, string title, string message, string? detailsJson, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<Notifications.Contracts.NotificationWebhookDeliveryResult?> ReplayAsync(string deliveryId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
