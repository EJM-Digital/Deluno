using System.Text.Json;

namespace Deluno.Series.Tests.Services;

/// <summary>
/// Guards the payload shapes the season-search endpoint serialises.
///
/// The endpoint returned 500 on every call because one of these anonymous
/// objects carried both the route's <c>seasonNumber</c> and the episode's
/// <c>SeasonNumber</c>. System.Text.Json refuses to bind two members differing
/// only by case to one constructor parameter, and it throws when the type is
/// first configured — so the failure only ever appeared at runtime, on a
/// primary action, with a generic "An unexpected error occurred." (#285)
///
/// These tests serialise the real shapes. Re-introducing a case-colliding pair
/// fails here rather than in production.
/// </summary>
public sealed class SeasonSearchPayloadTests
{
    private const int SeasonNumber = 1;
    private const int EpisodeNumber = 3;
    private const string EpisodeId = "01a0388a12e975c48f5e3edd74b70411";

    [Fact]
    public void Per_episode_attempt_payload_serialises_without_candidates()
    {
        var json = JsonSerializer.Serialize(new
        {
            EpisodeId,
            SeasonNumber,
            EpisodeNumber
        });

        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(EpisodeId, parsed.RootElement.GetProperty("EpisodeId").GetString());
        Assert.Equal(SeasonNumber, parsed.RootElement.GetProperty("SeasonNumber").GetInt32());
        Assert.Equal(EpisodeNumber, parsed.RootElement.GetProperty("EpisodeNumber").GetInt32());
    }

    [Fact]
    public void Per_episode_attempt_payload_serialises_with_a_search_plan()
    {
        var searchPlan = new { Summary = "Best feed candidate is Example.S01E03.1080p.", Candidates = new[] { "Example.S01E03.1080p" } };

        var json = JsonSerializer.Serialize(new
        {
            EpisodeId,
            SeasonNumber,
            EpisodeNumber,
            searchPlan
        });

        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(SeasonNumber, parsed.RootElement.GetProperty("SeasonNumber").GetInt32());
        Assert.True(parsed.RootElement.TryGetProperty("searchPlan", out _));
    }

    [Fact]
    public void A_payload_carrying_the_same_name_in_two_cases_is_rejected()
    {
        // The exact shape that broke the endpoint: a route-level `seasonNumber`
        // beside an episode's `SeasonNumber`. Pinned so the failure mode stays
        // understood rather than being rediscovered from a 500.
        var seasonNumber = SeasonNumber;

        var payload = new
        {
            seasonNumber,
            EpisodeId,
            SeasonNumber,
            EpisodeNumber
        };

        var error = Assert.Throws<InvalidOperationException>(() => JsonSerializer.Serialize(payload));
        Assert.Contains("cannot both bind", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
