using Deluno.Api;
using Deluno.Api.Backup;
using Deluno.Api.Updates;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Deluno.Persistence.Tests.Api;

/// <summary>What the System and Settings screens are told so they update with no reload and no timer (#1179).</summary>
public sealed class SystemLiveEventsTests
{
    [Theory]
    [InlineData("/api/tags", ConfigAreas.Tags)]
    [InlineData("/api/tags/bulk-connections", ConfigAreas.Tags)]
    [InlineData("/api/auto-tag-rules/run", ConfigAreas.AutoTagging)]
    [InlineData("/api/custom-formats/import", ConfigAreas.CustomFormats)]
    [InlineData("/api/quality-model", ConfigAreas.QualityModel)]
    [InlineData("/api/notification-webhooks/abc", ConfigAreas.Notifications)]
    [InlineData("/api/retention/rules", ConfigAreas.Retention)]
    [InlineData("/api/media-servers/test", ConfigAreas.MediaServers)]
    [InlineData("/api/subtitle-languages", ConfigAreas.Subtitles)]
    [InlineData("/api/security/allowed-hosts", ConfigAreas.Security)]
    [InlineData("/api/release-profiles/order", ConfigAreas.ReleaseRules)]
    [InlineData("/api/destination-rules/1", ConfigAreas.DestinationRules)]
    [InlineData("/api/download-health/ignore-list", ConfigAreas.DownloadHealth)]
    public void A_settings_write_names_its_area(string path, string area)
        => Assert.Equal(area, ConfigChangedPush.AreaFor(new PathString(path)));

    [Theory]
    [InlineData("/api/movies")]
    [InlineData("/api/tagsfoo")]
    [InlineData("/tags")]
    [InlineData("/api")]
    public void Any_other_path_names_no_area(string path)
        => Assert.Null(ConfigChangedPush.AreaFor(new PathString(path)));

    [Fact]
    public async Task A_successful_write_pushes_the_area_and_a_read_or_a_failure_does_not()
    {
        var publisher = new RecordingRealtimeEventPublisher();
        var middleware = ConfigChangedPush_Run(publisher, "POST", "/api/tags", 200);
        await middleware;
        Assert.Single(publisher.DashboardEvents, item => item.Name == SystemChangeEventNames.ConfigChanged);

        publisher = new RecordingRealtimeEventPublisher();
        await ConfigChangedPush_Run(publisher, "GET", "/api/tags", 200);
        await ConfigChangedPush_Run(publisher, "POST", "/api/tags", 400);
        Assert.Empty(publisher.DashboardEvents);
    }

    private static async Task ConfigChangedPush_Run(RecordingRealtimeEventPublisher publisher, string method, string path, int status)
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddSingleton<IRealtimeEventPublisher>(publisher)
            .BuildServiceProvider();
        var builder = new Microsoft.AspNetCore.Builder.ApplicationBuilder(services);
        builder.UseDelunoConfigChangedPush();
        builder.Run(context =>
        {
            context.Response.StatusCode = status;
            return Task.CompletedTask;
        });
        var pipeline = builder.Build();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = method;
        context.Request.Path = path;
        await pipeline(context);
    }

    [Fact]
    public async Task Saving_the_backup_schedule_and_the_update_preferences_push_their_own_events()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "deluno-system-live", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        try
        {
            var options = Options.Create(new StoragePathOptions { DataRoot = dataRoot });
            var publisher = new RecordingRealtimeEventPublisher();

            var backups = new DelunoBackupService(options, TimeProvider.System, NullLogger<DelunoBackupService>.Instance, realtime: publisher);
            await backups.SaveSettingsAsync(new UpdateBackupSettingsRequest(true, "daily", "03:00", 7, null), CancellationToken.None);
            Assert.Contains(publisher.Published, item => item.Type == "Backup");

            var recycle = new RecycleBinService(options, TimeProvider.System, NullLogger<RecycleBinService>.Instance, publisher);
            await recycle.SaveSettingsAsync(new RecycleBinSettings(14, 1024), CancellationToken.None);
            Assert.Contains(publisher.Published, item => item.Type == "RecycleBin");
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }
}
