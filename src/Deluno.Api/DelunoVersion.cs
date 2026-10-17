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

    /// <summary>
    /// Whether <paramref name="candidate"/> is a later release than <paramref name="running"/> (#1349), by semantic version:
    /// the numbers first, then a prerelease before its release ("1.0.0-rc.30.33" is earlier than "1.0.0"). A version that
    /// cannot be read is never called newer.
    /// </summary>
    public static bool IsNewer(string? candidate, string? running)
    {
        if (!TryParse(candidate, out var a, out var aPre) || !TryParse(running, out var b, out var bPre))
        {
            return false;
        }

        for (var i = 0; i < 3; i++)
        {
            if (a[i] != b[i])
            {
                return a[i] > b[i];
            }
        }

        if (aPre.Length == 0 || bPre.Length == 0)
        {
            return aPre.Length == 0 && bPre.Length > 0;
        }

        for (var i = 0; i < Math.Min(aPre.Length, bPre.Length); i++)
        {
            var aNumber = long.TryParse(aPre[i], out var an);
            var bNumber = long.TryParse(bPre[i], out var bn);
            var order = aNumber && bNumber ? an.CompareTo(bn)
                : aNumber ? -1
                : bNumber ? 1
                : string.CompareOrdinal(aPre[i], bPre[i]);
            if (order != 0)
            {
                return order > 0;
            }
        }

        return aPre.Length > bPre.Length;
    }

    private static bool TryParse(string? text, out long[] numbers, out string[] prerelease)
    {
        numbers = new long[3];
        prerelease = [];
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim().TrimStart('v', 'V');
        var plus = value.IndexOf('+');
        if (plus >= 0)
        {
            value = value[..plus];
        }

        var dash = value.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = value[(dash + 1)..].Split('.', StringSplitOptions.RemoveEmptyEntries);
            value = value[..dash];
        }

        var parts = value.Split('.');
        if (parts.Length is < 1 or > 4)
        {
            return false;
        }

        for (var i = 0; i < Math.Min(parts.Length, 3); i++)
        {
            if (!long.TryParse(parts[i], out numbers[i]))
            {
                return false;
            }
        }

        return true;
    }

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
