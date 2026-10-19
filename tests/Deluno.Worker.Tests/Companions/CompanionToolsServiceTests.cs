using Deluno.Companions;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Deluno.Worker.Tests.Companions;

/// <summary>
/// The "get the tools you need" runner (#506), with tools that install nothing: the order the tools run in, what each shows while it waits and
/// runs, that one failing never stops the next, that a second ask while one is running is refused, and that every change is pushed to an open screen.
/// </summary>
public sealed class CompanionToolsServiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "deluno-companion-service-tests", Guid.NewGuid().ToString("N"));
    private readonly List<string> _ran = [];
    private readonly List<(string Entity, string Id)> _pushed = [];
    private readonly Dictionary<string, Func<ICompanionProgress, Task>> _behaviour = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _connections = new(StringComparer.OrdinalIgnoreCase);
    private TaskCompletionSource? _gate;

    private sealed class FakeTool(string id, CompanionToolsServiceTests owner) : ICompanionTool
    {
        public string Id => id;
        public string Name => id switch { "sabnzbd" => "SABnzbd", "qbittorrent" => "qBittorrent", _ => "Weir" };
        public string Description => $"{Name} description";
        public bool IsInstalled() => false;
        public Task<string?> ConnectionNameAsync(CancellationToken cancellationToken) => Task.FromResult(owner._connections.GetValueOrDefault(id));

        public async Task RunAsync(CompanionRunContext context, CancellationToken cancellationToken)
        {
            lock (owner._ran) owner._ran.Add(id);
            if (owner._gate is { } gate) await gate.Task;
            if (owner._behaviour.TryGetValue(id, out var behaviour)) await behaviour(context.Progress);
            else context.Progress.Report("installing", $"Installing {Name}.");
        }
    }

    private CompanionToolsService Build()
    {
        var services = new ServiceCollection();
        foreach (var id in CompanionToolIds.All) services.AddScoped<ICompanionTool>(_ => new FakeTool(id, this));
        var provider = services.BuildServiceProvider();
        var realtime = new Mock<IRealtimeEventPublisher>();
        realtime.Setup(publisher => publisher.PublishEntityChangedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((entity, id, _) => { lock (_pushed) _pushed.Add((entity, id)); })
            .Returns(Task.CompletedTask);
        return new CompanionToolsService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new StoragePathOptions { DataRoot = _folder }),
            TimeProvider.System,
            NullLogger<CompanionToolsService>.Instance,
            realtime.Object);
    }

    private static async Task<IReadOnlyList<CompanionToolStatus>> FinishedAsync(CompanionToolsService service)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var tools = await service.ListAsync(CancellationToken.None);
            if (!tools.Any(tool => tool.Job?.Status is "queued" or "running")) return tools;
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The tools did not finish.");
            await Task.Delay(10); // reason: polling a background runner until it reaches a state, with a 15 second ceiling
        }
    }

    [Fact]
    public async Task Tools_run_one_after_another_in_a_fixed_order_whatever_order_they_were_asked_for_in()
    {
        var service = Build();

        Assert.True(service.TryStart(["weir", "qbittorrent", "sabnzbd"], "http://127.0.0.1:5099"));
        var tools = await FinishedAsync(service);

        Assert.Equal(["sabnzbd", "qbittorrent", "weir"], _ran);
        Assert.All(tools, tool => Assert.Equal("done", tool.Job!.Status));
    }

    [Fact]
    public async Task Each_tool_asked_for_shows_Waiting_its_turn_while_another_is_running_and_the_others_show_nothing()
    {
        _gate = new TaskCompletionSource();
        var service = Build();

        Assert.True(service.TryStart(["sabnzbd", "weir"], "http://127.0.0.1:5099"));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        IReadOnlyList<CompanionToolStatus> tools;
        do
        {
            tools = await service.ListAsync(CancellationToken.None);
            await Task.Delay(10); // reason: polling a background runner until it reaches a state, with a 15 second ceiling
        }
        while (tools.First(tool => tool.Id == "sabnzbd").Job?.Status != "running" && DateTime.UtcNow < deadline);

        var sab = tools.First(tool => tool.Id == "sabnzbd").Job!;
        Assert.Equal("running", sab.Status);
        Assert.Equal("Starting SABnzbd.", sab.Message);
        var weir = tools.First(tool => tool.Id == "weir").Job!;
        Assert.Equal(("queued", "Waiting its turn."), (weir.Status, weir.Message));
        Assert.Null(tools.First(tool => tool.Id == "qbittorrent").Job);

        _gate.SetResult();
        await FinishedAsync(service);
    }

    [Fact]
    public async Task A_second_ask_while_a_setup_is_running_is_refused_and_is_accepted_again_once_it_has_finished()
    {
        _gate = new TaskCompletionSource();
        var service = Build();

        Assert.True(service.TryStart(["sabnzbd"], "http://127.0.0.1:5099"));
        Assert.False(service.TryStart(["qbittorrent"], "http://127.0.0.1:5099"));

        _gate.SetResult();
        await FinishedAsync(service);
        Assert.True(service.TryStart(["qbittorrent"], "http://127.0.0.1:5099"));
        await FinishedAsync(service);
        Assert.Equal(["sabnzbd", "qbittorrent"], _ran);
    }

    [Fact]
    public async Task A_tool_that_fails_shows_why_in_the_words_it_gave_and_the_next_tool_still_runs()
    {
        _behaviour["sabnzbd"] = progress =>
        {
            progress.Report("download", "Downloading SABnzbd.");
            throw new CompanionStepException("The download was not the build Deluno expects.");
        };
        var service = Build();

        service.TryStart(["sabnzbd", "qbittorrent"], "http://127.0.0.1:5099");
        var tools = await FinishedAsync(service);

        var failed = tools.First(tool => tool.Id == "sabnzbd").Job!;
        Assert.Equal("failed", failed.Status);
        Assert.Equal("download", failed.Step);
        Assert.Equal("The download was not the build Deluno expects.", failed.Message);
        Assert.Equal("done", tools.First(tool => tool.Id == "qbittorrent").Job!.Status);
        Assert.Equal(["sabnzbd", "qbittorrent"], _ran);
    }

    [Fact]
    public async Task An_unexpected_failure_says_so_in_plain_words_and_never_shows_the_exception_text()
    {
        _behaviour["weir"] = _ => throw new InvalidOperationException(@"C:\Users\someone\secret-path\token=abc123");
        var service = Build();

        service.TryStart(["weir"], "http://127.0.0.1:5099");
        var tools = await FinishedAsync(service);

        var job = tools.First(tool => tool.Id == "weir").Job!;
        Assert.Equal("failed", job.Status);
        Assert.Equal("Setting up Weir stopped unexpectedly. Deluno's log has the details.", job.Message);
        Assert.DoesNotContain("abc123", job.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tool_that_is_not_one_of_the_three_is_ignored()
    {
        var service = Build();

        service.TryStart(["not-a-tool", "qbittorrent"], "http://127.0.0.1:5099");
        await FinishedAsync(service);

        Assert.Equal(["qbittorrent"], _ran);
    }

    [Fact]
    public async Task Every_change_of_a_job_is_pushed_to_an_open_screen_queued_each_step_and_the_end()
    {
        _behaviour["sabnzbd"] = progress => { progress.Report("download", "Downloading."); progress.Report("install", "Installing."); return Task.CompletedTask; };
        _behaviour["weir"] = _ => throw new CompanionStepException("No.");
        var service = Build();

        service.TryStart(["sabnzbd", "weir"], "http://127.0.0.1:5099");
        await FinishedAsync(service);

        var sab = _pushed.Where(item => item.Entity == "CompanionJob" && item.Id == "sabnzbd").ToArray();
        var weir = _pushed.Where(item => item.Entity == "CompanionJob" && item.Id == "weir").ToArray();
        // queued, starting, download, install, done
        Assert.Equal(5, sab.Length);
        // queued, starting, failed
        Assert.Equal(3, weir.Length);
        Assert.DoesNotContain(_pushed, item => item.Entity != "CompanionJob");
    }

    [Fact]
    public async Task The_list_shows_connected_for_a_tool_with_a_connection_and_not_installed_for_one_with_none()
    {
        _connections["qbittorrent"] = "My qBittorrent";
        var service = Build();

        var tools = await service.ListAsync(CancellationToken.None);

        Assert.Equal(["sabnzbd", "qbittorrent", "weir"], tools.Select(tool => tool.Id));
        var qb = tools.First(tool => tool.Id == "qbittorrent");
        Assert.Equal("connected", qb.State);
        Assert.Equal("My qBittorrent", qb.ConnectionName);
        Assert.Equal("not-installed", tools.First(tool => tool.Id == "sabnzbd").State);
        Assert.All(tools, tool => Assert.Null(tool.Job));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }
}
