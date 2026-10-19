using System.Text.Json;
using Deluno.Api.Network;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The installer's component picker (installer/bundled-components.json, the file that ships) and the web app's list of installed apps
/// (<see cref="BundledComponentCatalog"/>) must agree on which apps there are and where each one answers by default, or "Connect the apps
/// you installed" would look for an app on the wrong port.
/// </summary>
public sealed class BundledComponentPortsMatchTheInstallerTests
{
    private static JsonElement ReadManifest()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            var candidate = Path.Combine(folder.FullName, "installer", "bundled-components.json");
            if (File.Exists(candidate))
            {
                return JsonDocument.Parse(File.ReadAllText(candidate), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }).RootElement.Clone();
            }
        }

        throw new FileNotFoundException("installer/bundled-components.json was not found above the test folder.");
    }

    [Fact]
    public void The_web_apps_six_apps_are_the_six_the_installer_offers()
    {
        var manifest = ReadManifest();

        var offered = manifest.EnumerateObject().Where(entry => entry.Value.ValueKind == JsonValueKind.Object).Select(entry => entry.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();

        Assert.Equal(["deluge", "nzbget", "qbittorrent", "sabnzbd", "transmission", "weir"], offered);
        Assert.Equal(offered, BundledComponentCatalog.All.Select(spec => spec.Kind).OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Each_apps_default_port_category_and_name_in_the_web_app_equal_the_installers()
    {
        var manifest = ReadManifest();

        foreach (var spec in BundledComponentCatalog.All)
        {
            var entry = manifest.GetProperty(spec.Kind);
            Assert.Equal(entry.GetProperty("defaultPort").GetInt32(), spec.DefaultPort);
            Assert.Equal(entry.GetProperty("category").GetString(), spec.Category);
            Assert.Equal(entry.GetProperty("displayName").GetString(), spec.DisplayName);
            Assert.Contains(entry.GetProperty("uninstallDisplayNameHint").GetString(), spec.UninstallDisplayNameHints);
        }
    }

    [Fact]
    public void No_two_download_clients_share_a_default_port()
    {
        var ports = BundledComponentCatalog.All.Select(spec => spec.DefaultPort).ToArray();

        Assert.Equal(ports.Length, ports.Distinct().Count());
    }
}
