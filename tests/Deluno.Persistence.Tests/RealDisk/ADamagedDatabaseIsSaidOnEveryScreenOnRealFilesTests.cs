using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Infrastructure.Observability;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Data;
using Deluno.Api.SystemInfo;
using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// Platform DAT-07: the daily database check on a real database file with a stretch of a page overwritten. It names the database,
/// writes a warning to the log, records the pass as failed with the plain reason, and what it found is on the System databases card. (Health does not show it: see the skipped test.)
/// </summary>
public sealed class ADamagedDatabaseIsSaidOnEveryScreenOnRealFilesTests
{
    [Fact]
    public async Task A_damaged_database_is_named_logged_recorded_as_failed_and_shown_on_the_system_databases_card()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using (var stream = new FileStream(factory.GetDatabasePath("movies"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            stream.Position = 4096;
            await stream.WriteAsync(Enumerable.Repeat((byte)0xFF, 3000).ToArray());
        }

        Assert.True(File.Exists(factory.GetDatabasePath("movies")), "the movies database is a real file on disk");
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        await jobs.TryClaimScheduledPassAsync(SystemTasks.DatabaseIntegrity, TimeSpan.FromDays(1), CancellationToken.None);
        var problems = await app.Services.GetRequiredService<DatabaseIntegrityService>().RunOnceAsync(CancellationToken.None);

        Assert.Equal(["Movies"], problems);
        // The log (and the System log card, which reads it): a warning that names the database.
        var warning = app.Services.GetRequiredService<DelunoLogBuffer>().Recent("warning", 50)
            .FirstOrDefault(entry => entry.Message.Contains("did not pass its daily check", StringComparison.Ordinal));
        Assert.NotNull(warning);
        Assert.Contains("movies", warning!.Message, StringComparison.OrdinalIgnoreCase);
        // The recorded pass.
        var state = (await jobs.ListSystemTaskStatesAsync(CancellationToken.None)).Single(item => item.ScheduleKey == SystemTasks.DatabaseIntegrity);
        Assert.Equal("failed", state.LastResult);
        Assert.Equal("Problem found in: Movies.", state.LastError);
        // System: the databases card.
        var card = await app.Client.GetFromJsonAsync<JsonElement>("/api/system/databases");
        Assert.False(card.GetProperty("integrityOk").GetBoolean());
        Assert.Equal("Problem found in: Movies.", card.GetProperty("integrityNote").GetString());
    }

    // DEFECT: no health check reads the integrity note, so a database that failed its daily check is on System and in the log but not on Health.
    // GET /api/system/health answers {"checks":[],"summary":{"problemCount":0,...}} after a pass recorded as failed (src/Deluno.Worker/HealthChecks/Checks has no database check).
    [Fact(Skip = "Defect: a database that fails its daily integrity check is not shown on Health (no health check reads the integrity note); the register row says it is.")]
    public async Task A_damaged_database_is_shown_in_health_with_its_name()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var factory = app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using (var stream = new FileStream(factory.GetDatabasePath("movies"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            stream.Position = 4096;
            await stream.WriteAsync(Enumerable.Repeat((byte)0xFF, 3000).ToArray());
        }

        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        await jobs.TryClaimScheduledPassAsync(SystemTasks.DatabaseIntegrity, TimeSpan.FromDays(1), CancellationToken.None);
        await app.Services.GetRequiredService<DatabaseIntegrityService>().RunOnceAsync(CancellationToken.None);

        var health = await app.Client.GetStringAsync("/api/system/health");
        Assert.Contains("Movies", health, StringComparison.Ordinal);
    }
}
