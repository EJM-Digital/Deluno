using Deluno.Connections.Versions;

namespace Deluno.Integrations.Tests.Versions;

/// <summary>#1117: each connection's version, read from a recorded answer. An answer of another shape is null, never an error.</summary>
public sealed class ConnectionVersionParserTests
{
    [Theory]
    [InlineData("v4.6.7", "4.6.7")]
    [InlineData("v5.0.2\n", "5.0.2")]
    [InlineData("5.0.0beta1", "5.0.0beta1")]
    public void Qbittorrent_answers_the_bare_version_text(string body, string expected)
        => Assert.Equal(expected, ConnectionVersionParsers.FromQbittorrent(body));

    [Theory]
    [InlineData("")]
    [InlineData("Forbidden")]
    [InlineData("<html>login</html>")]
    public void Qbittorrent_says_nothing_for_an_answer_that_is_not_a_version(string body)
        => Assert.Null(ConnectionVersionParsers.FromQbittorrent(body));

    [Fact]
    public void Transmission_reads_the_version_without_its_build_hash()
        => Assert.Equal("4.0.5", ConnectionVersionParsers.FromTransmission(
            """{"arguments":{"download-dir":"/downloads","rpc-version":18,"rpc-version-minimum":14,"version":"4.0.5 (a6fe2de319)"},"result":"success"}"""));

    [Fact]
    public void Transmission_without_a_version_field_says_nothing()
        => Assert.Null(ConnectionVersionParsers.FromTransmission("""{"arguments":{"rpc-version":15},"result":"success"}"""));

    [Fact]
    public void Deluge_reads_the_daemon_info_result()
        => Assert.Equal("2.1.1", ConnectionVersionParsers.FromDeluge("""{"result":"2.1.1","error":null,"id":9}"""));

    [Fact]
    public void Deluge_error_answer_says_nothing()
        => Assert.Null(ConnectionVersionParsers.FromDeluge("""{"result":null,"error":{"message":"Unknown method","code":2},"id":9}"""));

    [Fact]
    public void Plex_reads_the_identity_version_without_its_hash_kept_as_part_of_the_number()
        => Assert.Equal("1.43.4.10903-e5521bd8c", ConnectionVersionParsers.FromPlexIdentity(
            """{"MediaContainer":{"size":0,"claimed":true,"machineIdentifier":"abc123","version":"1.43.4.10903-e5521bd8c"}}"""));

    [Fact]
    public void Jellyfin_and_emby_read_the_version_field()
    {
        Assert.Equal("10.10.7", ConnectionVersionParsers.FromEmbyFamily("""{"LocalAddress":"http://x:8096","ServerName":"jf","Version":"10.10.7","ProductName":"Jellyfin Server","Id":"abc"}"""));
        Assert.Equal("4.9.1.0", ConnectionVersionParsers.FromEmbyFamily("""{"ServerName":"emby","Version":"4.9.1.0","Id":"abc"}"""));
    }

    [Fact]
    public void Seerr_reads_the_status_version()
        => Assert.Equal("3.5.0", ConnectionVersionParsers.FromSeerr("""{"version":"3.5.0","commitTag":"abc","updateAvailable":false,"commitsBehind":0,"restartRequired":false}"""));

    [Fact]
    public void A_processor_publishes_its_version_in_its_capabilities()
    {
        Assert.Equal("4.0.0", ConnectionVersionParsers.FromProcessorCapabilities("""{"machine_name":"nas","capabilities":["handoff-status"],"version":"4.0.0"}"""));
        Assert.Equal("0.0.1-dev", ConnectionVersionParsers.FromProcessorCapabilities("""{"version":"0.0.1-dev"}"""));
    }

    [Fact]
    public void A_processor_before_it_published_a_version_says_nothing()
        => Assert.Null(ConnectionVersionParsers.FromProcessorCapabilities("""{"machine_name":"nas","capabilities":["handoff-status"]}"""));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"version":12}""")]
    [InlineData("""{"version":"banana"}""")]
    public void An_unknown_shape_is_null_for_every_reader(string? body)
    {
        Assert.Null(ConnectionVersionParsers.FromTransmission(body));
        Assert.Null(ConnectionVersionParsers.FromDeluge(body));
        Assert.Null(ConnectionVersionParsers.FromPlexIdentity(body));
        Assert.Null(ConnectionVersionParsers.FromEmbyFamily(body));
        Assert.Null(ConnectionVersionParsers.FromSeerr(body));
        Assert.Null(ConnectionVersionParsers.FromProcessorCapabilities(body));
    }
}
