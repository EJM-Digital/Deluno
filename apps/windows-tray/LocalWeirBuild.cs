using System.Diagnostics;
using System.Text.Json;

namespace Deluno.Tray;

/// <summary>
/// A test-only way to make the component picker install a Weir build from this machine instead of Weir's newest
/// release (#1158). The golden path installs Deluno and Weir from the same candidate commit before either is tagged,
/// and a Weir build that is not released yet cannot be found on GitHub. It is never offered on a screen: it exists
/// only when the person driving the test sets it up by hand.
///
/// <para>Two ways to give it, the first one found wins. Environment variables at install time:
/// <c>DELUNO_LOCAL_WEIR_SETUP</c> (the path of a local <c>Weir-win-Setup.exe</c>) and
/// <c>DELUNO_LOCAL_WEIR_SHA256</c> (its SHA-256). Or a file named <c>local-weir-build.json</c> beside the tray
/// program, <c>{ "path": "...", "sha256": "..." }</c>. The file must match the SHA-256 given or it is refused,
/// exactly as a downloaded installer is.</para>
/// </summary>
public sealed record LocalWeirBuild(string Path, string Sha256)
{
    public const string PathVariable = "DELUNO_LOCAL_WEIR_SETUP";
    public const string Sha256Variable = "DELUNO_LOCAL_WEIR_SHA256";
    public const string FileName = "local-weir-build.json";

    /// <summary>The override for <paramref name="componentKind"/>, or null when it is not Weir or none is set.</summary>
    public static LocalWeirBuild? Find(string componentKind, Func<string, string?> environment, string? directoryBesideTray)
    {
        if (!string.Equals(componentKind, "weir", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var path = environment(PathVariable);
        var sha256 = environment(Sha256Variable);
        if (!string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(sha256))
        {
            return new LocalWeirBuild(path.Trim(), sha256.Trim());
        }

        if (string.IsNullOrWhiteSpace(directoryBesideTray))
        {
            return null;
        }

        var file = System.IO.Path.Combine(directoryBesideTray, FileName);
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(file));
            var filePath = json.RootElement.TryGetProperty("path", out var p) ? p.GetString() : null;
            var fileSha = json.RootElement.TryGetProperty("sha256", out var s) ? s.GetString() : null;
            return string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(fileSha)
                ? null
                : new LocalWeirBuild(filePath.Trim(), fileSha.Trim());
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>The version the build reports, for the install log; "unknown" when the file carries none.</summary>
    public string Version()
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(Path);
            return info.ProductVersion ?? info.FileVersion ?? "unknown";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "unknown";
        }
    }
}
