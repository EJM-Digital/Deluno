using Deluno.Connections.Versions;
using Deluno.Tests.Recorded;

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
    public void Qbittorrent_reads_its_recorded_version_answer()
        => Assert.Equal("5.2.1", ConnectionVersionParsers.FromQbittorrent(RecordedReplies.Qbittorrent("version").Single.Reply.Body));

    [Fact]
    public void Transmission_reads_the_version_without_its_build_hash()
        => Assert.Equal("4.1.3", ConnectionVersionParsers.FromTransmission(RecordedReplies.Transmission("version").Body("session-get")));

    [Fact]
    public void Transmission_without_a_version_field_says_nothing()
        => Assert.Null(ConnectionVersionParsers.FromTransmission(RecordedReplies.Transmission("version").Body("session-get", ("arguments.version", null))));

    [Fact]
    public void Deluge_reads_the_daemon_info_result()
        => Assert.Equal("2.2.0", ConnectionVersionParsers.FromDeluge(RecordedReplies.Deluge("version").Body("daemon.get_version")));

    /// <summary>An answer with an error and no result: Deluge 2.2.0's recorded "Not authenticated" reply.</summary>
    [Fact]
    public void Deluge_error_answer_says_nothing()
        => Assert.Null(ConnectionVersionParsers.FromDeluge(RecordedReplies.Deluge("unauthenticated-call").Body("web.connected")));

    [Fact]
    public void Plex_reads_the_identity_version_without_its_hash_kept_as_part_of_the_number()
        => Assert.Equal("1.43.4.10903-e5521bd8c", ConnectionVersionParsers.FromPlexIdentity(RecordedReplies.Plex("identity").Single.Reply.Body));

    [Fact]
    public void Jellyfin_and_emby_read_the_version_field()
    {
        // Jellyfin 12.2.0's real replies (#1237): the public identity and the full system info both carry the Version.
        Assert.Equal("12.2.0", ConnectionVersionParsers.FromEmbyFamily(RecordedReplies.Jellyfin("identity").Exchanges[0].Reply.Body));
        Assert.Equal("12.2.0", ConnectionVersionParsers.FromEmbyFamily(RecordedReplies.Jellyfin("identity").Exchanges[1].Reply.Body));
        // Emby 4.10.1.0's real replies (Docker): the public identity and the full system info carry the Version the same way.
        Assert.Equal("4.10.1.0", ConnectionVersionParsers.FromEmbyFamily(RecordedReplies.Emby("identity").Exchanges[0].Reply.Body));
        Assert.Equal("4.10.1.0", ConnectionVersionParsers.FromEmbyFamily(RecordedReplies.Emby("identity").Exchanges[1].Reply.Body));
    }

    [Fact]
    public void Seerr_reads_the_status_version()
        => Assert.Equal("3.5.0", ConnectionVersionParsers.FromSeerr(RecordedReplies.Seerr("status").Single.Reply.Body));

    [Fact]
    public void A_processor_publishes_its_version_in_its_capabilities()
    {
        Assert.Equal("1.0.0-rc.18", ConnectionVersionParsers.FromProcessorCapabilities(RecordedReplies.Weir("capabilities").Body("capabilities")));
        Assert.Equal("0.0.1-dev", ConnectionVersionParsers.FromProcessorCapabilities(RecordedReplies.Weir("capabilities").Body("capabilities", ("version", "0.0.1-dev"))));
    }

    [Fact]
    public void A_processor_before_it_published_a_version_says_nothing()
        => Assert.Null(ConnectionVersionParsers.FromProcessorCapabilities(RecordedReplies.Weir("capabilities").Body("capabilities", ("version", null))));

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
