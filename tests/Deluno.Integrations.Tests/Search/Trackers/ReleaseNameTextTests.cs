using System.Text;
using System.Text.Json;
using Deluno.Integrations.Search.Trackers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Integrations.Tests.Search.Trackers;

/// <summary>#1102: names a tracker sent as UTF-8 read as Windows-1252 are put right.</summary>
public sealed class ReleaseNameTextTests
{
    private static string Garble(string text) =>
        CodePagesEncodingProvider.Instance.GetEncoding(1252)!.GetString(Encoding.UTF8.GetBytes(text));

    [Theory]
    [InlineData("Синтел  Sintel (2010) DMRip 1080p от Мифограда")]
    [InlineData("Мечта слонов  Elephants Dream (2006)")]
    public void A_cyrillic_name_read_as_windows_1252_is_repaired(string intended)
    {
        var garbled = Garble(intended);

        Assert.NotEqual(intended, garbled);
        Assert.Equal(intended, ReleaseNameText.RepairMojibake(garbled));
    }

    [Theory]
    [InlineData("Синтел  Sintel (2010) DMRip 1080p")]
    [InlineData("Amélie (2001) 1080p")]
    [InlineData("Night.of.the.Living.Dead.1968.1080p.BluRay.x264-AMIAB")]
    [InlineData("Ã stray accent that is not UTF-8 Ã")]
    [InlineData("")]
    public void A_name_that_is_already_right_is_left_alone(string name)
    {
        Assert.Equal(name, ReleaseNameText.RepairMojibake(name));
    }

    [Fact]
    public void A_parsed_title_comes_out_repaired()
    {
        const string intended = "Мечта слонов  Elephants Dream (2006)";
        var body = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, string>
            {
                ["id"] = "1", ["name"] = Garble(intended), ["info_hash"] = "EA7DCDFD19AE0BE46E3BF52EF310225F7AA97855",
                ["leechers"] = "1", ["seeders"] = "2", ["size"] = "1000", ["num_files"] = "1",
                ["username"] = "u", ["added"] = "1539729341", ["status"] = "vip", ["category"] = "207", ["imdb"] = "tt0063350",
            },
        });
        var catalog = new TrackerDefinitionCatalog(overrideDirectory: null, NullLogger<TrackerDefinitionCatalog>.Instance);
        var definition = catalog.Find("thepiratebay")!;
        var link = definition.Links[0];
        var connection = new TrackerConnection("test", definition, link, new Dictionary<string, string?>());
        var variables = TrackerVariables.Build(connection, new TrackerQuery("Elephants Dream", "movie", Categories: [2000]), link);

        var result = TrackerResponseParser.Parse(definition, definition.Search.Paths[0], body, new Uri(link), variables, new TrackerContext(DateTimeOffset.UtcNow));

        Assert.Equal(intended, Assert.Single(result.Releases).Title);
    }
}
