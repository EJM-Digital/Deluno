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

        Assert.True(count > 500, $"Only {count} definitions are built in.");
        Assert.Empty(failures);
    }

    [Fact]
    public void The_engine_can_run_nearly_every_definition()
    {
        var catalog = new TrackerDefinitionCatalog(overrideDirectory: null, NullLogger<TrackerDefinitionCatalog>.Instance);
        var all = catalog.List();
        var unsupported = all.Where(summary => summary.Unsupported is not null).ToArray();
        foreach (var summary in unsupported)
        {
            output.WriteLine($"{summary.Id}: {summary.Unsupported}");
        }

        // The captcha sign-ins need a person at the keyboard; nothing else may fail.
        Assert.All(unsupported, summary => Assert.Contains("captcha", summary.Unsupported!, StringComparison.OrdinalIgnoreCase));
        Assert.True(unsupported.Length <= all.Count / 10, $"{unsupported.Length} of {all.Count} definitions cannot run.");
        Assert.NotNull(catalog.Find("yts"));
        Assert.NotNull(catalog.Find("1337x"));
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
