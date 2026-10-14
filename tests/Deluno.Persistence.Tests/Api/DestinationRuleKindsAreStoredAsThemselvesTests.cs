using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1322: a destination rule of any kind the screen offers (read from the web app's own list) is stored as that kind, on
/// create and on update, and a kind nobody offers is refused with a 400 instead of being saved as a genre rule.
/// </summary>
public sealed class DestinationRuleKindsAreStoredAsThemselvesTests
{
    [Fact]
    public async Task Every_kind_the_web_app_offers_is_stored_as_itself_and_an_unknown_kind_is_refused()
    {
        var offered = KindsOfferedByTheWebApp();
        Assert.True(offered.Count >= 8, "the web app's kinds were not read: " + string.Join(",", offered));
        await using var app = await ApplicationTestHost.StartAsync();
        var folder = Path.Combine(app.DataRoot, "rule-root");
        Directory.CreateDirectory(folder);

        foreach (var kind in offered.Concat(["studio", "title"]).Distinct())
        {
            var created = await app.Client.PostAsJsonAsync("/api/destination-rules", Body(kind, folder));
            Assert.True(created.IsSuccessStatusCode, $"create {kind}: {await created.Content.ReadAsStringAsync()}");
            var rule = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(kind, rule.GetProperty("matchKind").GetString());

            // Updating it to the same kind (and from another kind) keeps the kind too.
            var id = rule.GetProperty("id").GetString();
            var updated = await app.Client.PutAsJsonAsync($"/api/destination-rules/{id}", Body(kind, folder));
            Assert.Equal(kind, JsonDocument.Parse(await updated.Content.ReadAsStringAsync()).RootElement.GetProperty("matchKind").GetString());
        }

        var listed = JsonDocument.Parse(await app.Client.GetStringAsync("/api/destination-rules")).RootElement
            .EnumerateArray().Select(item => item.GetProperty("matchKind").GetString()).ToList();
        Assert.Equal(offered.Concat(["studio", "title"]).Distinct().Order(), listed.Order());

        var refused = await app.Client.PostAsJsonAsync("/api/destination-rules", Body("producer", folder));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("matchKind", await refused.Content.ReadAsStringAsync());

        var someRule = JsonDocument.Parse(await app.Client.GetStringAsync("/api/destination-rules")).RootElement[0].GetProperty("id").GetString();
        var refusedUpdate = await app.Client.PutAsJsonAsync($"/api/destination-rules/{someRule}", Body("producer", folder));
        Assert.Equal(HttpStatusCode.BadRequest, refusedUpdate.StatusCode);
        Assert.Equal(offered.Concat(["studio", "title"]).Distinct().Count(),
            JsonDocument.Parse(await app.Client.GetStringAsync("/api/destination-rules")).RootElement.GetArrayLength());
    }

    private static object Body(string kind, string folder) => new
    {
        name = "Rule " + kind, mediaType = "movies", matchKind = kind, matchValue = "x", rootPath = folder,
        folderTemplate = (string?)null, priority = 10, isEnabled = true
    };

    private static List<string> KindsOfferedByTheWebApp()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Deluno.slnx"))) directory = directory.Parent;
        var page = File.ReadAllText(Path.Combine(directory!.FullName, "apps", "web", "src", "routes", "settings-destination-rules-page.tsx"));
        var block = Regex.Match(page, @"MATCH_KIND_LABELS: Record<string, string> = \{(?<body>.*?)\};", RegexOptions.Singleline).Groups["body"].Value;
        return Regex.Matches(block, @"^\s*(\w+):", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
    }
}
