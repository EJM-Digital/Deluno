using Deluno.Security.Hardening.Backends;

namespace Deluno.Security.Hardening;

/// <summary>
/// What the secret-backend choice asks of the system it runs on, handed in so every system's choice can be tested on
/// any system.
/// </summary>
public sealed record SecretHostFacts(
    bool IsWindows,
    string OsTag,
    Func<string?> KeyFromEnvironment,
    Func<string, bool> FileExists,
    Func<ISecretProtector> CreateWindowsProtector)
{
    public static SecretHostFacts ThisSystem { get; } = new(
        OperatingSystem.IsWindows(),
        TagFor(OperatingSystem.IsLinux(), OperatingSystem.IsMacOS(), OperatingSystem.IsWindows()),
        () => Environment.GetEnvironmentVariable(FileBackedSecretProtector.EnvVarName),
        File.Exists,
        WindowsSecretProtectors.Create);

    public static string TagFor(bool linux, bool macOs, bool windows)
        => linux ? "Linux" : macOs ? "macOS" : windows ? "Windows" : "Unknown";
}
