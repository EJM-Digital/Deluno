using System.Net;
using System.Text.Json;
using Deluno.Api.Network;
using Deluno.Tests.Recorded;
using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>
/// #1257, "look next door": NZBGet's save sent back a whole settings list it had read, runtime-only values included. Every other
/// client's setup writes through its own settings call, and each must send only the keys it means to change, and keys that are
/// real in that app (checked against the app's recorded reply). Running the NZBGet setup twice must leave the second save equal
/// to the first and carry no runtime-only values.
/// </summary>
public sealed partial class ComponentSetupTests
{
    private static HashSet<string> Properties(JsonElement element) => element.EnumerateObject().Select(p => p.Name).ToHashSet();

    private static HashSet<string> PropertiesOf(System.Text.Json.Nodes.JsonNode? node) => node!.AsObject().Select(p => p.Key).ToHashSet();

    [Fact]
    public async Task Qbittorrent_setPreferences_carries_only_the_folder_and_automatic_management_keys()
    {
        var client = new ScriptedClient(seen => (HttpStatusCode.OK, seen.Path.EndsWith("/login", StringComparison.Ordinal) ? "Ok." : string.Empty));

        await ConfiguratorFor(client).ConfigureAsync(Load("qbittorrent"), TestLayout(), new BundledComponentCredentials("deluno", "pw"), CancellationToken.None);

        var form = System.Web.HttpUtility.ParseQueryString(client.Requests.Single(r => r.Path.EndsWith("/setPreferences", StringComparison.Ordinal)).Body);
        Assert.Equal("json", Assert.Single(form.AllKeys));
        using var sent = JsonDocument.Parse(form["json"]!);
        var keys = Properties(sent.RootElement);
        Assert.Equal(new HashSet<string> { "save_path", "temp_path_enabled", "temp_path", "auto_tmm_enabled", "torrent_changed_tmm_enabled", "category_changed_tmm_enabled", "save_path_changed_tmm_enabled" }, keys);
        Assert.Subset(PropertiesOf(RecordedReplies.Qbittorrent("settings").Find("app/preferences").Reply.Json()), keys);
    }

    [Fact]
    public async Task Transmission_session_set_carries_only_the_three_folder_keys()
    {
        var client = new ScriptedClient(seen => seen.Request.Headers.Contains("X-Transmission-Session-Id")
            ? (HttpStatusCode.OK, """{"result":"success"}""")
            : (HttpStatusCode.Conflict, string.Empty));

        await new DownloadClientConfigurator(() => new SessionIdHandler(client), TimeSpan.Zero).ConfigureAsync(Load("transmission"), TestLayout(), null, CancellationToken.None);

        using var sent = JsonDocument.Parse(client.Requests.Last().Body);
        Assert.Equal(new HashSet<string> { "method", "arguments" }, Properties(sent.RootElement));
        var keys = Properties(sent.RootElement.GetProperty("arguments"));
        Assert.Equal(new HashSet<string> { "download-dir", "incomplete-dir", "incomplete-dir-enabled" }, keys);
        Assert.Subset(PropertiesOf(RecordedReplies.Transmission("settings").Find("session-get").Reply.Json()["arguments"]), keys);
    }

    [Fact]
    public async Task Deluge_set_config_carries_only_the_three_folder_keys()
    {
        var client = new ScriptedClient(seen => DelugeAnswer(seen, labelPluginWorks: true));

        await ConfiguratorFor(client).ConfigureAsync(Load("deluge"), TestLayout(), null, CancellationToken.None);

        var call = client.Requests.Select(r => JsonDocument.Parse(r.Body).RootElement).Single(c => c.GetProperty("method").GetString() == "core.set_config");
        Assert.Equal(1, call.GetProperty("params").GetArrayLength());
        var keys = Properties(call.GetProperty("params")[0]);
        Assert.Equal(new HashSet<string> { "download_location", "move_completed", "move_completed_path" }, keys);
        // Deluge's recorded settings read asks for exactly these keys and gets them back, so they are real settings.
        Assert.Subset(PropertiesOf(RecordedReplies.Deluge("settings").Find("core.get_config_values").Reply.Json()["result"]), keys);
    }

    [Fact]
    public async Task Sabnzbd_set_config_carries_only_the_two_folder_settings_and_the_category_name_and_folder()
    {
        var client = new ScriptedClient(new FakeSabnzbd().Answer);

        await ConfiguratorFor(client).ConfigureAsync(Load("sabnzbd"), TestLayout(), new BundledComponentCredentials(string.Empty, "the-key"), CancellationToken.None);

        var writes = client.Requests.Select(r => System.Web.HttpUtility.ParseQueryString(r.Query)).Where(q => q["mode"] == "set_config").ToList();
        Assert.NotEmpty(writes);
        var miscKeys = writes.Where(q => q["section"] == "misc").Select(q => q["keyword"]!).ToHashSet();
        Assert.Equal(new HashSet<string> { "download_dir", "complete_dir" }, miscKeys);
        Assert.Subset(PropertiesOf(RecordedReplies.Sabnzbd("settings").Find("mode=get_config").Reply.Json()["config"]!["misc"]), miscKeys);
        foreach (var write in writes)
        {
            var allowed = write["section"] == "misc"
                ? new HashSet<string> { "mode", "section", "keyword", "value", "output", "apikey" }
                : new HashSet<string> { "mode", "section", "name", "dir", "output", "apikey" };
            Assert.Subset(allowed, write.AllKeys.Select(k => k!).ToHashSet());
        }
    }

    [Fact]
    public async Task Running_the_nzbget_setup_twice_saves_the_same_list_the_second_time_with_no_runtime_only_values()
    {
        var runtime = RecordedOptionList("settings", "\"config\"").Where(o => NzbgetRuntimeOnly.Contains(o.Key)).ToList();
        var layout = TestLayout();
        var file = RecordedOptionList("loadconfig-clean", "loadconfig");
        var saves = new List<List<KeyValuePair<string, string>>>();
        var trueReply = NzbgetRecorded("config-error-paused", "reload");
        var client = new ScriptedClient(seen =>
        {
            using var document = JsonDocument.Parse(seen.Body);
            var method = document.RootElement.GetProperty("method").GetString();
            switch (method)
            {
                case "saveconfig":
                    file = document.RootElement.GetProperty("params")[0].EnumerateArray()
                        .Select(o => new KeyValuePair<string, string>(o.GetProperty("Name").GetString()!, o.GetProperty("Value").GetString() ?? string.Empty)).ToList();
                    saves.Add(file);
                    return (HttpStatusCode.OK, trueReply);
                case "loadconfig":
                    return (HttpStatusCode.OK, OptionReply(file));
                case "config":
                    return (HttpStatusCode.OK, OptionReply(file.Concat(runtime.Where(r => file.All(f => f.Key != r.Key))).ToList()));
                case "version":
                    return (HttpStatusCode.OK, NzbgetRecorded("version", "version"));
                case "status":
                    return (HttpStatusCode.OK, NzbgetRecorded("config-error-cleared", "\"status\""));
                default:
                    return (HttpStatusCode.OK, trueReply);
            }
        });
        var configurator = ConfiguratorFor(client);

        Assert.Null(await configurator.ConfigureAsync(Load("nzbget"), layout, null, CancellationToken.None));
        Assert.Null(await configurator.ConfigureAsync(Load("nzbget"), layout, null, CancellationToken.None));

        Assert.Equal(2, saves.Count);
        Assert.All(saves, save => Assert.DoesNotContain(save, o => NzbgetRuntimeOnly.Contains(o.Key)));
        Assert.Equal(saves[0], saves[1]);
    }

    private static List<KeyValuePair<string, string>> RecordedOptionList(string state, string method)
    {
        using var document = JsonDocument.Parse(NzbgetRecorded(state, method));
        return document.RootElement.GetProperty("result").EnumerateArray()
            .Select(o => new KeyValuePair<string, string>(o.GetProperty("Name").GetString()!, o.GetProperty("Value").GetString() ?? string.Empty)).ToList();
    }

    private static string OptionReply(IEnumerable<KeyValuePair<string, string>> options)
        => JsonSerializer.Serialize(new { version = "1.1", result = options.Select(o => new { Name = o.Key, Value = o.Value }).ToArray() });
}
