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

/// <summary>
/// Deluge's Web UI password format (#1054), from <c>deluge/ui/web/auth.py</c>:
/// <c>pwd_sha1</c> is the hex SHA-1 of the stored <c>pwd_salt</c> text followed by the
/// password. The salt is derived from the (already random) generated password, so
/// the two placeholders in the seeded web.conf always agree.
/// </summary>
public static class DelugeWebPassword
{
    public static string Salt(string password)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("deluno-deluge-web:" + password)))[..32].ToLowerInvariant();

    public static string Sha1(string password)
        => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Salt(password) + password))).ToLowerInvariant();
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
            .Replace("{{qbittorrentPbkdf2}}", QBittorrentPassword.ToIniValue(credentials.Password), StringComparison.Ordinal)
            .Replace("{{delugeSalt}}", DelugeWebPassword.Salt(credentials.Password), StringComparison.Ordinal)
            .Replace("{{delugePwdSha1}}", DelugeWebPassword.Sha1(credentials.Password), StringComparison.Ordinal);
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

/// <summary>
/// The one elevated step for a per-machine .msi (#1052, #1061). It is run as an
/// inline PowerShell command (nothing on disk to swap). Any user can create folders
/// under <c>C:\ProgramData</c>, so the step never trusts what is already there: it
/// refuses a folder (or any parent up to <c>%ProgramData%</c>) that is a junction or
/// symbolic link, takes ownership of <c>%ProgramData%\Deluno\installer</c> and locks it
/// to SYSTEM and Administrators, then makes a brand-new folder with a random name,
/// locks that the same way before anything goes in, copies the downloaded .msi into
/// it, refuses to run it unless its SHA-256 is the one the tray verified, runs msiexec
/// from that folder, and deletes the folder afterwards. For a package that installs a
/// Windows service Deluno does not want (Transmission's MSI registers an auto-start
/// service, run as LocalService and bound to every address) it stops and disables
/// that service in the same prompt.
/// </summary>
public static class ElevatedInstallScript
{
    /// <summary>Exit code the script returns when the copy does not match the verified hash.</summary>
    public const int HashMismatchExitCode = 4001;

    /// <summary>Exit code when the install folder could not be locked down.</summary>
    public const int FolderExitCode = 4002;

    /// <summary>Exit code when the install folder, or a parent of it, is a junction or symbolic link.</summary>
    public const int ReparsePointExitCode = 4003;

    public static string Build(string installerPath, string sha256, string? silentArgs, string? serviceName)
    {
        var lines = new List<string>
        {
            "$ErrorActionPreference = 'Stop'",
            "$code = 0",
            "$fresh = $null",
            "function Test-Link($p) { (Test-Path -LiteralPath $p) -and (((Get-Item -LiteralPath $p -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) }",
            // Take the folder over, drop every entry it had, and leave only SYSTEM and Administrators (owner: Administrators).
            "function Lock-Folder($p) { & icacls.exe $p /setowner '*S-1-5-32-544' | Out-Null; if ($LASTEXITCODE -ne 0) { throw 'owner' }; & icacls.exe $p /reset | Out-Null; if ($LASTEXITCODE -ne 0) { throw 'reset' }; & icacls.exe $p /inheritance:r | Out-Null; if ($LASTEXITCODE -ne 0) { throw 'inherit' }; & icacls.exe $p /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null; if ($LASTEXITCODE -ne 0) { throw 'grant' } }",
            "try {",
            "$programData = $env:ProgramData",
            "$parent = Join-Path $programData 'Deluno'",
            "$root = Join-Path $parent 'installer'",
            $"foreach ($p in @($programData, $parent, $root)) {{ if (Test-Link $p) {{ $code = {ReparsePointExitCode}; throw 'link' }} }}",
            "if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent | Out-Null }",
            "if (-not (Test-Path -LiteralPath $root)) { New-Item -ItemType Directory -Path $root | Out-Null }",
            $"foreach ($p in @($parent, $root)) {{ if (Test-Link $p) {{ $code = {ReparsePointExitCode}; throw 'link' }} }}",
            "Lock-Folder $root",
            "$name = [guid]::NewGuid().ToString('N')",
            "$fresh = Join-Path $root $name",
            "New-Item -ItemType Directory -Path $fresh | Out-Null",
            $"if (Test-Link $fresh) {{ $code = {ReparsePointExitCode}; throw 'link' }}",
            "Lock-Folder $fresh",
            "$copy = Join-Path $fresh ($name + '.msi')",
            "$log = Join-Path $fresh ($name + '.log')",
            $"Copy-Item -LiteralPath {Quote(installerPath)} -Destination $copy",
            $"if ((Get-FileHash -Algorithm SHA256 -LiteralPath $copy).Hash -ne {Quote(sha256)}) {{ $code = {HashMismatchExitCode}; throw 'hash' }}",
            $"$arguments = '/i \"' + $copy + '\" ALLUSERS=1 ' + {Quote(silentArgs ?? string.Empty)} + ' /l*v \"' + $log + '\"'",
            "$process = Start-Process -FilePath 'msiexec.exe' -ArgumentList $arguments -Wait -PassThru",
            "$code = $process.ExitCode",
            "} catch { if ($code -eq 0) { $code = " + FolderExitCode + " } } finally {",
            "if ($fresh) { Remove-Item -LiteralPath $fresh -Recurse -Force -ErrorAction SilentlyContinue }",
            "}",
            "if ($code -ne 0) { exit $code }"
        };
        if (!string.IsNullOrWhiteSpace(serviceName))
        {
            lines.Add($"Stop-Service -Name {Quote(serviceName)} -ErrorAction SilentlyContinue");
            lines.Add($"Set-Service -Name {Quote(serviceName)} -StartupType Disabled -ErrorAction SilentlyContinue");
        }

        lines.Add("exit 0");
        return string.Join("\n", lines);
    }

    /// <summary>The arguments for <c>powershell.exe</c> that run <paramref name="script"/> with no file involved.</summary>
    public static string PowerShellArguments(string script)
        => "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
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
