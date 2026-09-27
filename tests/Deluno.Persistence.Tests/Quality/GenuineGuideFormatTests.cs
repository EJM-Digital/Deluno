using System.Text.Json;
using Deluno.Quality;
using Deluno.Quality.Guides;
using Deluno.Persistence.Tests.Support;
using Deluno.Quality.ReleasePreferences;

namespace Deluno.Persistence.Tests.Quality;

/// <summary>
/// The guide catalogue Deluno ships claims to be TRaSH's. Until package 4 it
/// was not: 85 entries, 7 with the right TRaSH id, 15 carrying the id of a
/// different TRaSH format ("Amazon Prime" was Crave, "Disney+" was Pathe) and
/// the rest invented - and nothing checked. These keep it honest.
/// </summary>
public sealed class GenuineGuideFormatTests
{
    private static readonly GuideSourceInventory Inventory = GuideSourceInventoryCatalog.Current;

    [Fact]
    public void Every_shipped_entry_is_either_the_trash_format_its_id_names_or_a_deluno_owned_extra()
    {
        // The file as written, before the inventory is merged in: the merge
        // takes TRaSH's name, so a mislabelled entry is only visible here.
        using var stream = typeof(GuidePackageCatalog).Assembly
            .GetManifestResourceStream("Deluno.Quality.Guides.trash-guide-package.json")!;
        var shipped = JsonSerializer.Deserialize<GuidePackage>(stream, ReleasePreferenceJson.Options)!;
        var sourceById = Inventory.CustomFormats.ToDictionary(format => format.TrashId, StringComparer.OrdinalIgnoreCase);

        Assert.True(shipped.SchemaVersion >= GuidePackageCatalog.GenuineFormatsSchemaVersion);
        Assert.NotEmpty(shipped.CustomFormats);
        foreach (var format in shipped.CustomFormats)
        {
            if (format.SourceKind == GuidePackageCatalog.GenuineSourceKind)
            {
                Assert.True(sourceById.TryGetValue(format.TrashId, out var source), $"{format.Name} claims {format.TrashId}, which TRaSH does not publish.");
                Assert.Equal(source!.Name, format.Name);
                Assert.Empty(format.Patterns);
            }
            else
            {
                Assert.Equal(GuidePackageCatalog.DelunoSourceKind, format.SourceKind);
                Assert.StartsWith(GuidePackageCatalog.DelunoIdPrefix, format.TrashId, StringComparison.Ordinal);
                Assert.False(sourceById.ContainsKey(format.TrashId));
                Assert.NotEmpty(format.Patterns);
            }
        }
    }

    [Fact]
    public void Guide_profiles_and_presets_only_name_formats_for_their_own_media_type()
    {
        var package = GuidePackageCatalog.Current;
        var byId = package.CustomFormats.ToDictionary(format => format.TrashId, StringComparer.OrdinalIgnoreCase);

        foreach (var profile in package.QualityProfiles)
        {
            foreach (var recommendation in profile.RecommendedFormats)
            {
                var format = byId[recommendation.TrashId];
                // TRaSH ships anime release-group tiers and version tags twice
                // over, once scraped from Radarr's guide and once from
                // Sonarr's, under the same name but a different trashId
                // (#781). An "anime" profile legitimately recommends whichever
                // flavour it was curated from, so it accepts either rather
                // than the single strict bucket a movies or tv profile owes.
                var appliesToAnime = profile.MediaType == "anime" &&
                    (GuideFormatConditions.AppliesTo(format, "movies") || GuideFormatConditions.AppliesTo(format, "tv"));
                var mediaType = profile.MediaType == "movies" ? "movies" : "tv";
                Assert.True(appliesToAnime || GuideFormatConditions.AppliesTo(format, mediaType),
                    $"{profile.Id} ({profile.MediaType}) recommends {format.Name} {recommendation.TrashId}, which is not a {(profile.MediaType == "anime" ? "movies or tv" : mediaType)} format.");
            }
        }

        foreach (var bundle in package.Bundles.Where(bundle => bundle.MediaType is "movies" or "tv"))
        {
            Assert.All(bundle.Includes, entry => Assert.True(
                GuideFormatConditions.AppliesTo(byId[entry.TrashId], bundle.MediaType),
                $"{bundle.Id} includes {entry.TrashId}, which is not a {bundle.MediaType} format."));
        }
    }

    [Fact]
    public void A_genuine_format_makes_rules_with_trash_specifications_and_an_extra_with_its_patterns()
    {
        var amazonTv = Assert.Single(GuidePackageCatalog.Current.CustomFormats, format => format.TrashId == "d660701077794679fd59e8bdf4ce3a29");
        Assert.Equal("AMZN", amazonTv.Name);
        Assert.Equal(["tv"], amazonTv.MediaTypes);
        Assert.True(GuideFormatConditions.IsGenuine(amazonTv));
        Assert.True(GuideFormatConditions.IsSpecificationShaped(GuideFormatConditions.For(amazonTv)));
        Assert.True(CustomFormatDefinitionMatcher.CanEvaluate(GuideFormatConditions.For(amazonTv), "tv"));
        Assert.False(GuideFormatConditions.AppliesTo(amazonTv, "movies"));

        var hdr10 = Assert.Single(GuidePackageCatalog.Current.CustomFormats, format => format.TrashId == "deluno-hdr-hdr10");
        Assert.False(GuideFormatConditions.IsGenuine(hdr10));
        Assert.True(GuideFormatConditions.AppliesTo(hdr10, "movies"));
        Assert.True(GuideFormatConditions.AppliesTo(hdr10, "tv"));
        Assert.Equal(JsonSerializer.Serialize(hdr10.Patterns), GuideFormatConditions.For(hdr10));
    }

    /// <summary>
    /// An install that has stored a guide package keeps using it over the one
    /// Deluno ships, so without this it would keep the old catalogue for ever.
    /// </summary>
    [Fact]
    public async Task A_stored_package_from_before_genuine_formats_gets_the_new_layer_as_a_new_version()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-16T00:00:00Z"));
        await new Deluno.Platform.Data.PlatformSchemaInitializer(
            storage.Factory,
            new Deluno.Infrastructure.Storage.Migrations.SqliteDatabaseMigrator(storage.Factory, clock),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Deluno.Platform.Data.PlatformSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);
        var store = new SqliteGuidePackageStore(storage.Factory, clock);
        var shipped = GuidePackageCatalog.Current;

        // Nothing stored: the shipped package is already current.
        Assert.Null(await GuideCatalogueUpgrade.RunAsync(store, shipped, CancellationToken.None));

        var older = shipped with { SchemaVersion = 2, Version = shipped.Version + 3, IntegritySha256 = null };
        var stored = await store.ApplyAsync(new GuidePackageUpdateRequest(older, null), CancellationToken.None);
        Assert.Equal(2, (await store.GetCurrentAsync(CancellationToken.None)).Package.SchemaVersion);

        var upgraded = await GuideCatalogueUpgrade.RunAsync(store, shipped, CancellationToken.None);

        Assert.NotNull(upgraded);
        var current = await store.GetCurrentAsync(CancellationToken.None);
        Assert.Equal(shipped.SchemaVersion, current.Package.SchemaVersion);
        Assert.Equal(stored.Package.Version + 1, current.Package.Version);
        Assert.Contains(current.Package.CustomFormats, format => format.TrashId == "d660701077794679fd59e8bdf4ce3a29" && format.Name == "AMZN");
        Assert.Contains(await store.ListAsync(CancellationToken.None), item => item.Package.Version == stored.Package.Version && !item.IsActive);
        Assert.Null(await GuideCatalogueUpgrade.RunAsync(store, shipped, CancellationToken.None));
    }

    [Fact]
    public void Every_old_catalogue_entry_moves_to_a_format_the_package_has_for_that_media_type()
    {
        var legacy = LegacyGuideCatalogue.Current;
        var byId = GuidePackageCatalog.Current.CustomFormats.ToDictionary(format => format.TrashId, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(85, legacy.Entries.Count);
        Assert.Equal(legacy.Entries.Count, legacy.Entries.Select(entry => entry.LegacyTrashId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var entry in legacy.Entries)
        {
            Assert.True(entry.Movies is not null || entry.Tv is not null, $"{entry.LegacyName} moves nowhere.");
            foreach (var (mediaType, target) in new[] { ("movies", entry.Movies), ("tv", entry.Tv) })
            {
                if (target is null)
                {
                    continue;
                }

                Assert.True(byId.TryGetValue(target, out var format), $"{entry.LegacyName} moves to {target}, which the package does not have.");
                Assert.True(GuideFormatConditions.AppliesTo(format!, mediaType), $"{entry.LegacyName} moves to {format!.Name}, which is not a {mediaType} format.");
            }
        }

        // The pairs that proved the old catalogue wrong.
        Assert.Equal("b3b3a6ac74ecbd56bcdbefa4799fb9df", legacy.Find("4e9a630db98d5391aec1368a0256e2fe")!.Movies);
        Assert.Equal("d660701077794679fd59e8bdf4ce3a29", legacy.Find("4e9a630db98d5391aec1368a0256e2fe")!.Tv);
        Assert.Equal("deluno-source-bluray", legacy.Find("bfd8eb01832d646a0a89c4deb46f8564")!.Movies);
        Assert.Null(legacy.Find("b8cd450cbfa689c0259a01d9e29ba3d6")!.Tv);
    }
}
