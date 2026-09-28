using Deluno.Platform.Contracts;
using Deluno.Platform.Processing;

namespace Deluno.Platform.Tests.Processing;

/// <summary>
/// #962: a processor connection's optional path pairs, the same "Deluno sees" /
/// "processor sees" translation download client remote path mappings already
/// do (#809), built on the same shared matcher
/// (<see cref="Deluno.Contracts.PathMappingTranslator"/>).
/// </summary>
public sealed class ProcessorPathMappingTranslatorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-28T00:00:00Z");

    [Fact]
    public void ToProcessor_translates_delunos_own_path_to_what_the_processor_should_see()
    {
        var mapping = Mapping("D:\\Downloads\\complete", "/data/downloads");

        var result = ProcessorPathMappingTranslator.ToProcessor(
            "D:\\Downloads\\complete\\Film\\Film.mkv",
            [mapping]);

        Assert.Equal(Path.Combine("/data/downloads", "Film", "Film.mkv").Replace('\\', '/'), result!.Replace('\\', '/'));
    }

    [Fact]
    public void ToDeluno_translates_the_processors_reported_path_back_to_delunos_own()
    {
        var mapping = Mapping("D:\\Refined", "/data/refined");

        var result = ProcessorPathMappingTranslator.ToDeluno(
            "/data/refined/Film/Film.mkv",
            [mapping]);

        Assert.Equal(Path.Combine("D:\\Refined", "Film", "Film.mkv"), result);
    }

    [Fact]
    public void The_most_specific_matching_pair_wins()
    {
        var result = ProcessorPathMappingTranslator.ToDeluno(
            "/data/refined/tv/Show/episode.mkv",
            [
                Mapping("D:\\Refined", "/data/refined"),
                Mapping("E:\\Television refined", "/data/refined/tv")
            ]);

        Assert.Equal(Path.Combine("E:\\Television refined", "Show", "episode.mkv"), result);
    }

    [Fact]
    public void A_path_that_matches_no_pair_passes_through_unchanged()
    {
        const string reportedPath = "/somewhere/else/Film.mkv";

        var result = ProcessorPathMappingTranslator.ToDeluno(
            reportedPath,
            [Mapping("D:\\Refined", "/data/refined")]);

        Assert.Equal(reportedPath, result);
    }

    [Fact]
    public void With_no_mappings_configured_the_path_passes_through_unchanged()
    {
        const string reportedPath = "D:\\Downloads\\complete\\Film\\Film.mkv";

        Assert.Equal(reportedPath, ProcessorPathMappingTranslator.ToProcessor(reportedPath, []));
        Assert.Equal(reportedPath, ProcessorPathMappingTranslator.ToDeluno(reportedPath, []));
    }

    [Fact]
    public void A_disabled_pair_is_never_matched()
    {
        var mapping = Mapping("D:\\Refined", "/data/refined") with { IsEnabled = false };

        var result = ProcessorPathMappingTranslator.ToDeluno("/data/refined/Film.mkv", [mapping]);

        Assert.Equal("/data/refined/Film.mkv", result);
    }

    private static ProcessorPathMappingItem Mapping(string delunoPath, string processorPath) => new(
        Id: Guid.NewGuid().ToString("N"),
        ProcessorConnectionId: "connection-1",
        DelunoPath: delunoPath,
        ProcessorPath: processorPath,
        IsEnabled: true,
        Priority: 10,
        CreatedUtc: Now,
        UpdatedUtc: Now);
}
