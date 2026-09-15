using Deluno.Infrastructure.Observability;

namespace Deluno.Platform.Tests.Observability;

public sealed class SensitiveDataRedactorTests
{
    [Fact]
    public void RedactJson_masks_nested_secret_fields_without_removing_context()
    {
        var redacted = SensitiveDataRedactor.RedactJson(
            """
            {
              "name": "qBittorrent",
              "apiKey": "secret-api-key",
              "auth": {
                "password": "secret-password",
                "token": "secret-token"
              },
              "host": "localhost"
            }
            """);

        Assert.Contains("\"name\":\"qBittorrent\"", redacted);
        Assert.Contains("\"host\":\"localhost\"", redacted);
        Assert.DoesNotContain("secret-api-key", redacted);
        Assert.DoesNotContain("secret-password", redacted);
        Assert.DoesNotContain("secret-token", redacted);
        Assert.Equal(3, CountOccurrences(redacted, "[redacted]"));
    }

    [Theory]
    [InlineData("Authorization: Bearer abc123")]
    [InlineData("https://example.test?api_key=abc123")]
    [InlineData("password=hunter2")]
    public void RedactScalar_masks_obvious_secret_strings(string value)
    {
        Assert.Equal("[redacted]", SensitiveDataRedactor.RedactScalar(value));
    }

    [Theory]
    [InlineData("Search failed for http://indexer.test/api?t=movie&apikey=abc123&q=Sintel", "Search failed for http://indexer.test/api?t=movie&apikey=[redacted]&q=Sintel")]
    [InlineData("Client rejected password: hunter2 for user deluno", "Client rejected password: [redacted] for user deluno")]
    [InlineData("Sent Authorization: Bearer eyJhbGciOi.abc-def to broker", "Sent Authorization: Bearer [redacted] to broker")]
    public void RedactInline_masks_the_secret_and_keeps_the_sentence(string value, string expected)
    {
        Assert.Equal(expected, SensitiveDataRedactor.RedactInline(value));
    }

    private static int CountOccurrences(string value, string expected)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(expected, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += expected.Length;
        }

        return count;
    }
}
