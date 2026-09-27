using Deluno.Infrastructure.Observability;
using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Deluno.Platform.Tests.Observability;

/// <summary>
/// #866: a runtime log level change persists across restarts the same way
/// appsettings.json's own starting level does today -- by reading its own
/// small file back on the next construction, the same shape
/// DefaultUpdateOrchestrator already uses for its update preferences.
/// </summary>
public sealed class FileRuntimeLogLevelStoreTests : IDisposable
{
    private readonly string dataRoot = Path.Combine(Path.GetTempPath(), "deluno-log-level-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Defaults_to_the_configured_MinLevel_when_nothing_is_persisted_yet()
    {
        var store = CreateStore(new DelunoFileLogOptions { MinLevel = LogLevel.Debug });

        Assert.Equal(LogLevel.Debug, store.Current);
    }

    [Fact]
    public async Task A_saved_level_survives_a_fresh_instance_reading_it_back()
    {
        var first = CreateStore();
        await first.SetAsync(LogLevel.Trace, CancellationToken.None);

        var second = CreateStore();

        Assert.Equal(LogLevel.Trace, second.Current);
    }

    private FileRuntimeLogLevelStore CreateStore(DelunoFileLogOptions? options = null)
        => new(
            Options.Create(new StoragePathOptions { DataRoot = dataRoot }),
            Options.Create(options ?? new DelunoFileLogOptions()));

    public void Dispose()
    {
        if (Directory.Exists(dataRoot))
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }
}
