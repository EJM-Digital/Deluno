namespace Deluno.Downloader.DependencyInjection;

/// <summary>
/// Resolves the path to a bundled native binary (par2, unrar) at
/// startup. Lookup order:
/// <list type="number">
///   <item><description>Env-var override <c>DELUNO_&lt;NAME&gt;_PATH</c> if set + the file exists.</description></item>
///   <item><description>App-local <c>&lt;AppContext.BaseDirectory&gt;/tools/&lt;name&gt;/&lt;binary&gt;</c> if it exists
///     (this is where the Velopack release pipeline drops bundled binaries on Windows).</description></item>
///   <item><description>Fallback to the bare binary name (PATH lookup). On the Docker image, apt installs
///     <c>par2</c> + <c>unrar</c> to <c>/usr/bin</c> which is on PATH so this works.</description></item>
/// </list>
///
/// Returning a string (not a fully-resolved absolute path) when we fall
/// through to PATH is intentional: <c>Process.Start</c> handles the
/// lookup natively.
/// </summary>
internal static class BundledBinaryResolver
{
    public static string Resolve(string toolName, string binaryFileName)
    {
        var envOverride = Environment.GetEnvironmentVariable($"DELUNO_{toolName.ToUpperInvariant()}_PATH");
        if (!string.IsNullOrWhiteSpace(envOverride) && File.Exists(envOverride))
            return envOverride;

        var appLocal = Path.Combine(AppContext.BaseDirectory, "tools", toolName, binaryFileName);
        if (File.Exists(appLocal))
            return appLocal;

        // Fall back to PATH lookup via the bare filename. On *nix the
        // bundled .exe variant won't exist; strip it.
        if (!OperatingSystem.IsWindows() && binaryFileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return binaryFileName[..^4];
        return binaryFileName;
    }
}
