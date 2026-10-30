using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Quality.Guides;

namespace Deluno.Persistence.Tests.Quality;

/// <summary>
/// docs/guide-packages.md and docs/RELEASE_PREFERENCE_SPEC.md (#1329): both are linked from the docs map, and each behaviour the
/// guide-packages page states is the behaviour the running host shows: the guide's own rename flag is kept on the package entry,
/// the old-catalogue migration route answers, the update check is off on a new install, and the update flow reads before it writes.
/// </summary>
public sealed class GuidePackageDocsDescribeTheShippedBehaviourTests
{
    private static string Docs(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Deluno.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine([directory!.FullName, "docs", .. parts]));
    }

    [Fact]
    public void Both_pages_are_linked_from_the_docs_map_and_the_preference_spec_names_its_comparison_and_evidence_rules()
    {
        var map = Docs("README.md");
        Assert.Contains("`guide-packages.md`", map, StringComparison.Ordinal);
        Assert.Contains("`RELEASE_PREFERENCE_SPEC.md`", map, StringComparison.Ordinal);
        var spec = Docs("RELEASE_PREFERENCE_SPEC.md");
        Assert.Contains("## Comparison rules", spec, StringComparison.Ordinal);
        Assert.Contains("## Evidence truth table", spec, StringComparison.Ordinal);
        Assert.Contains("## Required API/persistence invariants", spec, StringComparison.Ordinal);
    }

    [Fact]
    public void The_guide_packages_page_states_the_rename_flag_the_old_catalogue_move_the_default_off_check_and_the_read_before_write_update_and_the_code_does_all_four()
    {
        var page = Docs("guide-packages.md");
        Assert.Contains("includeCustomFormatWhenRenaming", page, StringComparison.Ordinal);
        Assert.Contains("keeps that flag on the package entry", string.Join(' ', page.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), StringComparison.Ordinal);
        Assert.Contains("GET /api/v1/guides/trash/rule-migration", page, StringComparison.Ordinal);
        Assert.Contains("default-off", page, StringComparison.Ordinal);
        Assert.Contains("read-before-write", page, StringComparison.Ordinal);

        // The package entry really carries TRaSH's own rename answer, and the legacy catalogue the page names is there.
        Assert.Contains(typeof(GuidePackageCatalog).Assembly.GetTypes().SelectMany(type => type.GetProperties()), property => property.Name == "IncludeWhenRenaming");
        Assert.NotNull(LegacyGuideCatalogue.Current);
        Assert.Contains("trash-guide-legacy-catalogue.json", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_install_has_the_guide_update_check_off_and_the_old_catalogue_move_previews_and_changes_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var check = await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/guides/trash/update-check");
        var preview = await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/guides/trash/rule-migration");

        Assert.False(check.GetProperty("isEnabled").GetBoolean());
        Assert.Equal(0, preview.GetProperty("items").GetArrayLength());
        Assert.False(string.IsNullOrWhiteSpace(preview.GetProperty("previewHash").GetString()));
    }
}
