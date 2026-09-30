using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deluno.Api.Network;

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

public sealed class FileStartedComponentsRegistry(string? path = null) : IStartedComponentsRegistry
{
    private readonly string _path = path ?? Path.Combine(
        Environment.GetEnvironmentVariable("DELUNO_TEST_LOCALAPPDATA") is { Length: > 0 } testRoot
            ? testRoot
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Deluno", "config", "started-components.json");

    public IReadOnlyList<string> Read()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_path)) ?? []
                : [];
        }
        catch
        {
            return [];
        }
    }

    public void Add(string kind)
    {
        try
        {
            var kinds = Read().ToList();
            if (kinds.Contains(kind, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            kinds.Add(kind);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(kinds));
        }
        catch
        {
            // Not being able to remember it only means no restart at sign-in.
        }
    }
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
                starter.Start(Environment.ExpandEnvironmentVariables(spec.PreStartCommand), ExpandArgs(spec.PreStartArgs), spec.StartHidden);
            }

            var process = starter.Start(Environment.ExpandEnvironmentVariables(spec.PostInstallCommand!), ExpandArgs(spec.PostInstallArgs), spec.StartHidden);
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
        => string.IsNullOrWhiteSpace(arguments) ? arguments : Environment.ExpandEnvironmentVariables(arguments);
}
