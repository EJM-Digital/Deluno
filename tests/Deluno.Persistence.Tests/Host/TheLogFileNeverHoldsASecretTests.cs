using Deluno.Infrastructure.Observability;
using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Deluno.Persistence.Tests.Host;

/// <summary>
/// Platform DAT-14: what reaches the log file on disk has its secrets masked, by the name of the value and inside the message text.
/// The line is written by the real file logger to a real temp folder and read back from the file.
/// </summary>
public sealed class TheLogFileNeverHoldsASecretTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "deluno-log-secrets", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dataRoot, recursive: true); } catch (IOException) { }
    }

    private string LogText()
        => string.Join('\n', Directory.GetFiles(Path.Combine(_dataRoot, "logs"), "deluno-*.jsonl").Select(File.ReadAllText));

    [Fact]
    public void A_password_a_token_and_a_key_in_a_message_or_a_named_value_never_reach_the_log_file_but_the_rest_of_the_line_does()
    {
        using (var provider = new DelunoFileLoggerProvider(
                   Options.Create(new StoragePathOptions { DataRoot = _dataRoot }),
                   Options.Create(new DelunoFileLogOptions()),
                   TimeProvider.System))
        using (var factory = LoggerFactory.Create(builder => builder.AddProvider(provider)))
        {
            var logger = factory.CreateLogger("Deluno.Integrations.Test");
            logger.LogWarning(
                "Client {Client} refused http://client.test/api?apikey={Leak}&username=alice&password={Password}; header Authorization: Bearer {Token}",
                "qBittorrent",
                "leaked-api-key-123",
                "hunter2-password",
                "bearer-secret-456");
            logger.LogInformation("Saved the connection {Name} with {Secret} and {ApiKey} and {Password}", "Home client", "named-secret-aaa", "named-key-bbb", "named-password-ccc");
        }

        var text = LogText();
        foreach (var secret in new[] { "leaked-api-key-123", "hunter2-password", "bearer-secret-456", "named-secret-aaa", "named-key-bbb", "named-password-ccc" })
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }

        Assert.Contains("qBittorrent", text, StringComparison.Ordinal);
        Assert.Contains("Home client", text, StringComparison.Ordinal);
        Assert.Contains("[redacted]", text, StringComparison.Ordinal);
    }
}
