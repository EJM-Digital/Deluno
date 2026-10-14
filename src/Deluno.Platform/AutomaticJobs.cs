using Deluno.Platform.Data;

namespace Deluno.Platform;

/// <summary>
/// The one place the "automatic jobs" switch is read by a background service (#1321). Since 27 Sep nothing automatic may
/// change a person's files, downloads or library records unless they switched automatic jobs on, so every hosted or
/// background service that does so asks <see cref="IsOnAsync"/> before each pass and carries
/// <see cref="ChangesMediaOnlyWhenAutomaticJobsAreOnAttribute"/>. A test lists the hosted services from the
/// registration and fails on one that changes media and neither asks nor carries the mark.
/// </summary>
public static class AutomaticJobs
{
    /// <summary>The Activity category of the one line a paused background service writes.</summary>
    public const string PausedActivityCategory = "automation.paused";

    /// <summary>What Activity says, once, when a background service skips its pass because automatic jobs are off.</summary>
    public static string PausedMessage(string what)
        => $"Automatic jobs are off, so Deluno is not {what}. Switch them on in Settings to let it.";

    public static async Task<bool> IsOnAsync(IPlatformSettingsRepository settings, CancellationToken cancellationToken)
        => (await settings.GetAsync(cancellationToken)).AutoStartJobs;
}

/// <summary>
/// Marks a hosted or background service that changes files, downloads or library records on its own. It must call
/// <see cref="AutomaticJobs.IsOnAsync"/> before each pass; the registration test checks both.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ChangesMediaOnlyWhenAutomaticJobsAreOnAttribute : Attribute;
