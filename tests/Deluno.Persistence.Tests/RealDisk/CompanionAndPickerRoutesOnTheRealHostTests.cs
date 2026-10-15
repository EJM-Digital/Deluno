using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Companions;
using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// The Companion Tools page's install and update routes and the folder picker, over a real host and a real data folder.
/// Where this computer can install tools (Windows, started by a person) the routes act; anywhere else (Linux CI, a Windows
/// service) they say why in plain words and write nothing. Nothing here downloads, installs or opens a window.
/// </summary>
public sealed class CompanionAndPickerRoutesOnTheRealHostTests
{
    private static bool CanInstallHere => CompanionToolsService.Availability().Available;

    private static string PendingFile(ApplicationTestHost app) => PendingComponentInstalls.PathFor(app.DataRoot);

    [Fact]
    public async Task Asking_for_apps_only_the_tray_installs_writes_the_ask_for_the_tray_to_find_and_ignores_names_it_does_not_know()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        Assert.False(File.Exists(PendingFile(app)));

        var response = await app.Client.PostAsJsonAsync("/api/companion-tools/tray-install", new { toolIds = new[] { "deluge", "not-an-app", "nzbget" } });

        if (!CanInstallHere)
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.False(string.IsNullOrWhiteSpace((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString()));
            Assert.False(File.Exists(PendingFile(app)), "nothing is asked of the tray where nothing can be installed");
            return;
        }

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(File.Exists(PendingFile(app)), "the ask is a file in the data folder, because the tray is another process");
        var pending = PendingComponentInstalls.Read(app.DataRoot)!;
        Assert.Equal(["deluge", "nzbget"], pending.Kinds.OrderBy(kind => kind, StringComparer.Ordinal).ToArray());
        Assert.True(pending.AskedByPerson);
        Assert.Equal("You asked for it on the Companion Tools page.", pending.Reason);
    }

    [Fact]
    public async Task Asking_for_no_app_the_tray_installs_is_refused_in_plain_words_and_writes_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/companion-tools/tray-install", new { toolIds = new[] { "sabnzbd", "bogus" } });

        Assert.Equal(CanInstallHere ? HttpStatusCode.BadRequest : HttpStatusCode.Conflict, response.StatusCode);
        if (CanInstallHere)
        {
            Assert.Equal("Tick at least one app.", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").GetProperty("toolIds")[0].GetString());
        }

        Assert.False(File.Exists(PendingFile(app)));
    }

    [Fact]
    public async Task Installing_with_no_known_tool_ticked_is_refused_and_downloads_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/companion-tools/install", new { toolIds = new[] { "bogus" } });

        Assert.Equal(CanInstallHere ? HttpStatusCode.BadRequest : HttpStatusCode.Conflict, response.StatusCode);
        if (CanInstallHere)
        {
            Assert.Equal("Tick at least one tool.", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").GetProperty("toolIds")[0].GetString());
        }

        Assert.False(Directory.Exists(Path.Combine(app.DataRoot, "companions", "downloads")), "no installer was fetched");
    }

    [Fact]
    public async Task Both_install_routes_need_a_signed_in_person()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var anonymous = app.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/companion-tools/install", new { toolIds = new[] { "weir" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/companion-tools/tray-install", new { toolIds = new[] { "deluge" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/connections/weir/some-id/update", content: null)).StatusCode);
        Assert.False(File.Exists(PendingFile(app)));
    }

    [Fact]
    public async Task Updating_a_connection_with_no_newer_version_waiting_changes_nothing_and_says_so()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsync("/api/connections/weir/no-such-connection/update", content: null);

        if (!CanInstallHere)
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            return;
        }

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unchanged", body.GetProperty("outcome").GetString());
        Assert.Equal("Nothing changed: no newer version is waiting for this connection.", body.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("X-Forwarded-For")]
    [InlineData("Forwarded")]
    [InlineData("X-Real-IP")]
    public async Task The_server_folder_window_is_never_opened_for_a_request_that_came_through_a_proxy(string header)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/filesystem/native-folder-picker") { Content = JsonContent.Create(new { initialPath = app.DataRoot }) };
        request.Headers.TryAddWithoutValidation(header, "203.0.113.9");

        var response = await app.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("nativePickerUnavailable").GetBoolean());
        Assert.Contains("opens on the computer Deluno runs on", body.GetProperty("message").GetString(), StringComparison.Ordinal);
    }
}
