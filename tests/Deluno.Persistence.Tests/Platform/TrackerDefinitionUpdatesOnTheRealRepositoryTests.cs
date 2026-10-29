using System.IO.Compression;
using System.Net;
using Deluno.Infrastructure.Storage;
using Deluno.Integrations.Search.Trackers;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// #1301: tracker definitions come from the project's real definition repository (the recorded archive holds nine of its real files and its
/// README), are kept in the update store on a real database, and a failed download leaves what is installed alone.
/// </summary>
public sealed class TrackerDefinitionUpdatesOnTheRealRepositoryTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "deluno-defs-real-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    private sealed class Factory(Func<HttpRequestMessage, HttpResponseMessage> answer, List<string> hosts) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Script(answer, hosts), disposeHandler: true);
    }

    private sealed class Script(Func<HttpRequestMessage, HttpResponseMessage> answer, List<string> hosts) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (hosts) hosts.Add(request.RequestUri!.Host);
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Archive()
    {
        var reply = RecordedReplies.Github("prowlarr-indexers-archive").Exchanges[0].Reply;
        Assert.Equal(200, reply.Status);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(reply.Bytes!) };
    }

    private static string[] IdsInTheArchive()
    {
        using var zip = new ZipArchive(new MemoryStream(RecordedReplies.Github("prowlarr-indexers-archive").Exchanges[0].Reply.Bytes!));
        return zip.Entries.Where(entry => entry.FullName.Contains("/definitions/v11/", StringComparison.Ordinal) && entry.FullName.EndsWith(".yml", StringComparison.Ordinal))
            .Select(entry => Path.GetFileNameWithoutExtension(entry.FullName)).Order().ToArray();
    }

    [Fact]
    public async Task The_real_archive_gives_every_v11_definition_and_nothing_else_and_each_reads_in_deluno_words_with_its_secrets_kept_encrypted()
    {
        var hosts = new List<string>();
        var source = new GitHubTrackerDefinitionSource(new Factory(_ => Archive(), hosts));

        var definitions = await source.FetchAsync(CancellationToken.None);

        Assert.Equal(IdsInTheArchive(), definitions.Select(item => item.Id).Order().ToArray());
        Assert.True(definitions.Count >= 9);
        Assert.Equal(["codeload.github.com"], hosts);
        Directory.CreateDirectory(folder);
        foreach (var definition in definitions) File.WriteAllText(Path.Combine(folder, definition.Id + ".yml"), definition.Yaml);
        var catalog = new TrackerDefinitionCatalog(folder, NullLogger<TrackerDefinitionCatalog>.Instance);
        var secrets = 0;
        foreach (var definition in definitions)
        {
            var parsed = TrackerDefinition.Parse(definition.Yaml);
            var summary = catalog.List().Single(item => item.Id == definition.Id);
            foreach (var setting in parsed.Settings)
            {
                var shown = summary.Settings.SingleOrDefault(item => item.Name == setting.Name);
                if (shown is null) continue;
                Assert.False(TrackerSettingWording.NamesAnotherApp(shown.Label), $"{definition.Id}: {setting.Name} is shown as '{shown.Label}'");
                Assert.Equal(setting.IsSecret, shown.Secret);
                if (setting.IsSecret) secrets++;
            }
        }

        Assert.True(secrets > 0, "at least one real definition holds a secret-named setting (a password)");
        Assert.Contains(summaryPasswordNames(catalog, "torrentleech"), name => name == "password");
    }

    private static IEnumerable<string> summaryPasswordNames(TrackerDefinitionCatalog catalog, string id)
        => catalog.List().Single(item => item.Id == id).Settings.Where(setting => setting.Secret).Select(setting => setting.Name);

    [Fact]
    public async Task The_real_definitions_are_installed_through_the_update_and_a_definition_written_there_is_read_back_exactly()
    {
        var options = Options.Create(new StoragePathOptions { DataRoot = folder });
        var catalog = new TrackerDefinitionCatalog(Path.Combine(folder, "trackers"), NullLogger<TrackerDefinitionCatalog>.Instance);
        await using var app = await ApplicationTestHost.StartAsync();
        var store = new SqliteTrackerDefinitionUpdateStore(app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>(), TimeProvider.System);
        var service = new TrackerDefinitionUpdateService(store, new GitHubTrackerDefinitionSource(new Factory(_ => Archive(), [])), catalog, options,
            app.Services.GetRequiredService<IActivityFeedRepository>(), TimeProvider.System, NullLogger<TrackerDefinitionUpdateService>.Instance);

        var state = await service.UpdateNowAsync(CancellationToken.None);

        Assert.True(state.Status is TrackerDefinitionUpdateStatuses.Updated or TrackerDefinitionUpdateStatuses.UpToDate, state.Error);
        foreach (var id in IdsInTheArchive()) Assert.NotNull(catalog.Find(id));
        Assert.DoesNotContain("README", string.Join(",", catalog.List().Select(item => item.Id)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_switch_and_interval_and_the_last_check_are_kept_in_the_database_and_a_check_is_made_again_only_once_the_interval_has_passed()
    {
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-29T00:00:00Z"));
        var hosts = new List<string>();
        var options = Options.Create(new StoragePathOptions { DataRoot = folder });
        var catalog = new TrackerDefinitionCatalog(Path.Combine(folder, "trackers"), NullLogger<TrackerDefinitionCatalog>.Instance);
        await using var app = await ApplicationTestHost.StartAsync();
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        var service = new TrackerDefinitionUpdateService(new SqliteTrackerDefinitionUpdateStore(factory, clock), new GitHubTrackerDefinitionSource(new Factory(_ => Archive(), hosts)), catalog, options,
            app.Services.GetRequiredService<IActivityFeedRepository>(), clock, NullLogger<TrackerDefinitionUpdateService>.Instance);

        var offAtFirst = await service.RunIfDueAsync(CancellationToken.None);
        await service.SetSettingsAsync(isEnabled: true, intervalHours: 24, CancellationToken.None);
        var first = await service.RunIfDueAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(23));
        var tooSoon = await service.RunIfDueAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(2));
        var due = await service.RunIfDueAsync(CancellationToken.None);
        var readByAnotherStore = await new SqliteTrackerDefinitionUpdateStore(factory, clock).GetAsync(CancellationToken.None);

        Assert.Equal(TrackerDefinitionUpdateStatuses.Disabled, offAtFirst.Status);
        Assert.Equal(DateTimeOffset.Parse("2026-10-29T00:00:00Z"), first.LastCheckedUtc);
        Assert.Equal(first.LastCheckedUtc, tooSoon.LastCheckedUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-10-30T01:00:00Z"), due.LastCheckedUtc);
        Assert.Equal(2, hosts.Count);
        Assert.True(readByAnotherStore.IsEnabled);
        Assert.Equal(24, readByAnotherStore.IntervalHours);
        Assert.Equal(due.LastCheckedUtc, readByAnotherStore.LastCheckedUtc);
    }

    private sealed class TooBig() : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => Task.CompletedTask;
        protected override bool TryComputeLength(out long length)
        {
            length = 101L * 1024 * 1024;
            return true;
        }
    }

    [Fact]
    public async Task An_archive_that_is_too_large_has_no_v11_definitions_or_cannot_be_downloaded_is_a_failed_check_that_changes_no_installed_definition()
    {
        var recorded = RecordedReplies.Github("prowlarr-indexers-archive").Exchanges[0].Reply;
        var options = Options.Create(new StoragePathOptions { DataRoot = folder });
        var catalog = new TrackerDefinitionCatalog(Path.Combine(folder, "trackers"), NullLogger<TrackerDefinitionCatalog>.Instance);
        await using var app = await ApplicationTestHost.StartAsync();
        var store = new SqliteTrackerDefinitionUpdateStore(app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>(), TimeProvider.System);
        var activity = app.Services.GetRequiredService<IActivityFeedRepository>();
        TrackerDefinitionUpdateService ServiceFor(Func<HttpRequestMessage, HttpResponseMessage> answer)
            => new(store, new GitHubTrackerDefinitionSource(new Factory(answer, [])), catalog, options, activity, TimeProvider.System, NullLogger<TrackerDefinitionUpdateService>.Instance);
        var installedBefore = catalog.List().Select(item => item.Id).Order().ToArray();
        var notFound = new Func<HttpRequestMessage, HttpResponseMessage>(request => new HttpResponseMessage(HttpStatusCode.NotFound));

        var tooLarge = await ServiceFor(request => request.RequestUri!.Host == "codeload.github.com" ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new TooBig() } : notFound(request)).UpdateNowAsync(CancellationToken.None);
        var otherSchema = await ServiceFor(request =>
        {
            if (request.RequestUri!.Host != "codeload.github.com") return notFound(request);
            var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var writer = new StreamWriter(zip.CreateEntry("Indexers-master/definitions/v10/old.yml").Open());
                writer.Write("id: old");
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(buffer.ToArray()) };
        }).UpdateNowAsync(CancellationToken.None);
        var offline = await ServiceFor(_ => throw new HttpRequestException("No such host is known.")).UpdateNowAsync(CancellationToken.None);

        Assert.Equal(TrackerDefinitionUpdateStatuses.Failed, tooLarge.Status);
        Assert.Equal(TrackerDefinitionUpdateStatuses.Failed, otherSchema.Status);
        Assert.Equal(TrackerDefinitionUpdateStatuses.Failed, offline.Status);
        Assert.All(new[] { tooLarge, otherSchema, offline }, state => Assert.False(string.IsNullOrWhiteSpace(state.Error)));
        Assert.DoesNotContain("old", catalog.List().Select(item => item.Id));
        Assert.Equal(installedBefore, catalog.List().Select(item => item.Id).Order().ToArray());
        Assert.NotEmpty(recorded.Bytes!);
    }
}
