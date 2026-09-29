using System.Net;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Retention;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>#1066: a retention rule's tag conditions and its "tag" action tag follow a tag rename and stop a tag delete.</summary>
public sealed class RetentionTagReferenceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-29T12:00:00Z");

    private static async Task<(SqliteRetentionRepository Rules, SqliteTagReferenceStore Tags, SqlitePlatformSettingsRepository Settings)> OpenAsync(TestStorage storage)
    {
        var clock = new FixedTimeProvider(Now);
        await new PlatformSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, clock), NullLogger<PlatformSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);
        return (
            new SqliteRetentionRepository(storage.Factory, clock),
            new SqliteTagReferenceStore(storage.Factory),
            new SqlitePlatformSettingsRepository(storage.Factory, clock, TestSecretProtection.Create(storage)));
    }

    [Fact]
    public async Task Renaming_a_tag_follows_into_a_tag_is_not_protected_step_and_touches_nothing_else()
    {
        using var storage = TestStorage.Create();
        var (rules, tags, settings) = await OpenAsync(storage);
        var tag = await settings.CreateTagAsync(new CreateTagRequest("protected", null, null), CancellationToken.None);
        var steps = new[]
        {
            RetentionStep.KeepOnly("server.watchedBy:anyone:"),
            RetentionStep.KeepOnly("tag:notin:protected"),
            new RetentionStep(RetentionStep.BlockType, RetentionStep.Include, Name: "protected films",
                Steps: [RetentionStep.KeepOnly("tag:in:Protected|other"), RetentionStep.KeepOnly("server.labels:has:protected")]),
            RetentionStep.KeepOnly("genre:in:protected")
        };
        var rule = await rules.SaveRuleAsync(null, new SaveRetentionRuleRequest("Old and watched", RetentionLevels.Movie, true, steps, ["server-1"], true), CancellationToken.None);

        var users = await tags.ListAllUsersAsync(CancellationToken.None);
        Assert.Contains(users["protected"], user => user.Kind == "retentionRule" && user.Name == "Old and watched");

        await tags.RenameAsync(tag.Id, "protected", "keepers", CancellationToken.None);

        var read = (await rules.GetRuleAsync(rule!.Id, CancellationToken.None))!;
        Assert.Equal("server.watchedBy:anyone:", read.Steps[0].Condition);
        Assert.Equal("tag:notin:keepers", read.Steps[1].Condition);
        Assert.Equal("protected films", read.Steps[2].Name);
        Assert.Equal("tag:in:keepers|other", read.Steps[2].Steps![0].Condition);
        Assert.Equal("server.labels:has:protected", read.Steps[2].Steps![1].Condition);
        Assert.Equal("genre:in:protected", read.Steps[3].Condition);
        Assert.Equal((RetentionLevels.Movie, true), (read.Level, read.IsEnabled));
    }

    [Fact]
    public void A_rename_that_changes_nothing_hands_back_the_stored_text_byte_for_byte()
    {
        var json = RetentionStepList.Serialize([RetentionStep.KeepOnly("tag:notin:kids"), RetentionStep.KeepOnly("server.ratingAudience:max:5")]);

        Assert.Equal(json, RetentionStepTags.Rename(json, "protected", "keepers"));
        Assert.Equal("not json", RetentionStepTags.Rename("not json", "protected", "keepers"));
        Assert.Equal(["kids"], RetentionStepTags.Extract(json));
    }

    [Fact]
    public async Task Renaming_the_tag_a_rule_gives_on_expiry_follows_and_the_rule_counts_as_a_user()
    {
        using var storage = TestStorage.Create();
        var (rules, tags, settings) = await OpenAsync(storage);
        var tag = await settings.CreateTagAsync(new CreateTagRequest("watched-and-old", null, null), CancellationToken.None);
        var rule = await rules.SaveRuleAsync(null, new SaveRetentionRuleRequest("Tagger", RetentionLevels.Movie, true, [RetentionStep.KeepOnly("server.watchedBy:anyone:")], ["server-1"], false,
            ExpiryAction: RetentionExpiryActions.Tag, ExpiryTag: "watched-and-old"), CancellationToken.None);

        var users = await tags.ListAllUsersAsync(CancellationToken.None);
        Assert.Contains(users["watched-and-old"], user => user.Kind == "retentionRule");

        await tags.RenameAsync(tag.Id, "watched-and-old", "ready-to-go", CancellationToken.None);

        Assert.Equal("ready-to-go", (await rules.GetRuleAsync(rule!.Id, CancellationToken.None))!.ExpiryTag);
    }

    [Fact]
    public async Task A_tag_a_retention_rule_uses_cannot_be_deleted()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var tag = await app.Services.GetRequiredService<IPlatformSettingsRepository>().CreateTagAsync(new CreateTagRequest("protected", null, null), CancellationToken.None);
        var rules = app.Services.GetRequiredService<IRetentionRepository>();
        var rule = await rules.SaveRuleAsync(null, new SaveRetentionRuleRequest("Old and watched", RetentionLevels.Movie, true,
            [RetentionStep.KeepOnly("tag:notin:protected")], ["server-1"], true), CancellationToken.None);

        var refused = await app.Client.DeleteAsync($"/api/tags/{tag.Id}");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("still in use", await refused.Content.ReadAsStringAsync());

        await rules.DeleteRuleAsync(rule!.Id, CancellationToken.None);
        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/tags/{tag.Id}")).StatusCode);
    }
}
