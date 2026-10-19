using System.Net;
using System.Text.Json;
using Deluno.Api.Network;
using Deluno.Persistence.Tests.Support;
using Deluno.Security;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// "Apps you installed" (#1061, coverage register, area api): the picker list and the Connect button, run through the real
/// installed-apps list on disk, the real credential store file and the real connection code, against what a real qBittorrent
/// 5.2.1 answered (tests/fixtures/recorded). Only the app's own address is a loopback server serving those recordings.
/// </summary>
public sealed class InstalledAppsAreConnectedOnTheRealHostTests
{
    private const string Password = "Installer-Made-Pass-7";

    private static string ConnectionsDatabase(ApplicationTestHost app) => Path.Combine(app.DataRoot, "platform.db");

    private static Task<ApplicationTestHost> StartWithRealStoreAsync()
        => ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.RemoveAll<IBundledComponentCredentialStore>();
            services.AddSingleton<IBundledComponentCredentialStore>(sp => new FileBundledComponentCredentialStore(
                FileBundledComponentCredentialStore.PathFor(sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["Storage:DataRoot"]!),
                sp.GetRequiredService<ISecretProtector>()));
        });

    private static long DownloadClientRows(ApplicationTestHost app)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={ConnectionsDatabase(app)};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM download_clients;";
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public async Task The_installed_apps_list_reads_the_list_the_installer_wrote_on_disk_and_says_who_is_connected()
    {
        await using var qbit = await RecordedAppServer.StartAsync("qbittorrent", new[] { "sign-in", "version", "settings" }.Select(RecordedReplies.Qbittorrent));
        await using var app = await StartWithRealStoreAsync();
        var record = app.Services.GetRequiredService<IInstalledComponentRecord>();

        var before = await app.Client.GetFromJsonElementAsync("/api/system/bundled-components");
        var qbitBefore = before.EnumerateArray().Single(item => item.GetProperty("kind").GetString() == "qbittorrent");
        Assert.False(qbitBefore.GetProperty("installed").GetBoolean());
        Assert.False(File.Exists(FileInstalledComponentRecord.PathFor(app.DataRoot)));

        record.MarkInstalled("qbittorrent", "localhost", qbit.Port);
        Assert.True(File.Exists(FileInstalledComponentRecord.PathFor(app.DataRoot)));

        var after = await app.Client.GetFromJsonElementAsync("/api/system/bundled-components");
        var qbitAfter = after.EnumerateArray().Single(item => item.GetProperty("kind").GetString() == "qbittorrent");
        Assert.True(qbitAfter.GetProperty("installed").GetBoolean());
        Assert.False(qbitAfter.GetProperty("connected").GetBoolean());
        var sabnzbd = after.EnumerateArray().Single(item => item.GetProperty("kind").GetString() == "sabnzbd");
        Assert.False(sabnzbd.GetProperty("installed").GetBoolean());
        Assert.DoesNotContain(Password, after.GetRawText());
        Assert.Equal(0, DownloadClientRows(app));
    }

    [Fact]
    public async Task Connecting_an_installed_qbittorrent_signs_in_with_the_installers_login_saves_one_connection_and_shows_it_connected()
    {
        await using var qbit = await RecordedAppServer.StartAsync("qbittorrent", new[] { "sign-in", "version", "settings", "category-unknown" }.Select(RecordedReplies.Qbittorrent));
        await using var app = await StartWithRealStoreAsync();
        app.Services.GetRequiredService<IInstalledComponentRecord>().MarkInstalled("qbittorrent", "localhost", qbit.Port);
        app.Services.GetRequiredService<IBundledComponentCredentialStore>().Save("qbittorrent", new BundledComponentCredentials("deluno", Password));
        Assert.True(File.Exists(FileBundledComponentCredentialStore.PathFor(app.DataRoot)));
        Assert.DoesNotContain(Password, await File.ReadAllTextAsync(FileBundledComponentCredentialStore.PathFor(app.DataRoot)));

        var response = await app.Client.PostAsync("/api/system/bundled-components/qbittorrent/connect", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Password, text);
        var body = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.True(body.GetProperty("healthy").GetBoolean(), text);
        Assert.Equal("qBittorrent is connected.", body.GetProperty("message").GetString());
        var login = qbit.Requests.First(request => request.PathAndQuery.EndsWith("auth/login", StringComparison.Ordinal));
        Assert.Contains("username=deluno", login.Body);
        Assert.Contains(Uri.EscapeDataString(Password), login.Body);
        Assert.Equal(1, DownloadClientRows(app));

        var list = await app.Client.GetFromJsonElementAsync("/api/system/bundled-components");
        var qbitRow = list.EnumerateArray().Single(item => item.GetProperty("kind").GetString() == "qbittorrent");
        Assert.True(qbitRow.GetProperty("connected").GetBoolean());
        var wired = (await File.ReadAllTextAsync(FileInstalledComponentRecord.PathFor(app.DataRoot)));
        Assert.Contains("wiredUtc", wired, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Connecting_an_installed_qbittorrent_that_refuses_the_login_saves_nothing_and_asks_for_the_persons_own_login()
    {
        await using var qbit = await RecordedAppServer.StartAsync("qbittorrent", new[] { "login-refused" }.Select(RecordedReplies.Qbittorrent));
        await using var app = await StartWithRealStoreAsync();
        app.Services.GetRequiredService<IInstalledComponentRecord>().MarkInstalled("qbittorrent", "localhost", qbit.Port);
        app.Services.GetRequiredService<IBundledComponentCredentialStore>().Save("qbittorrent", new BundledComponentCredentials("deluno", Password));

        var response = await app.Client.PostAsync("/api/system/bundled-components/qbittorrent/connect", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Password, text);
        var body = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.False(body.GetProperty("healthy").GetBoolean());
        Assert.True(body.GetProperty("needsSignIn").GetBoolean());
        Assert.Contains("Nothing was saved", body.GetProperty("message").GetString());
        Assert.Equal(0, DownloadClientRows(app));
    }
}

internal static class JsonClientExtensions
{
    public static async Task<JsonElement> GetFromJsonElementAsync(this HttpClient client, string route)
        => JsonSerializer.Deserialize<JsonElement>(await client.GetStringAsync(route));
}
