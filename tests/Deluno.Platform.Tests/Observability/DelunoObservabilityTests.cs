using Deluno.Infrastructure.Observability;

namespace Deluno.Platform.Tests.Observability;

/// <summary>
/// The metric names Deluno publishes, the trace id it hands out, and every kind of JSON value the
/// redactor walks over while masking secrets.
/// </summary>
public sealed class DelunoObservabilityTests
{
    [Fact]
    public void Every_counter_is_published_under_its_own_name_on_the_deluno_meter()
    {
        var counters = new[]
        {
            DelunoObservability.JobsQueued, DelunoObservability.JobsCompleted, DelunoObservability.JobsFailed,
            DelunoObservability.JobRetries, DelunoObservability.RealtimeEventsDropped, DelunoObservability.ImportCompleted,
            DelunoObservability.ImportFailed, DelunoObservability.DecisionOutcomes, DelunoObservability.IntegrationFailures,
            DelunoObservability.IntegrationRetries, DelunoObservability.IntegrationCircuitOpened, DelunoObservability.MetadataBrokerFallbacks
        };

        Assert.Equal(counters.Length, counters.Select(counter => counter.Name).Distinct().Count());
        Assert.All(counters, counter => Assert.StartsWith("deluno.", counter.Name));
        Assert.All(counters, counter => Assert.Same(DelunoObservability.Meter, counter.Meter));
        Assert.Equal("Deluno", DelunoObservability.Meter.Name);
        Assert.Equal("X-Deluno-Trace-Id", DelunoObservability.TraceHeaderName);
    }

    [Fact]
    public void A_trace_id_is_thirty_two_hex_characters_and_never_repeats()
    {
        var first = DelunoObservability.CreateTraceId();
        var second = DelunoObservability.CreateTraceId();
        Assert.Matches("^[0-9a-f]{32}$", first);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void The_redactor_masks_secrets_by_name_at_any_depth_and_keeps_every_other_kind_of_value()
    {
        const string json = """
        {"user":"bob","ApiKey":"k1","nested":{"pass_word":"p","list":[1,2.5,true,false,null,"text",{"x-auth-token":"t","ok":1}]},"when":null}
        """;
        var redacted = SensitiveDataRedactor.RedactJson(json);
        Assert.Equal(
            """{"user":"bob","ApiKey":"[redacted]","nested":{"pass_word":"[redacted]","list":[1,2.5,true,false,null,"text",{"x-auth-token":"[redacted]","ok":1}]},"when":null}""",
            redacted);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_to_redact_gives_an_empty_string(string? json)
        => Assert.Equal(string.Empty, SensitiveDataRedactor.RedactJson(json));

    [Theory]
    [InlineData("this is not json", "this is not json")]
    [InlineData("plain password=hunter2 text", "[redacted]")]
    public void Text_that_is_not_json_is_treated_as_one_value(string text, string expected)
        => Assert.Equal(expected, SensitiveDataRedactor.RedactJson(text));

    [Theory]
    [InlineData("Authorization: Bearer abc", "[redacted]")]
    [InlineData("x?api_key=1", "[redacted]")]
    [InlineData("x?apikey=1", "[redacted]")]
    [InlineData("token=z", "[redacted]")]
    [InlineData("harmless", "harmless")]
    public void A_value_that_looks_like_a_credential_is_blanked(string value, string expected)
        => Assert.Equal(expected, SensitiveDataRedactor.RedactScalar(value));

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("GET /api?apikey=abc123&x=1 done", "GET /api?apikey=[redacted]&x=1 done")]
    [InlineData("password: hunter2, next", "password: [redacted], next")]
    [InlineData("header Authorization Bearer abc.def-123== end", "header Authorization Bearer [redacted] end")]
    [InlineData("nothing secret here", "nothing secret here")]
    public void Only_the_secret_inside_a_sentence_is_masked(string? value, string expected)
        => Assert.Equal(expected, SensitiveDataRedactor.RedactInline(value));

    [Theory]
    [InlineData("api-key", true)]
    [InlineData("X_Api_Key", true)]
    [InlineData("Cookie", true)]
    [InlineData("Authorization", true)]
    [InlineData("client_secret", true)]
    [InlineData("title", false)]
    public void A_name_is_sensitive_when_it_carries_a_secret_word_in_any_separator_style(string name, bool sensitive)
        => Assert.Equal(sensitive, SensitiveDataRedactor.IsSensitiveName(name));
}
