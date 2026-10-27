using Deluno.Contracts;

namespace Deluno.Platform.Tests;

public sealed class LogTextTests
{
    [Theory]
    [InlineData("Movie.2020\r\n2026-01-01 INFO Deluno: forged line", "Movie.2020  2026-01-01 INFO Deluno: forged line")]
    [InlineData("a\nb", "a b")]
    [InlineData("tab\there", "tab here")]
    [InlineData("a\u2028b\u2029c", "a b c")]
    [InlineData("\u001b[31mred", " [31mred")]
    public void A_value_from_outside_cannot_start_a_new_log_line(string input, string expected)
        => Assert.Equal(expected, input.ForLog());

    [Fact]
    public void Plain_text_null_and_empty_come_back_unchanged()
    {
        Assert.Equal("Sintel (2010) 1080p", "Sintel (2010) 1080p".ForLog());
        Assert.Null(((string?)null).ForLog());
        Assert.Equal(string.Empty, string.Empty.ForLog());
    }

    [Fact]
    public void A_value_that_is_not_text_is_cleaned_by_its_text_form()
    {
        Assert.Equal("x y", ((object)"x\ny").ForLog());
        Assert.Equal("42", ((object)42).ForLog());
        Assert.Null(((object?)null).ForLog());
    }
}
