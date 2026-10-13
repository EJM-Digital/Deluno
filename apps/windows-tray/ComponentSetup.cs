using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deluno.Api.Network;
using Deluno.Contracts;

namespace Deluno.Tray;

/// <summary>
/// qBittorrent's own password format (#1050), read from
/// <c>src/base/utils/password.cpp</c> at release-5.2.1: PBKDF2-HMAC-SHA512,
/// 100000 iterations, a 16-byte salt and a 64-byte key, stored in the ini as
/// <c>"@ByteArray(&lt;base64 salt&gt;:&lt;base64 key&gt;)"</c> under
/// <c>WebUI\Password_PBKDF2</c> (QSettings writes a QByteArray with that
/// wrapper and quotes it).
/// </summary>
public static class QBittorrentPassword
{
    public const int Iterations = 100000;
    public const int SaltBytes = 16;
    public const int KeyBytes = 64;

    public static string ToIniValue(string password, byte[]? salt = null)
    {
        salt ??= RandomNumberGenerator.GetBytes(SaltBytes);
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA512, KeyBytes);
        return $"@ByteArray({Convert.ToBase64String(salt)}:{Convert.ToBase64String(key)})";
    }
}

/// <summary>The one value the tray needs out of SABnzbd's own settings file (#1054).</summary>
public static class SabnzbdIni
{
    /// <summary>The <c>api_key</c> under <c>[misc]</c>, or null when there is none yet or it is blank.</summary>
    public static string? ApiKey(string? ini)
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

            if (inMisc && line.StartsWith("api_key", StringComparison.OrdinalIgnoreCase) && line.IndexOf('=') is var eq and > 0)
            {
                var value = line[(eq + 1)..].Trim().Trim('"');
                return value.Length == 0 ? null : value;
            }
        }

        return null;
    }
}

/// <summary>A random password for a client Deluno signs in to for the person: letters and digits only, so no client's config format needs escaping.</summary>
public static class GeneratedPassword
{
    private const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    public static string Create(int length = 24)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(chars);
    }
}

/// <summary>Fills a component's seeded config: placeholders, and edits to the vendor's own template.</summary>
public static class ComponentConfig
{
    public static string RenderPlaceholders(string content, BundledComponentCredentials? credentials)
    {
        if (credentials is null)
        {
            return content;
        }

        return content
            .Replace("{{username}}", credentials.Username, StringComparison.Ordinal)
            .Replace("{{password}}", credentials.Password, StringComparison.Ordinal)
            .Replace("{{qbittorrentPbkdf2}}", QBittorrentPassword.ToIniValue(credentials.Password), StringComparison.Ordinal);
    }

    /// <summary>The template with every replacement applied, or null when the template does not contain one of them (the vendor changed the file, so the seed would be wrong).</summary>
    public static string? ApplyReplacements(string template, IReadOnlyDictionary<string, string> replacements, BundledComponentCredentials? credentials)
    {
        var result = template;
        foreach (var (from, to) in replacements)
        {
            if (!result.Contains(from, StringComparison.Ordinal))
            {
                return null;
            }

            result = result.Replace(from, RenderPlaceholders(Environment.ExpandEnvironmentVariables(to), credentials), StringComparison.Ordinal);
        }

        return result;
    }
}

/// <summary>Which clients Deluno started, so the next sign-in starts them again (see <see cref="ComponentAutoStarter"/>).</summary>
public interface IStartedComponentsRegistry
{
    IReadOnlyList<string> Read();
    void Add(string kind);
}

public sealed class FileStartedComponentsRegistry(string? path = null, IStateFileSystem? io = null) : IStartedComponentsRegistry
{
    // Under the data root, not the install root Velopack replaces on a reinstall (#1177).
    private readonly string _path = path ?? Path.Combine(ComponentPaths.DataRoot(), "config", "started-components.json");

    /// <summary>The clients Deluno started. A file that is busy or damaged reads as none, so nothing is started from a guess; a write never starts from this.</summary>
    public IReadOnlyList<string> Read() => StateFile.ReadLocked<List<string>>(_path, null, io).Value ?? [];

    /// <summary>
    /// Remembers that Deluno started this client. Throws <see cref="StateFileException"/> (in everyday words) when it could not
    /// be remembered, leaving the file as it was: the other clients are never dropped from a failed read (#1260).
    /// </summary>
    public void Add(string kind)
        => StateFile.UpdateOrThrow<List<string>>(
            _path,
            current =>
            {
                var kinds = current ?? [];
                return kinds.Contains(kind, StringComparer.OrdinalIgnoreCase) ? current : [.. kinds, kind];
            },
            "The list of download clients Deluno starts at sign-in",
            io: io);
}

/// <summary>
/// After a restart, starts the download clients Deluno itself started at install
/// (#1051, #1052) -- the same command, the same windowless start -- unless the
/// client is already answering on its port (its own vendor start-up, or the
/// person). Runs from the tray's own start, which is the one thing that already
/// runs at sign-in, so a client survives a reboot without a service or an
/// elevated prompt.
/// </summary>
public sealed class ComponentAutoStarter(
    IStartedComponentsRegistry registry,
    IPortProbe portProbe,
    IProcessStarter starter,
    Action<string>? log = null)
{
    public async Task<IReadOnlyList<string>> RunAsync(IEnumerable<BundledComponentSpec> specs, CancellationToken cancellationToken)
    {
        var started = new List<string>();
        var remembered = registry.Read();
        foreach (var spec in specs)
        {
            if (!spec.RestartsAtSignIn || !remembered.Contains(spec.Kind, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (await portProbe.IsOpenAsync("127.0.0.1", spec.DefaultPort, cancellationToken))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(spec.PreStartCommand))
            {
                starter.Start(ComponentPaths.Expand(spec.PreStartCommand), ExpandArgs(spec.PreStartArgs), spec.StartHidden);
            }

            var process = starter.Start(ComponentPaths.Expand(spec.PostInstallCommand!), ExpandArgs(spec.PostInstallArgs), spec.StartHidden);
            log?.Invoke(process is null
                ? $"{spec.DisplayName} was not running after sign-in and Deluno could not start it."
                : $"{spec.DisplayName} was not running after sign-in, so Deluno started it.");
            if (process is not null)
            {
                started.Add(spec.Kind);
            }
        }

        return started;
    }

    public static string? ExpandArgs(string? arguments)
        => string.IsNullOrWhiteSpace(arguments) ? arguments : ComponentPaths.Expand(arguments);
}

/// <summary>
/// The login the person picks for SABnzbd and NZBGet in the installer's picker (#1142), checked before anything is
/// installed and written into each client's own settings. Never logged: a failed check names the rule, not the value.
/// </summary>
public static class UsenetLogins
{
    public const int MinimumUsernameLength = 3;
    public const int MinimumPasswordLength = 8;

    public static bool IsUsenetKind(string kind)
        => string.Equals(kind, "sabnzbd", StringComparison.OrdinalIgnoreCase) || string.Equals(kind, "nzbget", StringComparison.OrdinalIgnoreCase);

    /// <summary>The first thing wrong with a login, in plain words, or null when it can be used.</summary>
    public static string? Validate(string displayName, string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return $"Choose a username and a password for {displayName}.";
        }

        if (username.Length < MinimumUsernameLength)
        {
            return $"The {displayName} username needs at least {MinimumUsernameLength} characters.";
        }

        if (password.Length < MinimumPasswordLength)
        {
            return $"The {displayName} password needs at least {MinimumPasswordLength} characters.";
        }

        if (!IsSafe(username) || !IsSafe(password))
        {
            return $"The {displayName} username and password cannot start or end with a space, or contain quote marks, backslashes or $.";
        }

        return null;
    }

    private static bool IsSafe(string value)
        => value == value.Trim() && !value.Any(c => char.IsControl(c) || c is '"' or '\'' or '\\' or '$');

    /// <summary>SABnzbd's <c>[misc]</c> section gets the login; NZBGet's <c>ControlUsername</c> and <c>ControlPassword</c> lines are replaced. Null when the file has no place for it.</summary>
    public static string? WriteInto(string kind, string content, BundledComponentCredentials login)
    {
        if (string.Equals(kind, "sabnzbd", StringComparison.OrdinalIgnoreCase))
        {
            var at = content.IndexOf("[misc]", StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return null;
            }

            var lineEnd = content.IndexOf('\n', at);
            var lead = lineEnd < 0 ? "\n" : string.Empty;
            lineEnd = lineEnd < 0 ? content.Length : lineEnd + 1;
            return content[..lineEnd] + lead + $"username = \"{login.Username}\"\npassword = \"{login.Password}\"\n" + content[lineEnd..];
        }

        const System.Text.RegularExpressions.RegexOptions Lines = System.Text.RegularExpressions.RegexOptions.Multiline;
        if (System.Text.RegularExpressions.Regex.Matches(content, @"^ControlUsername=[^\r\n]*", Lines).Count != 1
            || System.Text.RegularExpressions.Regex.Matches(content, @"^ControlPassword=[^\r\n]*", Lines).Count != 1)
        {
            return null;
        }

        var replaced = System.Text.RegularExpressions.Regex.Replace(content, @"^ControlUsername=[^\r\n]*", _ => "ControlUsername=" + login.Username, Lines);
        return System.Text.RegularExpressions.Regex.Replace(replaced, @"^ControlPassword=[^\r\n]*", _ => "ControlPassword=" + login.Password, Lines);
    }

    /// <summary>
    /// What the picker hands the installer: one login per ticked Usenet client, the same one for both when asked.
    /// Returns the first problem when a ticked client's login is not usable.
    /// </summary>
    public static (IReadOnlyDictionary<string, BundledComponentCredentials> Logins, string? Problem) Resolve(
        bool sabnzbdTicked, bool nzbgetTicked, bool sameForBoth,
        string? sabnzbdUser, string? sabnzbdPassword, string? nzbgetUser, string? nzbgetPassword)
    {
        var logins = new Dictionary<string, BundledComponentCredentials>(StringComparer.OrdinalIgnoreCase);
        if (sameForBoth && sabnzbdTicked && nzbgetTicked)
        {
            nzbgetUser = sabnzbdUser;
            nzbgetPassword = sabnzbdPassword;
        }

        if (sabnzbdTicked)
        {
            if (Validate("SABnzbd", sabnzbdUser, sabnzbdPassword) is { } problem)
            {
                return (logins, problem);
            }

            logins["sabnzbd"] = new BundledComponentCredentials(sabnzbdUser!, sabnzbdPassword!);
        }

        if (nzbgetTicked)
        {
            if (Validate("NZBGet", nzbgetUser, nzbgetPassword) is { } problem)
            {
                return (logins, problem);
            }

            logins["nzbget"] = new BundledComponentCredentials(nzbgetUser!, nzbgetPassword!);
        }

        return (logins, null);
    }
}
