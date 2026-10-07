using Deluno.Integrations.Search.Trackers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Integrations.Tests.Search.Trackers;

/// <summary>#1137: tracker settings are shown in Deluno's words, not those of the apps the definitions were written for.</summary>
public sealed class TrackerSettingWordingTests
{
    private static readonly TrackerDefinitionCatalog Catalog = new(overrideDirectory: null, NullLogger<TrackerDefinitionCatalog>.Instance);

    [Fact]
    public void No_shown_setting_names_an_app_deluno_replaces()
    {
        var offenders = Catalog.List()
            .SelectMany(site => site.Settings.Select(setting => (site, setting)))
            .Where(pair => TrackerSettingWording.NamesAnotherApp(pair.setting.Label)
                || TrackerSettingWording.NamesAnotherApp(pair.setting.Type == "info" ? pair.setting.Default : null)
                || pair.setting.Options.Any(option => TrackerSettingWording.NamesAnotherApp(option.Value)))
            .Select(pair => $"{pair.site.Id}: {pair.setting.Name} = {pair.setting.Label}")
            .ToArray();

        Assert.True(offenders.Length == 0, string.Join(Environment.NewLine, offenders.Take(20)));
    }

    [Fact]
    public void The_compatibility_switches_are_not_asked_and_are_always_on()
    {
        var nyaa = Catalog.List().Single(site => site.Id == "nyaasi");

        Assert.DoesNotContain(nyaa.Settings, setting => setting.Name is "sonarr_compatibility" or "radarr_compatibility");
        Assert.Contains("sonarr_compatibility", TrackerSettingWording.AlwaysOn);
        Assert.Contains("radarr_compatibility", TrackerSettingWording.AlwaysOn);
    }

    [Fact]
    public void Sort_and_order_choices_read_as_words()
    {
        var site = Catalog.List().Single(item => item.Id == "1337x");
        var sort = site.Settings.Single(setting => setting.Name == "sort");
        var order = site.Settings.Single(setting => setting.Name == "type");

        Assert.Equal("Sort results by", sort.Label);
        Assert.Contains(sort.Options, option => option.Value == "Date added");
        Assert.Equal("Order of results", order.Label);
        Assert.Contains(order.Options, option => option.Value == "Highest or newest first");
        Assert.DoesNotContain(order.Options, option => option.Value is "desc" or "asc");
    }

    [Fact]
    public void The_1337x_download_note_is_in_deluno_words()
    {
        var note = Catalog.List().Single(item => item.Id == "1337x").Settings.Single(setting => setting.Name == "info_download");

        Assert.StartsWith("This site's .torrent link fails", note.Default);
        Assert.DoesNotContain("we suggest", note.Default);
    }
}
