using System.Text.Json;
using System.Text.RegularExpressions;
using Deluno.Contracts;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// The typed integration failure a caller receives is described the way it is sent: the TypeScript SDK declares exactly the
/// members the server sends (service type, id and name, retry state and the rest), and the external API document names the
/// real members and every real kind and retry state instead of the ones an earlier design used.
/// </summary>
public sealed class TheTypedFailureIsDescribedAsItIsSentTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Deluno.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static IReadOnlySet<string> SentMembers()
    {
        var failure = new IntegrationFailure("indexer", "idx-1", "Example", "search", IntegrationFailureKind.RateLimit, IntegrationRetryState.RetryScheduled, "Slow down.");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(failure, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return json.RootElement.EnumerateObject().Select(member => member.Name).ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void The_SDK_declares_exactly_the_members_the_server_sends_for_a_failure()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "sdk", "typescript", "src", "index.ts"));
        var body = Regex.Match(source, @"export interface IntegrationFailure \{(?<body>.*?)\n\}", RegexOptions.Singleline);
        Assert.True(body.Success, "The SDK no longer declares an IntegrationFailure type.");
        var declared = Regex.Matches(body.Groups["body"].Value, @"^\s*(?<name>[A-Za-z]+)\??:", RegexOptions.Multiline)
            .Select(match => match.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(SentMembers().Order(StringComparer.Ordinal), declared.Order(StringComparer.Ordinal));
        Assert.Contains("serviceType", declared);
        Assert.Contains("retryState", declared);
    }

    [Fact]
    public void The_external_API_document_names_the_real_members_kinds_and_retry_states()
    {
        var document = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "external-integration-api.md"));
        var start = document.IndexOf("### Typed integration failures", StringComparison.Ordinal);
        Assert.True(start >= 0, "The document no longer has its typed failures section.");
        var end = document.IndexOf("\n- `GET", start, StringComparison.Ordinal);
        var section = document[start..end];

        var sent = SentMembers();
        foreach (var member in new[] { "serviceType", "serviceId", "serviceName", "operation", "kind", "retryState", "httpStatus", "retryAfterUtc", "nextAction" })
        {
            Assert.Contains(member, sent);
            Assert.Contains($"`{member}`", section);
        }

        foreach (var kind in Enum.GetNames<IntegrationFailureKind>())
        {
            Assert.Contains($"`{kind}`", section);
        }

        foreach (var state in Enum.GetNames<IntegrationRetryState>())
        {
            Assert.Contains($"`{state}`", section);
        }

        // The words an earlier design used and the code never sent.
        Assert.DoesNotContain("`state` (`active`", section);
        Assert.DoesNotContain("`rate-limit`", section);
        Assert.DoesNotContain("`service`,", section);
    }
}
