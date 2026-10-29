using System.Reflection;
using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Worker.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Worker;

/// <summary>
/// The regular check of every connection, on the real job store (coverage register, engines): a test pass that fails or is stopped by
/// shutdown is recorded failed or cancelled and the loop carries on, and two looks inside the same minute test the connections once
/// because the second finds the pass already claimed. Refs #1296.
/// </summary>
public sealed class TheRegularConnectionCheckRecordsItsPassTests
{
    public class Tester : DispatchProxy
    {
        public int Calls { get; private set; }

        public Func<Task>? OnCall { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name != nameof(IConnectionHealthTester.TestStaleAsync))
            {
                throw new NotSupportedException(targetMethod.Name);
            }

            Calls++;
            return OnCall is null ? Task.FromResult<IReadOnlyList<ConnectionCheckedEvent>>([]) : Run(OnCall);
        }

        private static async Task<IReadOnlyList<ConnectionCheckedEvent>> Run(Func<Task> onCall)
        {
            await onCall();
            return [];
        }
    }

    public class Settings : DispatchProxy
    {
        public PlatformSettingsSnapshot? Snapshot { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod!.Name == nameof(IPlatformSettingsRepository.GetAsync)
                ? Task.FromResult(Snapshot!)
                : throw new NotSupportedException(targetMethod.Name);
    }

    private sealed class Rig(ApplicationTestHost app, Tester tester, RegularConnectionCheckService service) : IAsyncDisposable
    {
        public Tester Tester => tester;

        public RegularConnectionCheckService Service => service;

        public ApplicationTestHost App => app;

        public async Task<SystemTaskState> StateAsync()
            => (await app.Services.GetRequiredService<IJobQueueRepository>().ListSystemTaskStatesAsync(CancellationToken.None))
                .Single(state => state.ScheduleKey == SystemTasks.ConnectionsCheck);

        public ValueTask DisposeAsync() => app.DisposeAsync();
    }

    private static async Task<Rig> StartAsync()
    {
        var app = await ApplicationTestHost.StartAsync();
        var tester = DispatchProxy.Create<IConnectionHealthTester, Tester>();
        var services = new ServiceCollection().AddSingleton(tester).BuildServiceProvider();
        var service = new RegularConnectionCheckService(
            NullLogger<RegularConnectionCheckService>.Instance,
            services.GetRequiredService<IServiceScopeFactory>(),
            app.Services.GetRequiredService<IJobQueueRepository>(),
            TimeProvider.System);
        return new Rig(app, (Tester)(object)tester, service);
    }

    [Fact]
    public async Task A_test_pass_that_throws_is_recorded_failed_without_stopping_the_loop_and_a_shutdown_is_recorded_cancelled()
    {
        await using var rig = await StartAsync();
        rig.Tester.OnCall = () => Task.FromException(new InvalidOperationException("The test could not run."));

        await rig.Service.RunOnceAsync(CancellationToken.None);

        var failed = await rig.StateAsync();
        Assert.Equal("failed", failed.LastResult);
        Assert.NotNull(failed.NextRunUtc);

        using var stopping = new CancellationTokenSource();
        rig.Tester.OnCall = () =>
        {
            stopping.Cancel();
            return Task.FromException(new OperationCanceledException(stopping.Token));
        };
        // The next interval: make the pass due again.
        await ApiScenes.ExecuteAsync(rig.App, "jobs", "UPDATE worker_schedule_state SET last_run_utc = '2000-01-01T00:00:00.0000000+00:00' WHERE schedule_key = '" + SystemTasks.ConnectionsCheck + "';");

        await rig.Service.RunOnceAsync(stopping.Token);

        Assert.Equal("cancelled", (await rig.StateAsync()).LastResult);
        Assert.Equal(2, rig.Tester.Calls);
    }

    [Fact]
    public async Task Two_looks_inside_the_same_minute_test_the_connections_once()
    {
        await using var rig = await StartAsync();

        await rig.Service.RunOnceAsync(CancellationToken.None);
        await rig.Service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, rig.Tester.Calls);
        Assert.Equal("completed", (await rig.StateAsync()).LastResult);
    }
}
