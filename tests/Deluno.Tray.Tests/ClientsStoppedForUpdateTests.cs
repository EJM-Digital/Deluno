namespace Deluno.Tray.Tests;

/// <summary>#1532: the build that applies an update stops the clients Deluno started, so an older build's client cannot block Setup's rename.</summary>
public sealed class ClientsStoppedForUpdateTests
{
    private sealed class Registry(params string[] kinds) : IStartedComponentsRegistry
    {
        public IReadOnlyList<string> Read() => kinds;
        public void Add(string kind) { }
    }

    private sealed class Control(params string[] running) : IProcessControl
    {
        public List<string> Asked { get; } = [];
        public int StopAll(string name)
        {
            Asked.Add(name);
            return running.Contains(name, StringComparer.OrdinalIgnoreCase) ? 1 : 0;
        }
    }

    [Fact]
    public void Only_The_Clients_Deluno_Started_And_That_Are_Running_Are_Stopped()
    {
        var specs = BundledComponents.Load();
        var nzbget = specs.Single(s => s.Kind == "nzbget");
        var control = new Control("nzbget");
        var log = new List<string>();

        var stopped = ClientsStoppedForUpdate.StopStarted(new Registry("nzbget"), specs, control, log.Add);

        Assert.Equal(["nzbget"], stopped);
        Assert.Equal(["nzbget"], control.Asked);
        Assert.Contains(log, line => line.Contains(nzbget.DisplayName) && line.Contains("starts it again"));
    }

    [Fact]
    public void A_Client_The_Person_Started_Themselves_Is_Left_Alone()
    {
        var control = new Control("nzbget", "transmission-daemon");

        var stopped = ClientsStoppedForUpdate.StopStarted(new Registry("transmission"), BundledComponents.Load(), control);

        Assert.DoesNotContain("nzbget", control.Asked);
        Assert.Single(stopped);
    }

    [Fact]
    public void A_Failure_Never_Stops_The_Update()
    {
        var log = new List<string>();
        var stopped = ClientsStoppedForUpdate.StopStarted(new ThrowingRegistry(), BundledComponents.Load(), new Control(), log.Add);

        Assert.Empty(stopped);
        Assert.Single(log);
    }

    [Fact]
    public void Every_Way_An_Update_Is_Applied_Stops_The_Clients_First()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir.FullName, "apps", "windows-tray", "Program.cs")))
        {
            dir = dir.Parent!;
        }

        var text = File.ReadAllText(Path.Combine(dir.FullName, "apps", "windows-tray", "VelopackUpdateOrchestrator.cs"));
        foreach (var apply in new[] { "WaitExitThenApplyUpdates(", "ApplyUpdatesAndRestart(pending)", "ApplyUpdatesAndRestart(toApply)" })
        {
            var at = text.IndexOf(apply, StringComparison.Ordinal);
            Assert.True(at > 0, apply);
            var before = text[Math.Max(0, at - 400)..at];
            Assert.Contains("ClientsStoppedForUpdate.Stop();", before);
        }
    }

    private sealed class ThrowingRegistry : IStartedComponentsRegistry
    {
        public IReadOnlyList<string> Read() => throw new IOException("busy");
        public void Add(string kind) { }
    }
}
