using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1329: the read routes whose answer on a fresh install IS the behaviour, the content Deluno ships with and the defaults it starts on: the
/// stock failure rules, strike rules, scheduled tasks, subtitle languages, example media plans, retention fields, the shelves' filter and sort
/// choices, the notification and backup defaults, the guide package it carries and the state of everything not yet set up.
/// </summary>
public sealed class WhatAFreshInstallShipsIsReadBackTests
{
    private static async Task<JsonElement> ReadAsync(ApplicationTestHost app, string path) => await app.Client.GetFromJsonAsync<JsonElement>(path);

    private static JsonElement Item(JsonElement list, string property, string value)
        => Assert.Single(list.EnumerateArray(), item => item.GetProperty(property).GetString() == value);

    [Fact]
    public async Task The_stock_content_is_what_the_screens_start_with()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var banner = await ReadAsync(app, "/api");
        Assert.Equal("Deluno", banner.GetProperty("name").GetString());
        Assert.Equal("media-automation", banner.GetProperty("kind").GetString());

        var failureRules = await ReadAsync(app, "/api/failure-rules");
        var noVideo = Item(failureRules, "reasonCode", "noVideoStream");
        Assert.Equal("badFile", noVideo.GetProperty("category").GetString());
        Assert.Equal("Immediately", noVideo.GetProperty("decision").GetString());
        Assert.All(failureRules.EnumerateArray(), rule => Assert.False(rule.GetProperty("isOverridden").GetBoolean()));

        var strikes = await ReadAsync(app, "/api/download-health/rules");
        var noProgress = Item(strikes, "id", "legacy-no-progress");
        Assert.Equal("no-progress", noProgress.GetProperty("kind").GetString());
        Assert.False(noProgress.GetProperty("enabled").GetBoolean());
        Assert.Equal(3, noProgress.GetProperty("strikeLimit").GetInt32());
        Assert.Equal(30, noProgress.GetProperty("timerMinutes").GetInt32());

        var languages = await ReadAsync(app, "/api/subtitle-languages");
        Assert.Equal("en", languages[0].GetProperty("code").GetString());
        Assert.Equal("English", languages[0].GetProperty("name").GetString());
        Assert.Equal("Spanish", Item(languages, "code", "es").GetProperty("name").GetString());

        var tasks = await ReadAsync(app, "/api/system/tasks");
        Assert.Equal("Watch indexers for new releases", Item(tasks, "key", "rss.sync").GetProperty("name").GetString());

        var scenarios = await ReadAsync(app, "/api/media-plan-scenarios");
        Assert.Equal("Family 1080p", Item(scenarios, "id", "family-1080p").GetProperty("name").GetString());

        var fields = await ReadAsync(app, "/api/retention/fields");
        Assert.Equal("Watched by", Item(fields, "id", "server.watchedBy").GetProperty("label").GetString());
    }

    [Fact]
    public async Task The_shelves_start_with_their_filter_and_sort_choices()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        foreach (var (path, kind) in new[] { ("/api/movies/controls", "movies"), ("/api/series/controls", "shows") })
        {
            var controls = await ReadAsync(app, path);
            Assert.Equal(kind, controls.GetProperty("kind").GetString());
            Assert.Equal("Title", Item(controls.GetProperty("filterFields"), "id", "title").GetProperty("label").GetString());
            Assert.True(controls.GetProperty("sortFields").GetArrayLength() > 0);
        }
    }

    [Fact]
    public async Task Everything_not_set_up_yet_reads_as_its_default()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        Assert.Equal(500, (await ReadAsync(app, "/api/activity/display-settings")).GetProperty("pageSize").GetInt32());

        var draft = await ReadAsync(app, "/api/setup/draft");
        Assert.Equal("simple", draft.GetProperty("mode").GetString());
        Assert.Equal("both", draft.GetProperty("mediaIntent").GetString());
        var progress = await ReadAsync(app, "/api/setup/progress");
        Assert.False(progress.GetProperty("isCompleted").GetBoolean());
        Assert.False(progress.GetProperty("isSkipped").GetBoolean());
        Assert.Equal(0, progress.GetProperty("lastCompletedStep").GetInt32());

        var backups = await ReadAsync(app, "/api/backups/settings");
        Assert.False(backups.GetProperty("enabled").GetBoolean());
        Assert.Equal("daily", backups.GetProperty("frequency").GetString());
        Assert.Equal("03:00", backups.GetProperty("timeOfDay").GetString());
        Assert.Equal(7, backups.GetProperty("retentionCount").GetInt32());

        var blocklist = await ReadAsync(app, "/api/download-health/content-blocklist");
        Assert.False(blocklist.GetProperty("enabled").GetBoolean());
        Assert.Equal("blacklist", blocklist.GetProperty("mode").GetString());
        Assert.True(blocklist.GetProperty("useBuiltInPatterns").GetBoolean());

        var model = await ReadAsync(app, "/api/ranking-model/status");
        Assert.False(model.GetProperty("enabled").GetBoolean());
        Assert.Equal("offline", model.GetProperty("mode").GetString());
        Assert.Equal(20, model.GetProperty("maxAbsoluteBoost").GetInt32());

        var trakt = await ReadAsync(app, "/api/trakt/status");
        Assert.False(trakt.GetProperty("clientConfigured").GetBoolean());
        Assert.False(trakt.GetProperty("connected").GetBoolean());

        var notifications = await ReadAsync(app, "/api/notification-preferences");
        Assert.True(notifications.GetProperty("importCompletedEnabled").GetBoolean());
        Assert.True(notifications.GetProperty("importFailedEnabled").GetBoolean());

        var update = await ReadAsync(app, "/api/updates/status");
        Assert.Equal("stable", update.GetProperty("channel").GetString());
        Assert.Equal("notify-only", update.GetProperty("behaviorMode").GetString());
        Assert.False(update.GetProperty("updateAvailable").GetBoolean());

        var guideCheck = await ReadAsync(app, "/api/v1/guides/trash/update-check");
        Assert.False(guideCheck.GetProperty("isEnabled").GetBoolean());
        Assert.Equal("disabled", guideCheck.GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_guide_package_it_carries_is_listed_active_and_its_inventory_names_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var versions = await ReadAsync(app, "/api/v1/guides/trash/versions");
        var active = Assert.Single(versions.EnumerateArray(), item => item.GetProperty("isActive").GetBoolean());
        Assert.Equal("trash-guides", active.GetProperty("package").GetProperty("id").GetString());
        var inventory = await ReadAsync(app, "/api/v1/guides/trash/inventory");
        Assert.Equal("trash-guides", inventory.GetProperty("packageId").GetString());
        Assert.Equal(active.GetProperty("package").GetProperty("version").GetInt32(), inventory.GetProperty("packageVersion").GetInt32());
        Assert.True(inventory.GetProperty("totalItemCount").GetInt32() > 0);
        Assert.Equal(0, inventory.GetProperty("unaccounted").GetArrayLength());
    }
}
