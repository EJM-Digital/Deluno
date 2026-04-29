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
