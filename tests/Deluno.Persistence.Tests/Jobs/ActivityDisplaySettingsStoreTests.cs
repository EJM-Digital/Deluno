using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Data;
using Microsoft.Extensions.Options;

namespace Deluno.Persistence.Tests.Jobs;

/// <summary>
/// The Activity timeline's page-size setting (#871): a user-configurable
/// replacement for the fixed 500-row maxVisible cap, persisted the same way
/// the runtime log level is.
/// </summary>
public sealed class ActivityDisplaySettingsStoreTests
{
    [Fact]
    public async Task Defaults_to_500_and_persists_a_changed_value_across_instances()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "deluno-activity-display", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        try
        {
            var options = Options.Create(new StoragePathOptions { DataRoot = dataRoot });
            var store = new FileActivityDisplaySettingsStore(options);
            Assert.Equal(500, FileActivityDisplaySettingsStore.DefaultPageSize);
            Assert.Equal(FileActivityDisplaySettingsStore.DefaultPageSize, store.PageSize);

            await store.SetPageSizeAsync(50, CancellationToken.None);
            Assert.Equal(50, store.PageSize);

            var reloaded = new FileActivityDisplaySettingsStore(options);
            Assert.Equal(50, reloaded.PageSize);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(1, 25)]
    [InlineData(100000, 2000)]
    public async Task Clamps_out_of_range_values_to_the_allowed_bounds(int requested, int expected)
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "deluno-activity-display", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        try
        {
            var store = new FileActivityDisplaySettingsStore(Options.Create(new StoragePathOptions { DataRoot = dataRoot }));
            await store.SetPageSizeAsync(requested, CancellationToken.None);
            Assert.Equal(expected, store.PageSize);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }
}
