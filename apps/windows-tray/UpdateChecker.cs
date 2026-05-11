using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Deluno.Tray;

public sealed class UpdateInfo
{
    public string Version { get; init; } = "";
    public string InstallerUrl { get; init; } = "";
    public string Sha256 { get; init; } = "";
}

public sealed class UpdateChecker
{
    private const string ReleasesApiUrl =
        "https://api.github.com/repos/jampat000/Deluno/releases/latest";

    private readonly HttpClient _http = new()
    {
        DefaultRequestHeaders = { { "User-Agent", "Deluno-Tray-Updater" } }
    };

    public async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            var json = await _http.GetStringAsync(ReleasesApiUrl);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.GetProperty("tag_name").GetString() ?? "";
            var releaseVersion = tagName.TrimStart('v');
            var currentVersion = Assembly.GetEntryAssembly()!
                .GetName().Version?.ToString(3) ?? "0.0.0";

            if (!IsNewer(releaseVersion, currentVersion))
                return null;

            // Find the Windows installer asset
            var assets = root.GetProperty("assets");
            string? installerUrl = null;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                    name.Contains("Setup", StringComparison.OrdinalIgnoreCase))
                {
                    installerUrl = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }

            if (installerUrl is null) return null;

            // Extract SHA256 from release body ("abc123  Deluno-Setup-x.y.z.exe")
            var body = root.GetProperty("body").GetString() ?? "";
            var sha256 = ParseSha256FromReleaseBody(body, releaseVersion);

            return new UpdateInfo
            {
                Version = releaseVersion,
                InstallerUrl = installerUrl,
                Sha256 = sha256
            };
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> DownloadAndInstallAsync(UpdateInfo update, IProgress<int>? progress = null)
    {
        var tempDir  = Path.Combine(Path.GetTempPath(), "DelunoUpdate");
        var destPath = Path.Combine(tempDir, $"Deluno-Setup-{update.Version}.exe");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Download
            using var response = await _http.GetAsync(
                update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? -1L;
            await using var src  = await response.Content.ReadAsStreamAsync();
            await using var dest = File.Create(destPath);

            var buffer = new byte[81920];
            long downloaded = 0;
            int read;
            while ((read = await src.ReadAsync(buffer)) > 0)
            {
                await dest.WriteAsync(buffer.AsMemory(0, read));
                downloaded += read;
                if (total > 0)
                    progress?.Report((int)(downloaded * 100 / total));
            }

            // Verify SHA256
            if (!string.IsNullOrEmpty(update.Sha256))
            {
                dest.Position = 0;
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(dest)).ToLowerInvariant();
                if (!string.Equals(actual, update.Sha256.ToLowerInvariant()))
                    throw new InvalidOperationException(
                        "SHA256 checksum mismatch — installer may be corrupted or tampered with.");
            }

            // Run installer silently; it will close this process automatically
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName  = destPath,
                Arguments = "/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
                UseShellExecute = true
            });

            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Update failed: {ex.Message}\n\nPlease download manually from GitHub.",
                "Deluno Update", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private static bool IsNewer(string candidate, string current)
    {
        return Version.TryParse(candidate, out var c) &&
               Version.TryParse(current,   out var cur) &&
               c > cur;
    }

    private static string ParseSha256FromReleaseBody(string body, string version)
    {
        // Expected line format in release notes:
        // "abc123def...  Deluno-Setup-1.0.0.exe"
        foreach (var line in body.Split('\n'))
        {
            if (line.Contains($"Deluno-Setup-{version}.exe", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Trim().Split("  ", 2);
                if (parts.Length == 2 && parts[0].Length == 64)
                    return parts[0].Trim();
            }
        }
        return "";
    }
}
