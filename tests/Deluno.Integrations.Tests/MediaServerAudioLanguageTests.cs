using System.Text.Json;
using Deluno.Integrations.MediaServers;

namespace Deluno.Integrations.Tests;

/// <summary>
/// #1003 and #1007: what a Plex, Jellyfin or Emby reply says about audio
/// languages and smart collections. Whatever is uncertain must come out as
/// null (not known), because a known-but-wrong list lets a delete rule match.
/// </summary>
public sealed class MediaServerAudioLanguageTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Plex_reads_the_union_of_every_version_and_part()
    {
        var entry = Json("""
            {"Media":[
              {"Part":[{"Stream":[{"streamType":1},{"streamType":2,"languageCode":"eng"}]}]},
              {"Part":[{"Stream":[{"streamType":2,"languageCode":"fre"}]},{"Stream":[{"streamType":2,"language":"German"}]}]}
            ]}
            """);

        Assert.Equal(["English", "French", "German"], PlexMediaServerClient.PlexAudioLanguages(entry));
    }

    [Theory]
    [InlineData("""{"Media":[{"Part":[{"Stream":[{"streamType":2,"languageCode":"eng"}]}]},{"Part":[{"file":"x"}]}]}""")]
    [InlineData("""{"Media":[{"Part":[{"Stream":[{"streamType":2,"languageCode":"eng"}]}]},{"file":"x"}]}""")]
    [InlineData("""{"Media":[{"Part":[{"Stream":[{"streamType":2}]}]}]}""")]
    [InlineData("""{"Media":[{"Part":[{"Stream":[{"streamType":2,"languageCode":"xyz"}]}]}]}""")]
    [InlineData("""{"Media":[{"Part":[{"Stream":[{"streamType":2,"languageCode":"und"}]}]}]}""")]
    [InlineData("""{"Media":[{"Part":[{"Stream":[{"streamType":1}]}]}]}""")]
    [InlineData("""{"Media":[]}""")]
    [InlineData("""{}""")]
    public void Plex_is_unknown_when_any_version_lacks_streams_or_any_track_cannot_be_named(string json)
    {
        Assert.Null(PlexMediaServerClient.PlexAudioLanguages(Json(json)));
    }

    [Fact]
    public void Jellyfin_and_Emby_read_the_union_of_every_media_source()
    {
        var sources = Json("""
            [
              {"MediaStreams":[{"Type":"Video"},{"Type":"Audio","Language":"eng"}]},
              {"MediaStreams":[{"Type":"Audio","Language":"fra"},{"Type":"Audio","Language":"jpn"}]}
            ]
            """);

        Assert.Equal(["English", "French", "Japanese"], EmbyFamilyMediaServerClient.AudioLanguagesOfAllVersions(sources));
    }

    [Theory]
    [InlineData("""[{"MediaStreams":[{"Type":"Audio","Language":"eng"}]},{"Path":"x"}]""")]
    [InlineData("""[{"MediaStreams":[{"Type":"Audio","Language":"eng"}]},{"MediaStreams":[{"Type":"Audio"}]}]""")]
    [InlineData("""[{"MediaStreams":[{"Type":"Audio","Language":"tlh"}]}]""")]
    [InlineData("""[{"MediaStreams":[{"Type":"Video"}]}]""")]
    public void Jellyfin_and_Emby_are_unknown_when_any_source_is_unreadable(string json)
    {
        Assert.Null(EmbyFamilyMediaServerClient.AudioLanguagesOfAllVersions(Json(json)));
    }

    [Theory]
    [InlineData("""{"smart":true}""", true)]
    [InlineData("""{"smart":"true"}""", true)]
    [InlineData("""{"smart":"TRUE"}""", true)]
    [InlineData("""{"smart":"1"}""", true)]
    [InlineData("""{"smart":1}""", true)]
    [InlineData("""{"smart":false}""", false)]
    [InlineData("""{"smart":"false"}""", false)]
    [InlineData("""{"smart":"0"}""", false)]
    [InlineData("""{"smart":0}""", false)]
    [InlineData("""{"title":"ordinary"}""", false)]
    public void Plex_smart_flag_accepts_every_documented_spelling(string json, bool expected)
    {
        Assert.Equal(expected, PlexMediaServerClient.SmartFlag(Json(json)));
    }

    [Theory]
    [InlineData("""{"smart":"maybe"}""")]
    [InlineData("""{"smart":2}""")]
    [InlineData("""{"smart":null}""")]
    [InlineData("""{"smart":{}}""")]
    public void Plex_smart_flag_that_cannot_be_read_is_not_known(string json)
    {
        Assert.Null(PlexMediaServerClient.SmartFlag(Json(json)));
    }
}
