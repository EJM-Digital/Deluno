using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// The library free-space warn/pause thresholds (#909) had a setter
/// (<c>SqlitePlatformSettingsRepository</c> quietly clamps pause down to
/// warn on save) but no validation telling a person why their save produced
/// a different number than the one they typed. This checks the plain-words
/// refusal added to <c>ValidateSettings</c>, and that a valid pair still
/// saves both numbers.
/// </summary>
public sealed class LibraryFreeSpaceThresholdSettingTests
{
    [Fact]
    public async Task A_pause_threshold_above_warn_is_refused_with_a_plain_words_reason()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PatchAsJsonAsync("/api/settings", new
        {
            libraryFreeSpaceWarnPercent = 10,
            libraryFreeSpacePausePercent = 20
        });

        Assert.False(response.IsSuccessStatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var errors = body.GetProperty("errors");
        Assert.True(errors.TryGetProperty("libraryFreeSpacePausePercent", out var message));
        Assert.Contains("at or below the warn threshold", message[0].GetString());
    }

    [Fact]
    public async Task A_pause_threshold_at_or_below_warn_saves_both_numbers()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PatchAsJsonAsync("/api/settings", new
        {
            libraryFreeSpaceWarnPercent = 15,
            libraryFreeSpacePausePercent = 8
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        var settings = await app.Client.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.Equal(15, settings.GetProperty("libraryFreeSpaceWarnPercent").GetInt32());
        Assert.Equal(8, settings.GetProperty("libraryFreeSpacePausePercent").GetInt32());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public async Task A_warn_threshold_outside_1_to_50_is_refused(int warnPercent)
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PatchAsJsonAsync("/api/settings", new { libraryFreeSpaceWarnPercent = warnPercent });

        Assert.False(response.IsSuccessStatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("errors").TryGetProperty("libraryFreeSpaceWarnPercent", out _));
    }
}
