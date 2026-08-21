using Deluno.Api.Backup;
using Deluno.Api.Updates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Net;
using System.Text.Json;

namespace Deluno.Persistence.Tests.Api;

public sealed class UpdateBackupEndpointTests : IAsyncDisposable
{
    private readonly RecordingBackupService _backupService = new();
    private readonly RecordingUpdateOrchestrator _orchestrator = new();
    private readonly IHost _host;
    private readonly HttpClient _client;

    public UpdateBackupEndpointTests()
    {
        var builder = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddSingleton<IDelunoBackupService>(_backupService);
                        services.AddSingleton<IUpdateOrchestrator>(_orchestrator);
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapDelunoBackupEndpoints());
                    });
            });

        _host = builder.Build();
        _host.Start();
        _client = _host.GetTestServer().CreateClient();
    }

    [Fact]
    public async Task ApplyOnRestart_creates_pre_update_backup_before_staging()
    {
        var response = await _client.PostAsync("/api/updates/apply-on-restart", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var result = document.RootElement;

        Assert.True(result.GetProperty("accepted").GetBoolean());
        Assert.Equal("Backup completed. Update is prepared for restart.", result.GetProperty("message").GetString());
        Assert.Equal("pre-update", _backupService.LastReason);
        Assert.Equal(1, _orchestrator.PrepareCalls);
        Assert.Equal(0, _orchestrator.GetStatusCalls);
    }

    [Fact]
    public async Task ApplyOnRestart_blocks_staging_when_pre_update_backup_fails()
    {
        _backupService.Failure = new IOException("backup folder is unavailable");

        var response = await _client.PostAsync("/api/updates/apply-on-restart", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var result = document.RootElement;

        Assert.False(result.GetProperty("accepted").GetBoolean());
        Assert.Contains("Backup failed and staging was blocked", result.GetProperty("message").GetString());
        Assert.Equal("pre-update", _backupService.LastReason);
        Assert.Equal(0, _orchestrator.PrepareCalls);
        Assert.Equal(1, _orchestrator.GetStatusCalls);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    private static UpdateStatusResponse CreateStatus(string message) => new(
        CurrentVersion: "1.0.0",
        Channel: "stable",
        InstallKind: UpdateInstallKinds.Manual,
        BehaviorMode: UpdateModes.NotifyOnly,
        IsInstalled: false,
        CanCheck: false,
        CanDownload: false,
        CanApply: false,
        UpdateAvailable: false,
        LatestVersion: null,
        State: UpdateStates.NotSupported,
        ProgressPercent: null,
        RestartRequired: false,
        LastCheckedUtc: null,
        LastDownloadedUtc: null,
        Message: message,
        LastError: null,
        Notes: Array.Empty<string>());

    private sealed class RecordingBackupService : IDelunoBackupService
    {
        public string? LastReason { get; private set; }

        public Exception? Failure { get; set; }

        public Task<BackupSettingsSnapshot> GetSettingsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new BackupSettingsSnapshot(false, "daily", "02:00", 10, "backups", null, null));

        public Task<BackupSettingsSnapshot> SaveSettingsAsync(
            UpdateBackupSettingsRequest request,
            CancellationToken cancellationToken) => GetSettingsAsync(cancellationToken);

        public Task<IReadOnlyList<BackupItem>> ListBackupsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BackupItem>>(Array.Empty<BackupItem>());

        public Task<BackupItem> CreateBackupAsync(string reason, CancellationToken cancellationToken)
        {
            LastReason = reason;
            return Failure is null
                ? Task.FromResult(new BackupItem("backup-id", "backup.zip", "C:\\backups\\backup.zip", 1, DateTimeOffset.UtcNow, reason))
                : Task.FromException<BackupItem>(Failure);
        }

        public Task<(Stream Stream, string ContentType, string FileName)?> OpenBackupAsync(
            string id,
            CancellationToken cancellationToken) =>
            Task.FromResult<(Stream Stream, string ContentType, string FileName)?>(null);

        public Task<bool> DeleteBackupAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<RestorePreviewResponse> PreviewRestoreAsync(Stream backupStream, CancellationToken cancellationToken) =>
            Task.FromResult(new RestorePreviewResponse(false, "not implemented", null, Array.Empty<string>()));

        public Task<RestoreResultResponse> RestoreAsync(Stream backupStream, CancellationToken cancellationToken) =>
            Task.FromResult(new RestoreResultResponse(false, "not implemented", string.Empty, Array.Empty<string>()));
    }

    private sealed class RecordingUpdateOrchestrator : IUpdateOrchestrator
    {
        public int GetStatusCalls { get; private set; }

        public int PrepareCalls { get; private set; }

        public Task<UpdateStatusResponse> GetStatusAsync(CancellationToken cancellationToken)
        {
            GetStatusCalls++;
            return Task.FromResult(CreateStatus("status"));
        }

        public Task<UpdateStatusResponse> CheckForUpdatesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(CreateStatus("checked"));

        public Task<UpdateStatusResponse> DownloadUpdatesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(CreateStatus("downloaded"));

        public Task<UpdateStatusResponse> PrepareApplyOnNextRestartAsync(CancellationToken cancellationToken)
        {
            PrepareCalls++;
            return Task.FromResult(CreateStatus("prepared"));
        }

        public Task<UpdateStatusResponse> ApplyAndRestartNowAsync(CancellationToken cancellationToken) =>
            Task.FromResult(CreateStatus("restarted"));

        public Task<UpdatePreferencesResponse> GetPreferencesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new UpdatePreferencesResponse(UpdateModes.NotifyOnly, "stable", false));

        public Task<UpdatePreferencesResponse> SavePreferencesAsync(
            UpdatePreferencesRequest request,
            CancellationToken cancellationToken) => GetPreferencesAsync(cancellationToken);
    }
}
