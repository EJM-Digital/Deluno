using System.Reflection;

namespace Deluno.Api.Updates;

public sealed class DefaultUpdateOrchestrator : IUpdateOrchestrator
{
    private readonly string _installKind;
    private readonly string _currentVersion;

    public DefaultUpdateOrchestrator()
    {
        _currentVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";
        _installKind = IsRunningInDocker() ? UpdateInstallKinds.Docker : UpdateInstallKinds.Manual;
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

    public Task<UpdatePreferencesResponse> GetPreferencesAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(new UpdatePreferencesResponse(
            Mode: UpdateModes.NotifyOnly,
            Channel: "stable",
            AutoCheck: false));
    }

    public Task<UpdatePreferencesResponse> SavePreferencesAsync(UpdatePreferencesRequest request, CancellationToken cancellationToken)
    {
        return GetPreferencesAsync(cancellationToken);
    }

    private UpdateStatusResponse BuildStatus()
    {
        var message = _installKind == UpdateInstallKinds.Docker
            ? "Docker installs do not support in-place binary updates. Pull a newer image tag and recreate the container."
            : "This runtime is not a Velopack-managed Windows install. Update by installing a newer build package.";

        var notes = _installKind == UpdateInstallKinds.Docker
            ? new[]
            {
                "Use docker pull with the desired tag.",
                "Recreate the container after the pull.",
                "Keep your persistent data volume mounted so upgrades are seamless."
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
            Notes: notes);
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
}
