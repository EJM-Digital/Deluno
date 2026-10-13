using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Usenet;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1257, from NZBGet 26.3's real replies: <c>config</c> answers the settings in memory plus four runtime-only values
/// (ConfigFile, AppBin, AppDir, Version). Deluno sent that whole list back through <c>saveconfig</c>, so nzbget.conf
/// gained four lines NZBGet rejects ("Invalid option"), and NZBGet then paused all downloads over errors in
/// configuration. <c>loadconfig</c> answers what the file holds, and that is what a save must start from. The file
/// recorded in <c>loadconfig.json</c> was taken after the bug had already damaged it, so a save must repair it too.
/// </summary>
public sealed class NzbGetSavesOnlyItsOwnFileOptionsTests
{
    private static readonly string[] RuntimeOnly = ["ConfigFile", "AppBin", "AppDir", "Version"];

    private static readonly UsenetProviderRequest Provider = new("news.example.com", 563, true, "jo", "pw", 8);

    private static DownloadClientItem NzbGetClient()
        => new("nzb-1", "NZBGet", "nzbget", "localhost", 6789, "nzbget", "tegbzn6789", "http://localhost:6789/", false, null,
            "Movies", "TV", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static List<KeyValuePair<string, string>> Options(JsonElement array)
        => array.EnumerateArray().Select(o => new KeyValuePair<string, string>(o.GetProperty("Name").GetString()!, o.GetProperty("Value").GetString() ?? string.Empty)).ToList();

    private static List<KeyValuePair<string, string>> RecordedOptions(Recording recording, string method)
    {
        using var document = JsonDocument.Parse(recording.Find(method).Reply.Body!);
        return Options(document.RootElement.GetProperty("result"));
    }

    private static string Reply(IEnumerable<KeyValuePair<string, string>> options)
        => JsonSerializer.Serialize(new { version = "1.1", result = options.Select(o => new { Name = o.Key, Value = o.Value }).ToArray() });

    /// <summary>
    /// NZBGet 26.3 as recorded: the file starts as the recorded loadconfig (already damaged unless asked otherwise),
    /// <c>config</c> is the file plus the recorded runtime values, and a save replaces the file with exactly what was sent.
    /// </summary>
    private sealed class Nzbget
    {
        private readonly Recording _settings = RecordedReplies.Nzbget("settings");
        private readonly Recording _loadconfig;
        private readonly bool _loadconfigFails;

        public Nzbget(bool damagedFile = true, bool loadconfigFails = false)
        {
            _loadconfigFails = loadconfigFails;
            // The file as the bug left it, or the file of an NZBGet that never had the bug (both recorded).
            _loadconfig = RecordedReplies.Nzbget(damagedFile ? "loadconfig" : "loadconfig-clean");
            File = RecordedOptions(_loadconfig, "loadconfig");

            Runtime = RecordedOptions(_settings, "\"config\"").Where(o => RuntimeOnly.Contains(o.Key)).ToList();
        }

        public List<KeyValuePair<string, string>> File { get; private set; }

        public List<KeyValuePair<string, string>> Runtime { get; }

        public List<List<KeyValuePair<string, string>>> Saves { get; } = [];

        public List<string> Methods { get; } = [];

        public HttpMessageHandler Handler() => RecordedReplies.Handler("nzbget", [_settings, _loadconfig], Intercept);

        private HttpResponseMessage? Intercept(HttpRequestMessage request, string body)
        {
            using var document = JsonDocument.Parse(body);
            var method = document.RootElement.GetProperty("method").GetString()!;
            Methods.Add(method);
            switch (method)
            {
                case "saveconfig":
                    var sent = Options(document.RootElement.GetProperty("params")[0]);
                    Saves.Add(sent);
                    File = sent;
                    return RecordedReplies.Json("""{"version":"1.1","result":true}""");
                case "config":
                    return RecordedReplies.Json(Reply(File.Concat(Runtime.Where(r => !File.Any(f => f.Key == r.Key)))));
                case "loadconfig":
                    return _loadconfigFails ? throw new HttpRequestException("loadconfig refused") : RecordedReplies.Json(Reply(File));
                case "reload":
                    return RecordedReplies.Json("""{"version":"1.1","result":true}""");
                default:
                    return null;
            }
        }
    }

    private static NzbGetDownloadClient Client(Nzbget nzbget) => new(new Factory(nzbget.Handler()), TimeSpan.Zero);

    [Fact]
    public async Task The_provider_save_sends_none_of_NZBGets_runtime_only_values_and_repairs_a_file_the_bug_already_damaged()
    {
        var nzbget = new Nzbget(damagedFile: true);
        var before = nzbget.File.Where(o => !RuntimeOnly.Contains(o.Key)).ToList();

        var result = await Client(nzbget).SetUsenetProviderAsync(NzbGetClient(), Provider, CancellationToken.None);

        Assert.True(result!.Saved, result.Message);
        var saved = Assert.Single(nzbget.Saves);
        Assert.DoesNotContain(saved, o => RuntimeOnly.Contains(o.Key));
        // Nothing else is lost or changed: every other option is still there with its value, bar the provider's own slot.
        foreach (var option in before.Where(o => !o.Key.StartsWith("Server", StringComparison.OrdinalIgnoreCase)))
        {
            Assert.Contains(option, saved);
        }

        Assert.Equal("news.example.com", saved.Single(o => o.Key == "Server1.Host").Value);
    }

    [Fact]
    public async Task The_save_is_built_from_the_file_the_loadconfig_call_and_not_from_the_in_memory_config()
    {
        var nzbget = new Nzbget(damagedFile: false);

        await Client(nzbget).SetUsenetProviderAsync(NzbGetClient(), Provider, CancellationToken.None);

        var before = nzbget.Methods.IndexOf("saveconfig");
        Assert.Contains("loadconfig", nzbget.Methods.Take(before));
        Assert.DoesNotContain("config", nzbget.Methods.Take(before));
    }

    [Fact]
    public async Task When_loadconfig_cannot_be_answered_the_save_falls_back_to_config_minus_the_runtime_only_values()
    {
        var nzbget = new Nzbget(damagedFile: false, loadconfigFails: true);
        // The stand-in answers config with the file plus the recorded runtime-only values, so config minus those is the file's own options.
        var inMemory = nzbget.File.Where(o => !RuntimeOnly.Contains(o.Key)).ToList();

        var result = await Client(nzbget).SetUsenetProviderAsync(NzbGetClient(), Provider, CancellationToken.None);

        Assert.True(result!.Saved, result.Message);
        var saved = Assert.Single(nzbget.Saves);
        Assert.DoesNotContain(saved, o => RuntimeOnly.Contains(o.Key));
        Assert.Equal(inMemory.Count(o => !o.Key.StartsWith("Server1.", StringComparison.OrdinalIgnoreCase)),
            saved.Count(o => !o.Key.StartsWith("Server1.", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Setting_the_provider_twice_leaves_a_valid_option_list_with_one_server_and_no_runtime_values()
    {
        var nzbget = new Nzbget(damagedFile: true);
        var client = Client(nzbget);

        await client.SetUsenetProviderAsync(NzbGetClient(), Provider, CancellationToken.None);
        var second = await client.SetUsenetProviderAsync(NzbGetClient(), Provider with { Host = "other.example.com" }, CancellationToken.None);

        Assert.True(second!.Saved, second.Message);
        Assert.Equal(2, nzbget.Saves.Count);
        Assert.All(nzbget.Saves, save => Assert.DoesNotContain(save, o => RuntimeOnly.Contains(o.Key)));
        Assert.DoesNotContain(nzbget.File, o => RuntimeOnly.Contains(o.Key));
        Assert.Equal(nzbget.Saves[0].Count, nzbget.Saves[1].Count);
        Assert.Equal("other.example.com", nzbget.File.Single(o => o.Key == "Server1.Host").Value);
    }

    [Fact]
    public void The_plan_never_carries_a_runtime_only_value_whichever_list_it_is_given()
    {
        var given = RecordedOptions(RecordedReplies.Nzbget("settings"), "\"config\"");

        var plan = NzbGetServerPlan.Apply(given, Provider);

        Assert.DoesNotContain(plan, o => RuntimeOnly.Contains(o.Key));
    }
}
