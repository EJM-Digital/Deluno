using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Quality.Contracts;
using Deluno.Quality.Data;
using Deluno.Quality.Guides;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Quality;

/// <summary>
/// Moving rules made from the old guide catalogue (#1329), through the real HTTP host and a real SQLite file: apply refuses with a
/// 409 when the preview it carries no longer matches the rules or the guide, moves only the rules that were chosen, and a rule
/// edited since the preview is left as the person edited it.
/// </summary>
public sealed class GuideRuleMigrationOverHttpTests
{
    private const string OldAmazon = "4e9a630db98d5391aec1368a0256e2fe";
    private const string OldNetflix = "4e9a630db98d5391aec1368a0256e2ef";

    private static async Task<CustomFormatItem> AddOldRuleAsync(IQualityRepository quality, string name, string oldId)
    {
        var entry = LegacyGuideCatalogue.Current.Find(oldId)!;
        return await quality.CreateCustomFormatAsync(
            new CreateCustomFormatRequest(name, "movies", 0, oldId,
                JsonSerializer.Serialize(entry.LegacyPatterns.Select(pattern => new { type = "releaseTitle", value = pattern, negate = false, required = true })),
                true),
            CancellationToken.None);
    }

    [Fact]
    public async Task Apply_answers_409_when_the_preview_is_stale_moves_only_the_chosen_rules_and_leaves_a_rule_edited_since_the_preview_alone()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var quality = app.Services.GetRequiredService<IQualityRepository>();
        var amazon = await AddOldRuleAsync(quality, "Amazon Prime", OldAmazon);
        var netflix = await AddOldRuleAsync(quality, "Netflix", OldNetflix);
        var preview = await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/guides/trash/rule-migration");
        var hash = preview.GetProperty("previewHash").GetString()!;
        Assert.Equal(2, preview.GetProperty("items").GetArrayLength());

        var stale = await app.Client.PostAsJsonAsync("/api/v1/guides/trash/rule-migration/apply", new { previewHash = "not-this-preview", ruleIds = new[] { amazon.Id } });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("Your rules or the guide changed after this preview. Review the move again before applying it.", (await stale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
        Assert.Equal(OldAmazon, (await quality.ListCustomFormatsAsync(CancellationToken.None)).Single(rule => rule.Id == amazon.Id).TrashId);

        var applied = await app.Client.PostAsJsonAsync("/api/v1/guides/trash/rule-migration/apply", new { previewHash = hash, ruleIds = new[] { amazon.Id } });
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        var result = await applied.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, result.GetProperty("moved").GetInt32());
        var after = (await quality.ListCustomFormatsAsync(CancellationToken.None)).ToDictionary(rule => rule.Id);
        Assert.NotEqual(OldAmazon, after[amazon.Id].TrashId);
        Assert.Equal(OldNetflix, after[netflix.Id].TrashId);

        // The same preview no longer describes the rules, and a rule the person edits meanwhile keeps the edit.
        var again = await app.Client.PostAsJsonAsync("/api/v1/guides/trash/rule-migration/apply", new { previewHash = hash, ruleIds = new[] { netflix.Id } });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var fresh = (await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/guides/trash/rule-migration")).GetProperty("previewHash").GetString()!;
        const string Edited = "[{\"type\":\"releaseTitle\",\"value\":\"\\\\bNFLX\\\\b\",\"negate\":false,\"required\":true}]";
        Assert.NotNull(await quality.ReplaceCustomFormatDefinitionAsync(netflix.Id, netflix.Name, netflix.TrashId, Edited, netflix.Conditions, CancellationToken.None));
        var editedSince = await app.Client.PostAsJsonAsync("/api/v1/guides/trash/rule-migration/apply", new { previewHash = fresh, ruleIds = new[] { netflix.Id } });
        Assert.Equal(HttpStatusCode.Conflict, editedSince.StatusCode);
        var kept = (await quality.ListCustomFormatsAsync(CancellationToken.None)).Single(rule => rule.Id == netflix.Id);
        Assert.Equal(OldNetflix, kept.TrashId);
        Assert.Equal(Edited, kept.Conditions);
    }
}
