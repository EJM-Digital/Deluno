using Deluno.Quality.Guides;

namespace Deluno.Persistence.Tests.Quality;

/// <summary>
/// #1417: a damaged or hand-edited guide package comes back as plain-words problems naming the format and the field,
/// never as a crash.
/// </summary>
public sealed class TheGuidePackageValidatorNeverThrowsTests
{
    private static GuidePackage Good => GuidePackageCatalog.Current;

    public static TheoryData<string, Func<GuidePackage, GuidePackage?>, string> Damage() => new()
    {
        { "no package at all", _ => null, "guide package is missing" },
        { "no source", package => package with { Source = null! }, "needs source provenance" },
        { "no source and no inventory", package => package with { Source = null!, SourceInventory = null }, "needs source provenance" },
        { "the same inventory format twice", package => package with { SourceInventory = package.SourceInventory! with { CustomFormats = package.SourceInventory.CustomFormats.Append(package.SourceInventory.CustomFormats[0]).ToArray() } }, "source custom format" },
        { "a format with no id", package => package with { CustomFormats = package.CustomFormats.Select((format, index) => index == 0 ? format with { TrashId = null! } : format).ToArray() }, "needs a stable id" },
        { "a format with no source kind and no id", package => package with { CustomFormats = package.CustomFormats.Select((format, index) => index == 0 ? format with { TrashId = "", SourceKind = null! } : format).ToArray() }, "needs a stable id" },
        { "an inventory format with no id", package => package with { SourceInventory = package.SourceInventory! with { CustomFormats = package.SourceInventory.CustomFormats.Select((format, index) => index == 0 ? format with { TrashId = null! } : format).ToArray() } }, "needs a stable TRaSH id" },
        { "no custom formats list", package => package with { CustomFormats = null! }, "" },
        { "a missing tier entry", package => package with { QualityTiers = package.QualityTiers.Append(null!).ToArray() }, "quality tier" },
        { "a missing format entry", package => package with { CustomFormats = package.CustomFormats.Append(null!).ToArray() }, "custom format" },
        { "a missing profile entry", package => package with { QualityProfiles = package.QualityProfiles.Append(null!).ToArray() }, "quality profile" },
        { "a missing bundle entry", package => package with { Bundles = package.Bundles.Append(null!).ToArray() }, "format bundle" },
    };

    [Theory]
    [MemberData(nameof(Damage))]
    public void Damage_comes_back_as_a_problem_and_never_throws(string what, Func<GuidePackage, GuidePackage?> damage, string message)
    {
        IReadOnlyList<string>? errors = null;
        var thrown = Record.Exception(() => errors = GuidePackageCatalog.Validate(damage(Good)!));

        Assert.True(thrown is null, $"{what}: Validate threw {thrown}");
        Assert.NotEmpty(errors!);
        Assert.Contains(errors!, error => error.Contains(message, StringComparison.OrdinalIgnoreCase));
    }
}
