using Deluno.Connections.Versions;

namespace Deluno.Integrations.Tests.Versions;

public sealed class ConnectionProjectsTests
{
    [Theory]
    [InlineData("qbittorrent", ConnectionProjects.Qbittorrent)]
    [InlineData(" Transmission ", ConnectionProjects.Transmission)]
    [InlineData("deluge", ConnectionProjects.Deluge)]
    [InlineData("SABnzbd", ConnectionProjects.Sabnzbd)]
    [InlineData("nzbget", ConnectionProjects.Nzbget)]
    public void A_download_client_protocol_maps_to_the_project_whose_releases_it_is_compared_with(string protocol, string project)
        => Assert.Equal(project, ConnectionProjects.ForDownloadClient(protocol));

    [Theory]
    [InlineData("utorrent")]
    [InlineData("")]
    [InlineData(null)]
    public void A_client_with_no_release_feed_has_no_project(string? protocol)
        => Assert.Null(ConnectionProjects.ForDownloadClient(protocol));

    [Theory]
    [InlineData(ConnectionProjects.Qbittorrent, "qBittorrent")]
    [InlineData(ConnectionProjects.Transmission, "Transmission")]
    [InlineData(ConnectionProjects.Deluge, "Deluge")]
    [InlineData(ConnectionProjects.Sabnzbd, "SABnzbd")]
    [InlineData(ConnectionProjects.Nzbget, "NZBGet")]
    [InlineData(ConnectionProjects.Plex, "Plex")]
    [InlineData(ConnectionProjects.Jellyfin, "Jellyfin")]
    [InlineData(ConnectionProjects.Emby, "Emby")]
    [InlineData(ConnectionProjects.Seerr, "Seerr")]
    [InlineData(ConnectionProjects.Weir, "Weir")]
    [InlineData("other-project", "other-project")]
    public void A_project_is_named_the_way_a_person_knows_it(string project, string name)
        => Assert.Equal(name, ConnectionProjects.DisplayName(project));

    [Theory]
    [InlineData("plex", "plex")]
    [InlineData("JELLYFIN", "jellyfin")]
    [InlineData("kodi", null)]
    public void A_media_server_kind_maps_through_the_shared_normaliser(string kind, string? expected)
        => Assert.Equal(expected, ConnectionProjects.ForMediaServer(kind));
}
