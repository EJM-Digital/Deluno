using Deluno.Integrations.DownloadClients;
using Deluno.Platform.Contracts;
using Deluno.Connections.Contracts;

namespace Deluno.Persistence.Tests.Integrations;

public sealed class DownloadClientPathMappingTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-14T00:00:00Z");

    [Fact]
    public void TranslateRemotePath_translates_the_client_path_to_the_deluno_visible_path()
    {
        var mapping = Mapping("/downloads/complete", "D:\\Downloads\\complete");

        var result = DownloadClientTelemetryService.TranslateRemotePath(
            "/downloads/complete/Film/Film.mkv",
            [mapping]);

        Assert.Equal(Path.Combine("D:\\Downloads\\complete", "Film", "Film.mkv"), result);
    }

    [Fact]
    public void TranslateRemotePath_uses_the_most_specific_matching_location_link()
    {
        var result = DownloadClientTelemetryService.TranslateRemotePath(
            "/downloads/complete/tv/Show/episode.mkv",
            [
                Mapping("/downloads/complete", "D:\\Downloads"),
                Mapping("/downloads/complete/tv", "E:\\Television intake")
            ]);

        Assert.Equal(Path.Combine("E:\\Television intake", "Show", "episode.mkv"), result);
    }

    [Fact]
    public void TranslateRemotePath_preserves_the_reported_path_when_no_location_link_applies()
    {
        const string reportedPath = "/downloads/complete/Film/Film.mkv";

        var result = DownloadClientTelemetryService.TranslateRemotePath(
            reportedPath,
            [Mapping("/different-root", "D:\\Downloads")]);

        Assert.Equal(reportedPath, result);
    }

    private static DownloadClientPathMappingItem Mapping(string remotePath, string localPath) => new(
        Id: Guid.NewGuid().ToString("N"),
        DownloadClientId: "client-1",
        RemotePath: remotePath,
        LocalPath: localPath,
        IsEnabled: true,
        Priority: 10,
        CreatedUtc: Now,
        UpdatedUtc: Now);
}
