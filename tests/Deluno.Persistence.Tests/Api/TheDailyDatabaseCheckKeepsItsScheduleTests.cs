using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Api.SystemInfo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Platform DAT-08: the database check's own schedule. It waits after start-up for things to settle, looks hourly whether the pass is
/// due, and the claim in the job store keeps the pass to once a day however often it looks. Real databases, a real job store.
/// </summary>
public sealed class TheDailyDatabaseCheckKeepsItsScheduleTests
{
    private static DatabaseIntegrityService ServiceFor(ApplicationTestHost app, TimeSpan startDelay, TimeSpan tick) => new(
        app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>(),
        app.Services.GetRequiredService<IJobQueueRepository>(),
        app.Services.GetRequiredService<IHealthCheckRepository>(),
        TimeProvider.System,
        NullLogger<DatabaseIntegrityService>.Instance)
    { StartDelay = startDelay, Tick = tick };

    [Fact]
    public async Task The_check_waits_two_minutes_after_start_up_and_then_looks_every_hour_unless_told_otherwise()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var defaults = new DatabaseIntegrityService(
            app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>(),
            app.Services.GetRequiredService<IJobQueueRepository>(),
            app.Services.GetRequiredService<IHealthCheckRepository>(),
            TimeProvider.System,
            NullLogger<DatabaseIntegrityService>.Instance);

        Assert.Equal(TimeSpan.FromMinutes(2), defaults.StartDelay);
        Assert.Equal(TimeSpan.FromHours(1), defaults.Tick);
        Assert.Equal(TimeSpan.FromDays(1), SystemTasks.IntervalFor(SystemTasks.DatabaseIntegrity));
    }

    [Fact]
    public async Task Nothing_is_read_before_the_start_delay_and_the_pass_runs_once_however_many_times_it_looks()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var service = ServiceFor(app, startDelay: TimeSpan.FromSeconds(2), tick: TimeSpan.FromMilliseconds(150));

        await service.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(700));
            Assert.Null(DatabaseMaintenanceLog.Read(app.DataRoot).IntegrityCheckedUtc);

            var deadline = DateTime.UtcNow.AddSeconds(15);
            DateTimeOffset? first = null;
            while (DateTime.UtcNow < deadline && (first = DatabaseMaintenanceLog.Read(app.DataRoot).IntegrityCheckedUtc) is null)
            {
                await Task.Delay(100);
            }

            Assert.NotNull(first);
            // Many looks later (the tick is 150 ms), the claim has kept it to the one pass.
            await Task.Delay(TimeSpan.FromMilliseconds(1200));
            Assert.Equal(first, DatabaseMaintenanceLog.Read(app.DataRoot).IntegrityCheckedUtc);
            var state = (await app.Services.GetRequiredService<IJobQueueRepository>().ListSystemTaskStatesAsync(CancellationToken.None))
                .Single(item => item.ScheduleKey == SystemTasks.DatabaseIntegrity);
            Assert.Equal("completed", state.LastResult);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }
}
