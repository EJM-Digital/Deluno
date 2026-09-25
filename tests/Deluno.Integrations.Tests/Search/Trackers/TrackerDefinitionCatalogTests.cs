using Deluno.Integrations.Search.Trackers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Deluno.Integrations.Tests.Search.Trackers;

/// <summary>
/// #606: every definition Deluno ships loads, and the engine can run all but
/// the few that need a person (a captcha) or something it does not have.
/// </summary>
public sealed class TrackerDefinitionCatalogTests(ITestOutputHelper output)
{
    [Fact]
    public void Every_built_in_definition_loads()
    {
        var failures = new List<string>();
        var count = 0;
        foreach (var (id, yaml) in TrackerDefinitionCatalog.BuiltIn())
        {
            count++;
            try
            {
                Assert.False(string.IsNullOrWhiteSpace(TrackerDefinition.Parse(yaml).Id));
            }
            catch (Exception exception)
            {
                failures.Add($"{id}: {exception.Message}");
            }
        }

        // The starter set (#606): a film, a TV and general trackers, each
        // written from scratch from the site's own API or pages.
        Assert.Equal(9, count);
        Assert.Empty(failures);
    }

    [Fact]
    public void The_engine_can_run_every_built_in_definition()
    {
        var catalog = new TrackerDefinitionCatalog(overrideDirectory: null, NullLogger<TrackerDefinitionCatalog>.Instance);
        var all = catalog.List();
        var unsupported = all.Where(summary => summary.Unsupported is not null).ToArray();
        foreach (var summary in unsupported)
        {
            output.WriteLine($"{summary.Id}: {summary.Unsupported}");
        }

        // None of the starter set needs a captcha or anything else the engine lacks.
        Assert.Empty(unsupported);
        Assert.NotNull(catalog.Find("yts"));
        Assert.NotNull(catalog.Find("eztv"));
        Assert.NotNull(catalog.Find("thepiratebay"));
        Assert.NotNull(catalog.Find("limetorrents"));
        Assert.NotNull(catalog.Find("1337x"));
        Assert.NotNull(catalog.Find("nyaasi"));
        Assert.NotNull(catalog.Find("rutor"));
        Assert.NotNull(catalog.Find("internetarchive"));
        Assert.NotNull(catalog.Find("torrentdownloads"));
    }

    /// <summary>
    /// #606: the starter set is written from scratch, not copied from an
    /// indexer manager's Cardigann definitions — Deluno carries no licence
    /// file that would allow bundling GPL content. A definition that still
    /// carried a Jackett issue-number comment, or named Prowlarr or Jackett
    /// as its source, would be exactly that mistake happening again.
    /// </summary>
    [Fact]
    public void No_built_in_definition_carries_Jackett_or_Prowlarr_provenance()
    {
        var offenders = new List<string>();
        foreach (var (id, yaml) in TrackerDefinitionCatalog.BuiltIn())
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(yaml, @"#\s*#\d{3,6}", System.Text.RegularExpressions.RegexOptions.None)
                || yaml.Contains("Jackett", StringComparison.OrdinalIgnoreCase)
                || yaml.Contains("Prowlarr", StringComparison.OrdinalIgnoreCase)
                || yaml.Contains("Cardigann", StringComparison.OrdinalIgnoreCase))
            {
                offenders.Add(id);
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void A_newer_copy_in_the_data_folder_replaces_the_built_in_one()
    {
        var directory = Directory.CreateTempSubdirectory("deluno-trackers-");
        try
        {
            var yaml = TrackerDefinitionCatalog.BuiltIn().Single(pair => pair.Id == "yts").Yaml
                .Replace("name: YTS", "name: YTS (updated)", StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(directory.FullName, "yts.yml"), yaml);

            var catalog = new TrackerDefinitionCatalog(directory.FullName, NullLogger<TrackerDefinitionCatalog>.Instance);

            Assert.Equal("YTS (updated)", catalog.Find("yts")!.Name);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
