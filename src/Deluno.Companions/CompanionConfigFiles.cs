using System.Security.Cryptography;
using System.Text;

namespace Deluno.Companions;

/// <summary>
/// The settings files a fresh SABnzbd and qBittorrent read on first start,
/// written before they start so neither ever shows a setup screen nobody asked
/// for, and both answer Deluno on the loopback address only.
/// </summary>
public static class CompanionConfigFiles
{
    /// <summary>
    /// qBittorrent stores a Web UI password as PBKDF2-HMAC-SHA512, 100,000
    /// iterations, a 16-byte salt and a 64-byte key: <c>@ByteArray(salt:key)</c>.
    /// </summary>
    public static string QbittorrentPasswordHash(string password, byte[]? salt = null)
    {
        salt ??= RandomNumberGenerator.GetBytes(16);
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 100_000, HashAlgorithmName.SHA512, 64);
        return $"@ByteArray({Convert.ToBase64String(salt)}:{Convert.ToBase64String(key)})";
    }

    public static string QbittorrentIni(int port, string username, string passwordHash)
        => $"""
            [LegalNotice]
            Accepted=true

            [Preferences]
            WebUI\Enabled=true
            WebUI\Address=127.0.0.1
            WebUI\Port={port}
            WebUI\Username={username}
            WebUI\Password_PBKDF2="{passwordHash}"
            WebUI\LocalHostAuth=true
            WebUI\CSRFProtection=true

            """;

    /// <summary>Whether an existing qBittorrent settings file already has a Web UI login somebody chose.</summary>
    public static bool QbittorrentHasWebUiLogin(string? ini)
        => ini is not null && ini.Contains("WebUI\\Username=", StringComparison.OrdinalIgnoreCase);

    public static string SabnzbdIni(int port, string apiKey, string nzbKey)
        => $"""
            [misc]
            host = 127.0.0.1
            port = {port}
            api_key = {apiKey}
            nzb_key = {nzbKey}
            auto_browser = 0

            """;

    /// <summary>Reads <c>key = value</c> from SABnzbd's <c>[misc]</c> section.</summary>
    public static string? SabnzbdValue(string? ini, string key)
    {
        if (ini is null)
        {
            return null;
        }

        var inMisc = false;
        foreach (var raw in ini.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inMisc = string.Equals(line, "[misc]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inMisc)
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator > 0 && string.Equals(line[..separator].Trim(), key, StringComparison.OrdinalIgnoreCase))
            {
                var value = line[(separator + 1)..].Trim();
                return value.Length == 0 ? null : value;
            }
        }

        return null;
    }

    public static string NewHexKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static string NewPassword() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).TrimEnd('=').Replace('+', 'x').Replace('/', 'y');

    /// <summary>The first free port from <paramref name="preferred"/>, so a tool never lands on one already taken.</summary>
    public static int FreePort(ICompanionSystem system, int preferred, int span = 20)
    {
        for (var port = preferred; port < preferred + span; port++)
        {
            if (!system.IsPortInUse(port))
            {
                return port;
            }
        }

        return preferred;
    }
}
