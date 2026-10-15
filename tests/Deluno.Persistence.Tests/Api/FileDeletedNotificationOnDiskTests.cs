using System.Net;
using System.Net.Http.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Notifications;
using Deluno.Notifications.Contracts;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// "File delete" notification (#856) on the whole application over real files: removing a title with its files from the
/// library fires it only when the switch is on and only when the files really left the disk; keeping the files fires nothing.
/// </summary>
public sealed class FileDeletedNotificationOnDiskTests
{
    private static async Task SetFileDeletedAsync(ApplicationTestHost app, bool enabled)
    {
        var current = await app.Client.GetFromJsonAsync<NotificationPreferences>("/api/notification-preferences");
        current!.FileDeletedEnabled = enabled;
        var saved = await app.Client.PutAsJsonAsync("/api/notification-preferences", current);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
    }

    [Fact]
    public async Task Removing_a_film_with_its_files_sends_file_deleted_once_the_file_is_really_off_the_disk_when_the_switch_is_on()
    {
        var outbound = new Recorder();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<IOutboundNotificationService>(outbound));
        await SetFileDeletedAsync(app, true);
        var film = await ApiScenes.FilmOnDiskAsync(app, "Dementia 13", 1963);
        Assert.True(File.Exists(film.FilePath));

        var removed = await app.Client.DeleteAsync($"/api/movies/{film.MovieId}?deleteFiles=true");

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.False(File.Exists(film.FilePath), "the file should have left the library folder");
        Assert.Equal(1, outbound.Categories.Count(category => category == "file.deleted"));
        Assert.Contains("Dementia 13", outbound.Messages.Single(m => m.Category == "file.deleted").Text);
    }

    [Fact]
    public async Task Removing_a_film_with_its_files_sends_nothing_when_the_switch_is_off_but_the_file_still_goes()
    {
        var outbound = new Recorder();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<IOutboundNotificationService>(outbound));
        await SetFileDeletedAsync(app, false);
        var film = await ApiScenes.FilmOnDiskAsync(app, "Carnival of Souls", 1962);

        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/movies/{film.MovieId}?deleteFiles=true")).StatusCode);

        Assert.False(File.Exists(film.FilePath));
        Assert.DoesNotContain("file.deleted", outbound.Categories);
    }

    [Fact]
    public async Task Removing_a_film_and_keeping_its_files_never_sends_file_deleted_even_with_the_switch_on()
    {
        var outbound = new Recorder();
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<IOutboundNotificationService>(outbound));
        await SetFileDeletedAsync(app, true);
        var film = await ApiScenes.FilmOnDiskAsync(app, "Night of the Living Dead", 1968);

        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/movies/{film.MovieId}")).StatusCode);

        Assert.True(File.Exists(film.FilePath), "the files were kept, so the file must still be on disk");
        Assert.DoesNotContain("file.deleted", outbound.Categories);
    }

    private sealed class Recorder : IOutboundNotificationService
    {
        public List<string> Categories { get; } = [];

        public List<(string Category, string Text)> Messages { get; } = [];

        public Task DispatchAsync(string eventCategory, string title, string message, string? detailsJson, CancellationToken cancellationToken, IReadOnlyList<string>? titleTags = null)
        {
            Categories.Add(eventCategory);
            Messages.Add((eventCategory, $"{title} {message}"));
            return Task.CompletedTask;
        }

        public Task<NotificationWebhookDeliveryResult?> DispatchToWebhookAsync(string webhookId, string eventCategory, string title, string message, string? detailsJson, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<NotificationWebhookDeliveryResult?> ReplayAsync(string deliveryId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
