using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>Low-severity recycle bin rows (#1329), on the whole application with its own data folder.</summary>
public sealed class RecycleBinSettingsLowGapTests
{
    [Fact]
    public async Task A_fresh_install_keeps_deleted_files_for_thirty_days_up_to_102400_MB_and_a_saved_choice_is_read_back()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var fresh = await app.Client.GetFromJsonAsync<JsonElement>("/api/recycle-bin/settings");
        Assert.Equal(30, fresh.GetProperty("retentionDays").GetInt32());
        Assert.Equal(102400, fresh.GetProperty("maxSizeMb").GetInt64());
        Assert.Equal(0, (await app.Client.GetFromJsonAsync<JsonElement>("/api/recycle-bin")).GetArrayLength());

        var saved = await app.Client.PutAsJsonAsync("/api/recycle-bin/settings", new { retentionDays = 7, maxSizeMb = 2048 });
        Assert.True(saved.IsSuccessStatusCode);

        var after = await app.Client.GetFromJsonAsync<JsonElement>("/api/recycle-bin/settings");
        Assert.Equal(7, after.GetProperty("retentionDays").GetInt32());
        Assert.Equal(2048, after.GetProperty("maxSizeMb").GetInt64());
    }
}
