using Deluno.Quality.Guides;

namespace Deluno.Persistence.Tests.Quality;

/// <summary>
/// GuidePackageCatalog.Validate (coverage register, engines): starting from the shipped package, which is clean, each way of breaking
/// a package is refused with its own plain message, so nothing half-formed is ever stored or activated.
/// </summary>
public sealed class TheGuidePackageValidatorRefusesEveryBrokenShapeTests
{
    private static GuidePackage Good => GuidePackageCatalog.Current;

    private static GuidePackage WithFirstReviewedFormat(GuidePackage package, Func<GuideCustomFormat, GuideCustomFormat> change)
    {
        var target = package.CustomFormats.First(format => format.SourceKind == GuidePackageCatalog.GenuineSourceKind && format.MappingStatus == GuideMappingStatus.Reviewed);
        return package with { CustomFormats = package.CustomFormats.Select(format => format.TrashId == target.TrashId ? change(format) : format).ToArray() };
    }

    private static GuidePackage WithFirstProfile(GuidePackage package, Func<GuideQualityProfile, GuideQualityProfile> change)
        => package with { QualityProfiles = package.QualityProfiles.Select((profile, index) => index == 0 ? change(profile) : profile).ToArray() };

    private static GuidePackage WithInventory(GuidePackage package, Func<GuideSourceInventory, GuideSourceInventory> change)
        => package with { SourceInventory = change(package.SourceInventory!) };

    public static TheoryData<string, Func<GuidePackage, GuidePackage>, string> Breaks() => new()
    {
        { "no id", package => package with { Id = " " }, "needs a stable id" },
        { "no name", package => package with { Name = "" }, "guide package needs a name" },
        { "version zero", package => package with { Version = 0 }, "version must be positive" },
        { "schema zero", package => package with { SchemaVersion = 0 }, "schema version must be positive" },
        { "no source name", package => package with { Source = package.Source with { SourceName = "" } }, "needs a source name" },
        { "no repository", package => package with { Source = package.Source with { RepositoryUrl = "" } }, "needs a repository URL" },
        { "no guide url", package => package with { Source = package.Source with { GuideUrl = "" } }, "needs a guide URL" },
        { "no revision", package => package with { Source = package.Source with { UpstreamRevision = "" } }, "needs an upstream revision" },
        { "no review date", package => package with { Source = package.Source with { ReviewedUtc = "" } }, "needs a review date" },
        { "no adaptation", package => package with { Source = package.Source with { Adaptation = "" } }, "needs an adaptation note" },
        { "duplicate tier", package => package with { QualityTiers = package.QualityTiers.Append(package.QualityTiers[0]).ToArray() }, "quality tier" },
        { "duplicate format", package => package with { CustomFormats = package.CustomFormats.Append(package.CustomFormats[0]).ToArray() }, "custom format" },
        { "tier with a backwards size range", package => package with { QualityTiers = package.QualityTiers.Select((tier, index) => index == 0 ? tier with { MinMbPerMin = 9, MaxMbPerMin = 1 } : tier).ToArray() }, "invalid size range" },
        { "format with no name", package => WithFirstReviewedFormat(package, format => format with { Name = "" }), "needs a name" },
        { "format with no category", package => WithFirstReviewedFormat(package, format => format with { Category = "" }), "needs a category" },
        { "format with no source kind", package => WithFirstReviewedFormat(package, format => format with { SourceKind = "" }), "needs source-kind provenance" },
        { "format with a broken regular expression", package => WithFirstReviewedFormat(package, format => format with { Patterns = ["(unclosed"] }), "invalid matching pattern" },
        { "reviewed format with no typed mapping", package => WithFirstReviewedFormat(package, format => format with { MappedTraitIds = [] }), "must declare a typed mapping" },
        { "reviewed format with an unknown trait", package => WithFirstReviewedFormat(package, format => format with { MappedTraitIds = ["not.a.real.trait"] }), "unknown typed trait" },
        { "profile with an unknown cutoff tier", package => WithFirstProfile(package, profile => profile with { CutoffQualityId = "no-such-tier" }), "unknown cutoff tier" },
        { "profile ordering an unknown tier", package => WithFirstProfile(package, profile => profile with { QualityOrder = profile.QualityOrder.Append("no-such-tier").ToArray() }), "refers to unknown tier" },
        { "profile recommending an unknown format", package => WithFirstProfile(package, profile => profile with { RecommendedFormats = profile.RecommendedFormats.Append(new GuideRecommendedFormat("no-such-format", 1)).ToArray() }), "refers to unknown custom format" },
        { "bundle naming an unknown format", package => package with { Bundles = package.Bundles.Select((bundle, index) => index == 0 ? bundle with { Includes = bundle.Includes.Append(new GuideFormatBundleEntry("no-such-format", 1)).ToArray() } : bundle).ToArray() }, "refers to unknown custom format" },
        { "genuine format with no TRaSH specifications", package => WithFirstReviewedFormat(package, format => format with { SourceMatcherClauses = [] }), "has no TRaSH specifications" },
        { "genuine format matched by Deluno patterns", package => WithFirstReviewedFormat(package, format => format with { Patterns = ["x264"] }), "must be matched by TRaSH" },
        { "Deluno extra with a TRaSH id", package => WithFirstReviewedFormat(package, format => format with { SourceKind = GuidePackageCatalog.DelunoSourceKind, Patterns = ["x264"] }), "must use a 'deluno-' id" },
        { "Deluno extra with no patterns", package => package with { CustomFormats = package.CustomFormats.Append(new GuideCustomFormat("deluno-test-extra", "Extra", "misc", "", 0, [], false, GuideMappingStatus.Advanced, [], GuidePackageCatalog.DelunoSourceKind)).ToArray() }, "needs at least one pattern" },
        { "inventory missing from a v2 package", package => package with { SourceInventory = null }, "pinned upstream source inventory" },
        { "inventory at another revision", package => WithInventory(package, inventory => inventory with { UpstreamRevision = "someone-elses-revision" }), "revision must match" },
        { "inventory format with no blob SHA", package => WithInventory(package, inventory => inventory with { CustomFormats = inventory.CustomFormats.Select((format, index) => index == 0 ? format with { SourceBlobSha = "" } : format).ToArray() }), "pinned source blob SHA" },
    };

    [Fact]
    public void The_shipped_package_is_clean_so_every_refusal_below_comes_from_the_one_thing_broken()
    {
        Assert.Empty(GuidePackageCatalog.Validate(Good));
    }

    [Theory]
    [MemberData(nameof(Breaks))]
    public void Each_broken_shape_is_refused_with_its_own_message(string what, Func<GuidePackage, GuidePackage> breakIt, string message)
    {
        var errors = GuidePackageCatalog.Validate(breakIt(Good));

        Assert.True(errors.Any(error => error.Contains(message, StringComparison.Ordinal)), $"{what}: expected '{message}' in [{string.Join(" | ", errors)}]");
    }
}
