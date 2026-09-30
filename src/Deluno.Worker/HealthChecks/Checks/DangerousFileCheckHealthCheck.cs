using Deluno.Contracts;
using Deluno.Platform.Data;

namespace Deluno.Worker.HealthChecks.Checks;

/// <summary>
/// #944: the content blocklist (#816) checks the files actually inside a
/// download - not only its release name - for a dangerous extension
/// (<c>.exe</c>, <c>.scr</c>, ...). It is off by default for a brand-new
/// install (#936) until the setup guide or Settings turns it on. This says
/// so once, in plain words, with a one-click Fix the System -> Health
/// screen can route straight to the blocklist's own settings, the same
/// shape <see cref="AllowedHostsHealthCheck"/> uses for Allowed Hosts.
/// </summary>
public sealed class DangerousFileCheckHealthCheck(IDownloadHealthRepository repository) : IHealthCheck
{
    public const string Id = "download-health:dangerous-file";
    private const string Name = "Dangerous file check";

    public string Category => HealthCheckCategories.System;

    public async Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken)
    {
        var settings = await repository.GetContentBlocklistSettingsAsync(cancellationToken);

        return settings.Enabled
            ? [HealthCheckOutcome.Ok(Id, Category, Name, "Deluno checks the files inside a download against the content blocklist.")]
            : [HealthCheckOutcome.Suggestion(
                Id, Category, Name,
                "Deluno does not check downloads for dangerous or junk files.",
                cause: "The content blocklist is off, so a download is never checked for a risky file - executable, script or shortcut - packaged inside it.",
                fix: "Open the content blocklist under Automation & Recovery and turn it on.")];
    }
}
