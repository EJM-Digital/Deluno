using System.Reflection;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace Deluno.Api.Updates;

public sealed class DefaultUpdateOrchestrator : IUpdateOrchestrator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _installKind;
    private readonly string _currentVersion;
    private readonly string _preferencesPath;
    private readonly string? _currentImageRef;
    private readonly string? _currentImageDigest;
    private readonly SemaphoreSlim _preferencesGate = new(1, 1);

    public DefaultUpdateOrchestrator(IOptions<StoragePathOptions> storageOptions)
    {
        _currentVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";
        _installKind = IsRunningInDocker() ? UpdateInstallKinds.Docker : UpdateInstallKinds.Manual;
        _preferencesPath = Path.Combine(Path.GetFullPath(storageOptions.Value.DataRoot), "update-preferences.json");
        _currentImageRef = Environment.GetEnvironmentVariable("DELUNO_IMAGE_REF");
        _currentImageDigest = Environment.GetEnvironmentVariable("DELUNO_IMAGE_DIGEST")
            ?? ExtractImageDigest(_currentImageRef);
    }

    public Task<UpdateStatusResponse> GetStatusAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(BuildStatus());
    }

    public Task<UpdateStatusResponse> CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(BuildStatus());
    }

    public Task<UpdateStatusResponse> DownloadUpdatesAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(BuildStatus());
    }

    public Task<UpdateStatusResponse> PrepareApplyOnNextRestartAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(BuildStatus());
    }

    public Task<UpdateStatusResponse> ApplyAndRestartNowAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(BuildStatus());
    }

    public async Task<UpdatePreferencesResponse> GetPreferencesAsync(CancellationToken cancellationToken)
    {
        await _preferencesGate.WaitAsync(cancellationToken);
        try
        {
            return ToResponse(await ReadPreferencesAsync(cancellationToken));
        }
        finally
        {
            _preferencesGate.Release();
        }
    }

    public async Task<UpdatePreferencesResponse> SavePreferencesAsync(UpdatePreferencesRequest request, CancellationToken cancellationToken)
    {
        await _preferencesGate.WaitAsync(cancellationToken);
        try
        {
            var current = await ReadPreferencesAsync(cancellationToken);
            var next = new UpdatePreferencesState
            {
                Mode = NormalizeMode(request.Mode ?? current.Mode),
                Channel = NormalizeChannel(request.Channel ?? current.Channel),
                AutoCheck = request.AutoCheck ?? current.AutoCheck
            };
            await WritePreferencesAsync(next, cancellationToken);
            return ToResponse(next);
        }
        finally
        {
            _preferencesGate.Release();
        }
    }

    private async Task<UpdatePreferencesState> ReadPreferencesAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_preferencesPath))
        {
            return UpdatePreferencesState.Default();
        }

        try
        {
            await using var stream = File.OpenRead(_preferencesPath);
            var state = await JsonSerializer.DeserializeAsync<UpdatePreferencesState>(stream, JsonOptions, cancellationToken);
            return state is null
                ? UpdatePreferencesState.Default()
                : new UpdatePreferencesState
                {
                    Mode = NormalizeMode(state.Mode),
                    Channel = NormalizeChannel(state.Channel),
                    AutoCheck = state.AutoCheck
                };
        }
        catch (JsonException)
        {
            return UpdatePreferencesState.Default();
        }
    }

    private async Task WritePreferencesAsync(UpdatePreferencesState state, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_preferencesPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = _preferencesPath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporaryPath, _preferencesPath, overwrite: true);
    }

    private static UpdatePreferencesResponse ToResponse(UpdatePreferencesState state)
        => new(state.Mode, state.Channel, state.AutoCheck);

    private static string NormalizeMode(string? value)
        => UpdateModes.IsValid(value) ? value! : UpdateModes.NotifyOnly;

    private static string NormalizeChannel(string? value)
        => string.IsNullOrWhiteSpace(value) ? "stable" : value.Trim().ToLowerInvariant();

    private UpdateStatusResponse BuildStatus()
    {
        var message = _installKind == UpdateInstallKinds.Docker
            ? "Docker installs do not support in-place binary updates. Pull a newer image tag and recreate the container."
            : "This runtime is not a Velopack-managed Windows install. Update by installing a newer build package.";

        var notes = _installKind == UpdateInstallKinds.Docker
            ? new[]
            {
                "Update: set DELUNO_IMAGE to the chosen version or digest, then run `docker compose pull deluno`.",
                "Recreate: run `docker compose up -d --no-build` and wait for `/api/health/ready` to return HTTP 200 while migrations finish.",
                "Verify: use `docker image inspect` to record the resolved image digest before declaring the rollout complete.",
                "Rollback: pin DELUNO_IMAGE to the previous digest and recreate with the same `/data` volume; restore a pre-upgrade backup if the schema is not backward-compatible."
            }
            : new[]
            {
                "In-app apply controls are only enabled for Velopack-managed Windows installs.",
                "Keep Storage__DataRoot outside the app folder."
            };

        return new UpdateStatusResponse(
            CurrentVersion: _currentVersion,
            Channel: "stable",
            InstallKind: _installKind,
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
            Notes: notes,
            CurrentImageRef: _currentImageRef,
            CurrentImageDigest: _currentImageDigest);
    }

    private static bool IsRunningInDocker()
    {
        var envFlag = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER");
        if (bool.TryParse(envFlag, out var runningInContainer) && runningInContainer)
        {
            return true;
        }

        var aspNetEnvFlag = Environment.GetEnvironmentVariable("ASPNETCORE_RUNNING_IN_CONTAINER");
        if (bool.TryParse(aspNetEnvFlag, out runningInContainer) && runningInContainer)
        {
            return true;
        }

        return false;
    }

    private static string? ExtractImageDigest(string? imageRef)
    {
        if (string.IsNullOrWhiteSpace(imageRef))
        {
            return null;
        }

        var separator = imageRef.LastIndexOf("@sha256:", StringComparison.OrdinalIgnoreCase);
        return separator >= 0
            ? imageRef[(separator + 1)..]
            : null;
    }

    private sealed class UpdatePreferencesState
    {
        public string Mode { get; set; } = UpdateModes.NotifyOnly;

        public string Channel { get; set; } = "stable";

        public bool AutoCheck { get; set; }

        public static UpdatePreferencesState Default() => new();
    }
}
