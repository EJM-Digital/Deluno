namespace Deluno.Platform.Presets;

public sealed record QualityProfilePreset(
    string Id,
    string Name,
    string Description,
    string MediaType,
    string CutoffQuality,
    string AllowedQualities,
    bool UpgradeUntilCutoff,
    bool UpgradeUnknownItems,
    int Version);

public static class QualityProfilePresetCatalog
{
    public static readonly IReadOnlyList<QualityProfilePreset> All =
    [
        new QualityProfilePreset(
            Id: "standard-movies",
            Name: "Standard Movies",
            Description: "WEB releases up to 1080p. Good quality for general viewing without large file sizes.",
            MediaType: "movies",
            CutoffQuality: "WEB 1080p",
            AllowedQualities: "WEB 720p,WEB 1080p,Bluray 1080p",
            UpgradeUntilCutoff: true,
            UpgradeUnknownItems: true,
            Version: 1),

        new QualityProfilePreset(
            Id: "premium-movies",
            Name: "Premium Movies (Remux)",
            Description: "Blu-ray Remux for maximum quality. Best for home theater setups with large storage.",
            MediaType: "movies",
            CutoffQuality: "Remux 1080p",
            AllowedQualities: "WEB 1080p,Bluray 1080p,Remux 1080p",
            UpgradeUntilCutoff: true,
            UpgradeUnknownItems: true,
            Version: 1),

        new QualityProfilePreset(
            Id: "4k-movies",
            Name: "4K HDR Movies",
            Description: "4K WEB and Blu-ray releases. Requires a 4K display and capable playback device.",
            MediaType: "movies",
            CutoffQuality: "WEB 2160p",
            AllowedQualities: "WEB 2160p,Bluray 2160p,Remux 2160p",
            UpgradeUntilCutoff: true,
            UpgradeUnknownItems: true,
            Version: 1),

        new QualityProfilePreset(
            Id: "any-movies",
            Name: "Any Quality Movies",
            Description: "Grab whatever is available first. No upgrades after the first file is downloaded.",
            MediaType: "movies",
            CutoffQuality: "WEB 720p",
            AllowedQualities: "SDTV,DVD,HDTV 720p,WEB 720p,Bluray 720p,HDTV 1080p,WEB 1080p,Bluray 1080p,Remux 1080p,WEB 2160p,Bluray 2160p,Remux 2160p",
            UpgradeUntilCutoff: false,
            UpgradeUnknownItems: false,
            Version: 1),

        new QualityProfilePreset(
            Id: "standard-tv",
            Name: "Standard TV",
            Description: "WEB 720p for TV shows. Fast availability with reasonable file sizes.",
            MediaType: "tv",
            CutoffQuality: "WEB 720p",
            AllowedQualities: "WEB 720p,WEB 1080p",
            UpgradeUntilCutoff: false,
            UpgradeUnknownItems: true,
            Version: 1),

        new QualityProfilePreset(
            Id: "hd-tv",
            Name: "HD TV",
            Description: "1080p WEB target for TV shows. Upgrades from 720p when better releases appear.",
            MediaType: "tv",
            CutoffQuality: "WEB 1080p",
            AllowedQualities: "WEB 720p,WEB 1080p,Bluray 1080p",
            UpgradeUntilCutoff: true,
            UpgradeUnknownItems: true,
            Version: 1),

        new QualityProfilePreset(
            Id: "4k-tv",
            Name: "4K TV",
            Description: "4K WEB releases for TV. Suitable for premium streaming content with 4K masters.",
            MediaType: "tv",
            CutoffQuality: "WEB 2160p",
            AllowedQualities: "WEB 1080p,WEB 2160p,Bluray 2160p",
            UpgradeUntilCutoff: true,
            UpgradeUnknownItems: true,
            Version: 1),
    ];

    public static QualityProfilePreset? FindById(string id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}
