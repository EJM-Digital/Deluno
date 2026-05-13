using Deluno.Api;
using Deluno.Api.Downloads;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Data.Common;
using System.Net;
using System.Text.Json;

namespace Deluno.Persistence.Tests.Api;

public sealed class DownloadDispatchesApiTests : IAsyncDisposable
{
    private readonly TestStorage _storage;
    private readonly IHost _host;
    private readonly TestServer _server;
    private readonly HttpClient _client;
    private readonly IDownloadDispatchesRepository _repository;
    private readonly IServiceProvider _services;

    public DownloadDispatchesApiTests()
    {
        _storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T04:00:00Z"));

        var builder = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton<TimeProvider>(timeProvider);
                        services.AddSingleton(_storage.Factory);
                        services.AddDelunoApi();
                        services.AddSingleton<IJobScheduler, TestJobScheduler>();
                        services.AddSingleton<IDownloadDispatchesRepository>(
                            new SqliteDownloadDispatchesRepository(_storage.Factory, timeProvider));
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapDelunoApi());
                    });
            });

        _host = builder.Build();
        _host.StartAsync().GetAwaiter().GetResult();

        _server = _host.GetTestServer();
        _client = _server.CreateClient();
        _services = _server.Services;
        _repository = _services.GetRequiredService<IDownloadDispatchesRepository>();

        InitializeSchema(timeProvider).GetAwaiter().GetResult();
    }

    private async Task InitializeSchema(TimeProvider timeProvider)
    {
        await new JobsSchemaInitializer(
            _storage.Factory,
            new SqliteDatabaseMigrator(_storage.Factory, timeProvider),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
    }

    private async Task InsertDispatchAsync(string dispatchId, string libraryId, string entityId, string releaseName)
    {
        await using var connection = await _storage.Factory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            CancellationToken.None);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO download_dispatches (
                id, library_id, media_type, entity_type, entity_id, release_name,
                indexer_name, download_client_id, download_client_name, status, created_utc
            ) VALUES (
                @id, @libraryId, 'movie', 'movie', @entityId, @releaseName,
                'test-indexer', 'qbittorrent-main', 'qBittorrent', 'initial', datetime('now')
            )
            """;

        var idParam = command.CreateParameter();
        idParam.ParameterName = "@id";
        idParam.Value = dispatchId;
        command.Parameters.Add(idParam);

        var libParam = command.CreateParameter();
        libParam.ParameterName = "@libraryId";
        libParam.Value = libraryId;
        command.Parameters.Add(libParam);

        var entityParam = command.CreateParameter();
        entityParam.ParameterName = "@entityId";
        entityParam.Value = entityId;
        command.Parameters.Add(entityParam);

        var nameParam = command.CreateParameter();
        nameParam.ParameterName = "@releaseName";
        nameParam.Value = releaseName;
        command.Parameters.Add(nameParam);

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    [Fact]
    public async Task GetDispatches_returns_paginated_list()
    {
        var dispatchId = Guid.CreateVersion7().ToString("N");
        await InsertDispatchAsync(dispatchId, "movies-main", "123", "Test.Movie.2024.1080p");

        await _repository.RecordGrabAsync(
            dispatchId: dispatchId,
            grabStatus: "succeeded",
            grabResponseCode: 200,
            grabMessage: "ok",
            grabFailureCode: null,
            grabResponseJson: null,
            CancellationToken.None);

        var response = await _client.GetAsync("/api/v1/download-dispatches");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        var dispatches = json.RootElement.GetProperty("dispatches");
        Assert.True(dispatches.GetArrayLength() > 0);
    }

    [Fact]
    public async Task GetDispatch_returns_single_dispatch_with_timeline()
    {
        var dispatchId = Guid.CreateVersion7().ToString("N");
        await InsertDispatchAsync(dispatchId, "movies-main", "123", "Test.Movie.2024.1080p");

        await _repository.RecordGrabAsync(
            dispatchId: dispatchId,
            grabStatus: "succeeded",
            grabResponseCode: 200,
            grabMessage: "ok",
            grabFailureCode: null,
            grabResponseJson: null,
            CancellationToken.None);

        var response = await _client.GetAsync($"/api/v1/download-dispatches/{dispatchId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        Assert.Equal(dispatchId, json.RootElement.GetProperty("dispatch").GetProperty("id").GetString());
        var timeline = json.RootElement.GetProperty("timeline");
        Assert.True(timeline.ValueKind != JsonValueKind.Undefined);
    }

    [Fact]
    public async Task GetDispatch_returns_404_for_nonexistent_dispatch()
    {
        var response = await _client.GetAsync("/api/v1/download-dispatches/nonexistent");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetUnresolvedDispatches_returns_grabs_not_detected()
    {
        var dispatchId = Guid.CreateVersion7().ToString("N");
        await InsertDispatchAsync(dispatchId, "movies-main", "123", "Test.Movie.2024.1080p");

        await _repository.RecordGrabAsync(
            dispatchId: dispatchId,
            grabStatus: "succeeded",
            grabResponseCode: 200,
            grabMessage: "ok",
            grabFailureCode: null,
            grabResponseJson: null,
            CancellationToken.None);

        var response = await _client.GetAsync("/api/v1/download-dispatches/unresolved?minAgeMinutes=0");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        var dispatches = json.RootElement.GetProperty("dispatches");
        Assert.True(dispatches.GetArrayLength() > 0);
    }

    [Fact]
    public async Task RetryDispatch_returns_202_for_failed_grab()
    {
        var dispatchId = Guid.CreateVersion7().ToString("N");
        await InsertDispatchAsync(dispatchId, "movies-main", "123", "Test.Movie.2024.1080p");

        await _repository.RecordGrabAsync(
            dispatchId: dispatchId,
            grabStatus: "failed",
            grabResponseCode: 403,
            grabMessage: "forbidden",
            grabFailureCode: "access_denied",
            grabResponseJson: null,
            CancellationToken.None);

        var response = await _client.PostAsync($"/api/v1/download-dispatches/{dispatchId}/retry", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        Assert.Equal(dispatchId, json.RootElement.GetProperty("dispatchId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("jobId").GetString()));
    }

    [Fact]
    public async Task ArchiveDispatch_returns_204()
    {
        var dispatchId = Guid.CreateVersion7().ToString("N");
        await InsertDispatchAsync(dispatchId, "movies-main", "123", "Test.Movie.2024.1080p");

        await _repository.RecordGrabAsync(
            dispatchId: dispatchId,
            grabStatus: "succeeded",
            grabResponseCode: 200,
            grabMessage: "ok",
            grabFailureCode: null,
            grabResponseJson: null,
            CancellationToken.None);

        var response = await _client.DeleteAsync($"/api/v1/download-dispatches/{dispatchId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify it's archived
        var getResponse = await _client.GetAsync($"/api/v1/download-dispatches/{dispatchId}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task GetImportResolutions_returns_imported_dispatches()
    {
        var dispatchId = Guid.CreateVersion7().ToString("N");
        await InsertDispatchAsync(dispatchId, "movies-main", "123", "Test.Movie.2024.1080p");

        await _repository.RecordImportOutcomeAsync(
            dispatchId: dispatchId,
            importStatus: "imported",
            importedFilePath: "/library/file.mkv",
            importFailureCode: null,
            importFailureMessage: null,
            CancellationToken.None);

        var response = await _client.GetAsync("/api/v1/import-resolutions?status=imported");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        var resolutions = json.RootElement.GetProperty("resolutions");
        Assert.True(resolutions.GetArrayLength() > 0);
        Assert.Equal("imported", resolutions[0].GetProperty("status").GetString());
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        _storage?.Dispose();
    }

    private sealed class TestJobScheduler : IJobScheduler
    {
        public Task<JobQueueItem> EnqueueAsync(EnqueueJobRequest request, CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            var item = new JobQueueItem(
                Id: $"job-{Guid.CreateVersion7():N}",
                JobType: request.JobType,
                Source: request.Source,
                Status: "queued",
                PayloadJson: request.PayloadJson,
                Attempts: 0,
                CreatedUtc: now,
                ScheduledUtc: request.ScheduledUtc ?? now,
                StartedUtc: null,
                CompletedUtc: null,
                LeasedUntilUtc: null,
                WorkerId: null,
                LastError: null,
                RelatedEntityType: request.RelatedEntityType,
                RelatedEntityId: request.RelatedEntityId,
                IdempotencyKey: request.IdempotencyKey,
                DedupeKey: request.DedupeKey,
                MaxAttempts: request.MaxAttempts ?? 6,
                LastAttemptUtc: null,
                NextAttemptUtc: null);
            return Task.FromResult(item);
        }
    }
}
