using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>
/// The install hook process once died after 2.5 s and wrote no log at all
/// (#968). The hook log now retries a briefly locked file, falls back to the
/// temp folder when the per-user folder cannot be written, and never throws.
/// </summary>
[Collection("HookLogSeams")]
public sealed class HookLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deluno-hooklog-" + Guid.NewGuid().ToString("N"));

    public HookLogTests()
    {
        Directory.CreateDirectory(_root);
        HookLog.RetryDelay = TimeSpan.FromMilliseconds(1);
    }

    public void Dispose()
    {
        HookLog.PrimaryFolderOverride = null;
        HookLog.FallbackFolderOverride = null;
        HookLog.RetryDelay = TimeSpan.FromMilliseconds(250);
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void Write_appends_to_the_primary_folder()
    {
        HookLog.PrimaryFolderOverride = Path.Combine(_root, "primary");
        HookLog.Write("hello");
        Assert.Contains("hello", File.ReadAllText(Path.Combine(_root, "primary", "install-hook.log")));
    }

    [Fact]
    public void Write_falls_back_when_the_primary_folder_cannot_be_created()
    {
        // A file where the folder should be makes CreateDirectory fail every time.
        var blocker = Path.Combine(_root, "blocked");
        File.WriteAllText(blocker, "not a folder");
        HookLog.PrimaryFolderOverride = Path.Combine(blocker, "logs");
        HookLog.FallbackFolderOverride = Path.Combine(_root, "fallback");

        HookLog.Write("still logged");

        var text = File.ReadAllText(Path.Combine(_root, "fallback", "Deluno-install-hook.log"));
        Assert.Contains("still logged", text);
        Assert.Contains("could not write the per-user log", text);
    }

    [Fact]
    public async Task Write_retries_a_briefly_locked_file()
    {
        var dir = Path.Combine(_root, "locked");
        Directory.CreateDirectory(dir);
        HookLog.PrimaryFolderOverride = dir;
        HookLog.RetryDelay = TimeSpan.FromMilliseconds(40);
        var path = Path.Combine(dir, "install-hook.log");
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        var release = Task.Run(async () => { await Task.Delay(100); stream.Dispose(); }); // reason: holds a real file lock for longer than the 40 ms retry delay so the retry under test happens

        HookLog.Write("after the lock");
        await release;

        Assert.Contains("after the lock", File.ReadAllText(path));
    }

    [Fact]
    public void Write_never_throws_when_nothing_is_writable()
    {
        var blocker = Path.Combine(_root, "blocked2");
        File.WriteAllText(blocker, "x");
        HookLog.PrimaryFolderOverride = Path.Combine(blocker, "a");
        HookLog.FallbackFolderOverride = Path.Combine(blocker, "b");
        HookLog.Write("nowhere to go");
    }

    [Theory]
    [InlineData("--veloapp-install", true)]
    [InlineData("--veloapp-updated", true)]
    [InlineData("--service", false)]
    public void Velopack_hook_invocations_are_recognised(string arg, bool expected)
        => Assert.Equal(expected, Program.IsVelopackHookInvocation([arg, "1.0.0"]));

    /// <summary>
    /// #968: the install and update hooks answer before Velopack loads, so a
    /// busy first boot cannot push them past Setup's 30 second limit; the
    /// uninstall hook still goes through Velopack to remove the firewall rule.
    /// </summary>
    [Theory]
    [InlineData("--veloapp-install", true)]
    [InlineData("--veloapp-updated", true)]
    [InlineData("--veloapp-uninstall", false)]
    [InlineData("--service", false)]
    public void Only_the_install_and_update_hooks_exit_before_Velopack_loads(string arg, bool expected)
        => Assert.Equal(expected, Program.IsFastExitInstallHook([arg, "1.0.0"]));
}
