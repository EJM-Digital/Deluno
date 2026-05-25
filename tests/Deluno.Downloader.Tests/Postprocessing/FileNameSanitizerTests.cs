using Deluno.Downloader.Postprocessing;

namespace Deluno.Downloader.Tests.Postprocessing;

public class FileNameSanitizerTests
{
    [Theory]
    [InlineData("normal.mkv", "normal.mkv")]
    [InlineData("with:colon.mkv", "with_colon.mkv")]
    [InlineData("question?.mkv", "question_.mkv")]
    [InlineData("multi*chars<>.mkv", "multi_chars__.mkv")]
    [InlineData("pipe|name.mkv", "pipe_name.mkv")]
    [InlineData("trailing dots... ", "trailing dots")]
    public void Sanitize_replaces_invalid_chars(string input, string expected)
    {
        Assert.Equal(expected, FileNameSanitizer.Sanitize(input));
    }

    [Fact]
    public void Sanitize_handles_empty_after_stripping()
    {
        Assert.Equal("_", FileNameSanitizer.Sanitize(". "));
        Assert.Equal("_", FileNameSanitizer.Sanitize("...   "));
    }

    [Fact]
    public void Sanitize_strips_control_chars()
    {
        var input = "name\x01\x02.mkv";
        var output = FileNameSanitizer.Sanitize(input);
        Assert.DoesNotContain('\x01', output);
        Assert.DoesNotContain('\x02', output);
        Assert.Contains("name", output);
        Assert.EndsWith(".mkv", output);
    }
}
