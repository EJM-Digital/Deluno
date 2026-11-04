using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1475: every download health time (strike spacing, no-progress, too-slow, low-peers) takes 1 to 999 minutes and every
/// strike count (the platform threshold and each rule's limit) takes 1 to 999. The same bounds hold in the API's answer,
/// in the clamps the engine applies, and in what an older install already stored.
/// </summary>
public sealed class DownloadHealthTimesAndStrikesTakeOneTo999Tests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);

    public static TheoryData<string> Fields => new()
    {
        "downloadHealthStrikeThreshold",
        "downloadHealthDefaultStrikeWindowMinutes",
        "downloadHealthNoProgressMinutes",
        "downloadHealthMinSpeedMinutes",
        "downloadHealthMinPeersMinutes"
    };

    [Theory]
    [MemberData(nameof(Fields))]
    public async Task The_settings_API_takes_1_to_999_and_refuses_0_and_1000_in_plain_words_keeping_the_saved_value(string field)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var current = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await app.Client.GetStringAsync("/api/settings"))!;
        object With(int value)
        {
            var body = current.ToDictionary(pair => pair.Key, pair => (object)pair.Value);
            body[field] = value;
            return body;
        }

        foreach (var bad in new[] { 0, 1000 })
        {
            var refused = await app.Client.PutAsJsonAsync("/api/settings", With(bad));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            var text = await refused.Content.ReadAsStringAsync();
            Assert.Contains(field, text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("between 1 and 999", text, StringComparison.Ordinal);
        }

        Assert.Equal(current[field].GetInt32(), (await app.Client.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty(field).GetInt32());

        foreach (var good in new[] { 1, 999 })
        {
            var saved = await app.Client.PutAsJsonAsync("/api/settings", With(good));
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            Assert.Equal(good, (await app.Client.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty(field).GetInt32());
        }
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(999, 999)]
    [InlineData(1000, 999)]
    public void The_strike_spacing_the_engine_uses_is_brought_to_1_to_999_minutes(int minutes, int used)
    {
        var record = new DownloadHealthRecord("c", "q", "r", "k", "client-stalled", "warning", "e", Start, Start, 1, null, LastStrikeUtc: Start);

        Assert.Equal(Start.AddMinutes(used), record.NextStrikeUtc(minutes));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(999, 999)]
    [InlineData(1000, 999)]
    public void A_download_is_blocked_once_its_strikes_reach_the_limit_brought_to_1_to_999(int limit, int used)
    {
        DownloadHealthRecord WithStrikes(int strikes) =>
            new("c", "q", "r", "k", "client-stalled", "warning", "e", Start, Start, strikes, null, RuleStrikeLimit: limit);

        Assert.False(WithStrikes(used - 1).BlocksCandidate(Start));
        Assert.True(WithStrikes(used).BlocksCandidate(Start));
        var platform = new DownloadHealthRecord("c", "q", "r", "k", "client-stalled", "warning", "e", Start, Start, used, null);
        Assert.True(platform.BlocksCandidate(Start, limit));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(999, true)]
    [InlineData(1000, false)]
    public void A_rule_takes_1_to_999_strikes_and_1_to_999_minutes_and_refuses_the_rest_in_plain_words(int value, bool accepted)
    {
        var rule = new DownloadHealthRule(
            Id: "r", Kind: DownloadHealthRuleKinds.NoProgress, Enabled: true, StrikeLimit: 3, TimerMinutes: 30,
            AppliesTo: DownloadHealthAppliesTo.Both, MinCompletionPercent: 0, MaxCompletionPercent: 100,
            ResetStrikesOnProgress: true, ActionPreset: DownloadHealthActionPresets.JustTellMe,
            CreatedUtc: DateTimeOffset.MinValue, UpdatedUtc: DateTimeOffset.MinValue);

        var strikes = DownloadHealthRuleValidator.Validate(rule with { StrikeLimit = value }, []);
        var minutes = DownloadHealthRuleValidator.Validate(rule with { TimerMinutes = value }, []);

        Assert.Equal(accepted, strikes is null);
        Assert.Equal(accepted, minutes is null);
        if (!accepted)
        {
            Assert.Contains("between 1 and 999", strikes, StringComparison.Ordinal);
            Assert.Contains("between 1 and 999", minutes, StringComparison.Ordinal);
        }

        Assert.Equal(TimeSpan.FromMinutes(Math.Clamp(value, 1, 999)), (rule with { TimerMinutes = value }).Timer);
    }
}
