using System.Reflection;

namespace Deluno.Api;

/// <summary>
/// The version Deluno is running, as released, prerelease label included
/// ("1.0.0-rc.23"), read from the informational version with any "+commit"
/// suffix dropped. The numeric assembly version alone ("1.0.0") made every
/// release candidate look the same: an rc.22 to rc.23 update was never
/// recorded and release notes were looked up under a version no section is
/// named after.
/// </summary>
public static class DelunoVersion
{
    public static string Running { get; } = Of(Assembly.GetEntryAssembly());

    public static string Of(Assembly? assembly)
        => Normalize(
            assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            assembly?.GetName().Version);

    public static string Normalize(string? informationalVersion, Version? assemblyVersion)
    {
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var plus = informationalVersion.IndexOf('+');
            var version = (plus >= 0 ? informationalVersion[..plus] : informationalVersion).Trim();
            if (version.Length > 0)
            {
                return version;
            }
        }

        return assemblyVersion?.ToString(3) ?? "0.0.0";
    }
}
