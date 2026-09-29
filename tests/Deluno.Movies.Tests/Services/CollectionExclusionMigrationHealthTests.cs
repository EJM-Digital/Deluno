using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Movies.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Movies.Tests.Services;

/// <summary>#1080: the collection-exclusion move must not stop the host, but a failure must show on Health.</summary>
public sealed class CollectionExclusionMigrationHealthTests
{
    private sealed class RecordingReporter : IBackgroundProblemReporter
    {
        public List<(string Key, string Title, string Reason)> Reported { get; } = [];
        public List<string> Cleared { get; } = [];

        public void Report(string key, string title, string message, string reason) => Reported.Add((key, title, reason));

        public void Clear(string key) => Cleared.Add(key);
    }

    [Fact]
    public async Task A_failed_move_does_not_stop_start_up_and_is_reported_to_Health()
    {
        var factory = new Mock<IDelunoDatabaseConnectionFactory>();
        factory
            .Setup(f => f.OpenConnectionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("database is locked"));
        var reporter = new RecordingReporter();
        var service = new CollectionExclusionMigrationHostedService(
            factory.Object,
            Mock.Of<IUnifiedExclusionRepository>(),
            NullLogger<CollectionExclusionMigrationHostedService>.Instance,
            reporter);

        await service.StartAsync(CancellationToken.None);

        var reported = Assert.Single(reporter.Reported);
        Assert.Equal("startup:collection-exclusions", reported.Key);
        Assert.Equal("database is locked", reported.Reason);
        Assert.Empty(reporter.Cleared);
    }
}
