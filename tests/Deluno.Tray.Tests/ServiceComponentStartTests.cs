using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>#1452: in service mode the bundled download clients are started at service start, the same way and from the same choices as in tray mode.</summary>
public sealed partial class ComponentSetupTests
{
    [Fact]
    public async Task Service_start_starts_the_clients_Deluno_started_from_the_same_stored_choices_as_the_tray()
    {
        var registry = new FakeRegistry("nzbget", "transmission");
        var viaService = new RecordingStarter();
        var viaTray = new RecordingStarter();

        var serviceStarted = await ServiceHost.StartComponentsAsync(registry, new FakePorts(), viaService, null, CancellationToken.None);
        var trayStarted = await new ComponentAutoStarter(registry, new FakePorts(), viaTray).RunAsync(BundledComponents.Load(), CancellationToken.None);

        Assert.Equal(new[] { "nzbget", "transmission" }, serviceStarted.OrderBy(k => k).ToArray());
        Assert.Equal(trayStarted.OrderBy(k => k), serviceStarted.OrderBy(k => k));
        Assert.Equal(viaTray.Calls, viaService.Calls);
        Assert.All(viaService.Calls, call => Assert.True(call.Hidden));
    }

    [Fact]
    public async Task Service_start_leaves_alone_a_client_the_person_turned_off_or_that_already_answers()
    {
        var starter = new RecordingStarter();

        // Only nzbget was chosen: qBittorrent, Transmission and the rest stay off.
        var chosen = await ServiceHost.StartComponentsAsync(new FakeRegistry("nzbget"), new FakePorts(), starter, null, CancellationToken.None);
        Assert.Equal(new[] { "nzbget" }, chosen.ToArray());
        Assert.Single(starter.Calls);

        var answering = new RecordingStarter();
        Assert.Empty(await ServiceHost.StartComponentsAsync(new FakeRegistry("nzbget"), new FakePorts(open: 6789), answering, null, CancellationToken.None));
        Assert.Empty(answering.Calls);

        var none = new RecordingStarter();
        Assert.Empty(await ServiceHost.StartComponentsAsync(new FakeRegistry(), new FakePorts(), none, null, CancellationToken.None));
        Assert.Empty(none.Calls);
    }

    [Fact]
    public void The_service_calls_the_starter_once_it_is_up()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "apps", "windows-tray", "ServiceHost.cs")))
        {
            dir = dir.Parent;
        }

        var source = File.ReadAllText(Path.Combine(dir!.FullName, "apps", "windows-tray", "ServiceHost.cs"));
        Assert.Contains("ApplicationStarted.Register(() => _ = StartComponentsInBackgroundAsync", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"C:\Users\sam\AppData\Local\DelunoData", @"C:\Users\sam\AppData\Roaming", @"C:\Users\sam\AppData\Local")]
    [InlineData(@"C:\Users\sam\AppData\Local\Deluno\data", @"C:\Users\sam\AppData\Roaming", @"C:\Users\sam\AppData\Local")]
    public void A_service_starts_each_client_with_the_person_profile_folders_the_client_keeps_its_settings_in(string dataRoot, string roaming, string local)
    {
        var folders = ServiceProcessStarter.ProfileFoldersFor(dataRoot);

        Assert.NotNull(folders);
        Assert.Equal(roaming, folders!.Value.AppData);
        Assert.Equal(local, folders.Value.LocalAppData);
    }

    [Theory]
    [InlineData(@"D:\Media\DelunoData")]
    [InlineData(@"C:\ProgramData\Deluno\data")]
    public void A_data_folder_outside_a_profile_leaves_the_clients_environment_as_it_is(string dataRoot)
        => Assert.Null(ServiceProcessStarter.ProfileFoldersFor(dataRoot));
}
