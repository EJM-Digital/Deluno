using System.Text.Json;

namespace Deluno.Worker.Jobs;

internal static class JobPayloads
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal sealed record LibrarySearchPayload(
        string LibraryId,
        string LibraryName,
        string MediaType,
        bool CheckMissing,
        bool CheckUpgrades,
        int MaxItems,
        int RetryDelayHours,
        string TriggeredBy,
        string? TargetEntityId = null,
        string SearchKind = "combined");

    internal sealed record LibraryQualityPayload(
        string LibraryId,
        string LibraryName,
        string MediaType,
        string? CutoffQuality,
        bool UpgradeUntilCutoff,
        bool UpgradeUnknownItems);

    internal sealed record EpisodeSearchPayload(
        string EpisodeId,
        string SeriesId,
        string LibraryId,
        int SeasonNumber,
        int EpisodeNumber,
        string Title);

    internal sealed record IntakeSyncPayload(
        string? SourceId,
        bool Manual);

    internal static LibrarySearchPayload? ParseLibraryPayload(string? payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<LibrarySearchPayload>(payloadJson ?? "{}", Options);
        }
        catch
        {
            return null;
        }
    }

    internal static LibraryQualityPayload? ParseQualityPayload(string? payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<LibraryQualityPayload>(payloadJson ?? "{}", Options);
        }
        catch
        {
            return null;
        }
    }

    internal static EpisodeSearchPayload? ParseEpisodeSearchPayload(string? payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<EpisodeSearchPayload>(payloadJson ?? "{}", Options);
        }
        catch
        {
            return null;
        }
    }

    internal static IntakeSyncPayload? ParseIntakeSyncPayload(string? payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<IntakeSyncPayload>(payloadJson ?? "{}", Options);
        }
        catch
        {
            return null;
        }
    }
}
