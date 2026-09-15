using Deluno.Contracts;

namespace Deluno.Platform.Tests.Intake;

/// <summary>
/// The Activity line a selected existing-library import writes. Seen on the
/// physical rig: two selected imports left Activity with no rows at all.
/// </summary>
public sealed class ExistingLibraryImportActivityTests
{
    [Fact]
    public void Everything_new_is_called_added()
    {
        var text = ExistingLibraryImportActivity.DescribeSelected("Movies", Result(requested: 2, imported: 2));

        Assert.Equal("Deluno added 2 titles you selected from Movies.", text);
    }

    [Fact]
    public void A_repeat_import_does_not_claim_to_add_anything()
    {
        var text = ExistingLibraryImportActivity.DescribeSelected("TV", Result(requested: 1, imported: 0));

        Assert.Equal(
            "Deluno checked the 1 item you selected from TV; nothing new was added, because it was already in Deluno or set aside.",
            text);
    }

    [Fact]
    public void A_partial_import_names_both_counts_and_the_items_to_review()
    {
        var text = ExistingLibraryImportActivity.DescribeSelected(
            null,
            Result(requested: 3, imported: 1, issues: [new ExistingLibraryImportIssue("/library/Unknown", "ambiguousEpisode", "No episode numbers.")]));

        Assert.Equal(
            "Deluno added 1 of the 3 items you selected from this library; 2 were already in Deluno or set aside. 1 needs a look in Review existing files.",
            text);
    }

    private static ExistingLibraryImportResult Result(int requested, int imported, IReadOnlyList<ExistingLibraryImportIssue>? issues = null)
        => new(requested, imported, requested - imported, issues ?? []);
}
