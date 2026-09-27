using Deluno.Contracts;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// #816's glob-style matcher: patterns are checked against a file's own name
/// (never its full path), and both blacklist and whitelist modes.
/// </summary>
public sealed class ContentBlocklistMatcherTests
{
    [Theory]
    [InlineData("*.exe", "Setup.exe", true)]
    [InlineData("*.exe", "Setup.EXE", true)]
    [InlineData("*.exe", "Example.Movie.2026.mkv", false)]
    [InlineData("autorun.inf", "autorun.inf", true)]
    public void IsMatch_MatchesGlobPatternsCaseInsensitively(string pattern, string fileName, bool expected)
        => Assert.Equal(expected, ContentBlocklistMatcher.IsMatch(pattern, fileName));

    [Fact]
    public void IsMatch_ChecksOnlyTheFilesOwnNameNotItsFolder()
    {
        Assert.True(ContentBlocklistMatcher.IsMatch("*.exe", "Movie/Extras/Setup.exe"));
        Assert.True(ContentBlocklistMatcher.IsMatch("*.exe", "Movie\\Extras\\Setup.exe"));
    }

    [Fact]
    public void FindOffendingFile_Blacklist_ReturnsTheFirstMatchingFile()
    {
        var settings = new ContentBlocklistSettings(
            Enabled: true, Mode: ContentBlocklistModes.Blacklist, UseBuiltInPatterns: false,
            CustomPatterns: ["*.exe"], RemoteUrl: null, RemotePatterns: [], LastRefreshedUtc: null, UpdatedUtc: DateTimeOffset.MinValue);

        var offender = ContentBlocklistMatcher.FindOffendingFile(["Movie.mkv", "Setup.exe", "readme.txt"], settings);

        Assert.Equal("Setup.exe", offender);
    }

    [Fact]
    public void FindOffendingFile_Blacklist_NoMatch_ReturnsNull()
    {
        var settings = new ContentBlocklistSettings(
            Enabled: true, Mode: ContentBlocklistModes.Blacklist, UseBuiltInPatterns: false,
            CustomPatterns: ["*.exe"], RemoteUrl: null, RemotePatterns: [], LastRefreshedUtc: null, UpdatedUtc: DateTimeOffset.MinValue);

        Assert.Null(ContentBlocklistMatcher.FindOffendingFile(["Movie.mkv", "readme.txt"], settings));
    }

    [Fact]
    public void FindOffendingFile_Whitelist_ReturnsTheFirstFileNotOnTheAllowList()
    {
        var settings = new ContentBlocklistSettings(
            Enabled: true, Mode: ContentBlocklistModes.Whitelist, UseBuiltInPatterns: false,
            CustomPatterns: ["*.mkv", "*.srt"], RemoteUrl: null, RemotePatterns: [], LastRefreshedUtc: null, UpdatedUtc: DateTimeOffset.MinValue);

        var offender = ContentBlocklistMatcher.FindOffendingFile(["Movie.mkv", "Movie.en.srt", "Setup.exe"], settings);

        Assert.Equal("Setup.exe", offender);
    }

    [Fact]
    public void FindOffendingFile_Whitelist_EveryFileCovered_ReturnsNull()
    {
        var settings = new ContentBlocklistSettings(
            Enabled: true, Mode: ContentBlocklistModes.Whitelist, UseBuiltInPatterns: false,
            CustomPatterns: ["*.mkv", "*.srt"], RemoteUrl: null, RemotePatterns: [], LastRefreshedUtc: null, UpdatedUtc: DateTimeOffset.MinValue);

        Assert.Null(ContentBlocklistMatcher.FindOffendingFile(["Movie.mkv", "Movie.en.srt"], settings));
    }

    [Fact]
    public void FindOffendingFile_NoPatternsConfigured_NeverMatches()
    {
        var settings = new ContentBlocklistSettings(
            Enabled: true, Mode: ContentBlocklistModes.Blacklist, UseBuiltInPatterns: false,
            CustomPatterns: [], RemoteUrl: null, RemotePatterns: [], LastRefreshedUtc: null, UpdatedUtc: DateTimeOffset.MinValue);

        Assert.Null(ContentBlocklistMatcher.FindOffendingFile(["Setup.exe"], settings));
    }

    [Fact]
    public void EffectivePatterns_CombinesBuiltInCustomAndRemoteWithoutDuplicates()
    {
        var settings = new ContentBlocklistSettings(
            Enabled: true, Mode: ContentBlocklistModes.Blacklist, UseBuiltInPatterns: true,
            CustomPatterns: ["*.custom"], RemoteUrl: "https://example.test/list.txt", RemotePatterns: ["*.exe", "*.remote"],
            LastRefreshedUtc: DateTimeOffset.UtcNow, UpdatedUtc: DateTimeOffset.UtcNow);

        var patterns = settings.EffectivePatterns();

        Assert.Contains("*.exe", patterns);
        Assert.Contains("*.custom", patterns);
        Assert.Contains("*.remote", patterns);
        Assert.Single(patterns, pattern => pattern == "*.exe");
    }
}
