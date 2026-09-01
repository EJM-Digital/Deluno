using Deluno.Api.Updates;
using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace Deluno.Persistence.Tests.Api;

public sealed class UpdatePreferencesPersistenceTests
{
    [Fact]
    public async Task Default_update_orchestrator_persists_preferences_under_the_data_root()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "deluno-update-preferences", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);

        try
        {
            var options = Options.Create(new StoragePathOptions { DataRoot = dataRoot });
            var orchestrator = new DefaultUpdateOrchestrator(options);

            var initial = await orchestrator.GetPreferencesAsync(CancellationToken.None);
            Assert.Equal(UpdateModes.NotifyOnly, initial.Mode);
            Assert.Equal("stable", initial.Channel);
            Assert.False(initial.AutoCheck);

            var saved = await orchestrator.SavePreferencesAsync(
                new UpdatePreferencesRequest(UpdateModes.DownloadBackground, "stable", true),
                CancellationToken.None);
            Assert.Equal(UpdateModes.DownloadBackground, saved.Mode);
            Assert.True(saved.AutoCheck);

            var reloaded = new DefaultUpdateOrchestrator(options);
            var loaded = await reloaded.GetPreferencesAsync(CancellationToken.None);
            Assert.Equal(saved, loaded);
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }
}
