using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>#1523: the golden-VM suite found the service account's password in install-hook.log, in the tray's start-up line.</summary>
[Collection("HookLogSeams")]
public sealed class ACommandLineLogNeverHoldsASecretTests
{
    [Fact]
    public void The_password_after_the_flag_is_masked()
    {
        var line = CommandLineRedaction.ForLog([@"--install-service", "--username", @".\goldensvc", "--password", "Secret-123"]);
        Assert.DoesNotContain("Secret-123", line);
        Assert.Equal(@"--install-service --username .\goldensvc --password ***", line);
    }

    [Theory]
    [InlineData("--password=Secret-123")]
    [InlineData("--PASSWORD=Secret-123")]
    [InlineData("--token=Secret-123")]
    [InlineData("--api-key=Secret-123")]
    public void The_equals_form_is_masked(string argument)
    {
        var line = CommandLineRedaction.ForLog(["--install-service", argument]);
        Assert.DoesNotContain("Secret-123", line);
        Assert.EndsWith("=***", line);
    }

    [Theory]
    [InlineData("--token")]
    [InlineData("--api-key")]
    [InlineData("--apikey")]
    [InlineData("--secret")]
    public void Other_secret_flags_are_masked(string flag)
        => Assert.DoesNotContain("Secret-123", CommandLineRedaction.ForLog([flag, "Secret-123"]));

    [Fact]
    public void Arguments_without_a_secret_are_kept_and_a_trailing_flag_is_safe()
    {
        Assert.Equal("--after-restart 3368", CommandLineRedaction.ForLog(["--after-restart", "3368"]));
        Assert.Equal("--install-service --password", CommandLineRedaction.ForLog(["--install-service", "--password"]));
        Assert.Equal("", CommandLineRedaction.ForLog([]));
    }

    [Fact]
    public void What_Main_logs_for_its_arguments_goes_through_the_helper()
    {
        // No other place may log raw arguments: Program.cs is the only one that wrote them, and it uses the helper.
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "apps", "windows-tray", "Program.cs"));
        Assert.Contains("CommandLineRedaction.ForLog(args)", source);
        Assert.DoesNotContain("string.Join(' ', args)", source);
    }

    [Fact]
    public void The_start_up_line_written_to_the_hook_log_has_no_password()
    {
        var folder = Path.Combine(Path.GetTempPath(), "deluno-redact-" + Guid.NewGuid().ToString("N"));
        try
        {
            HookLog.PrimaryFolderOverride = folder;
            HookLog.Write($"Main starting: args=[{CommandLineRedaction.ForLog(["--install-service", "--username", "svc", "--password", "Secret-123"])}].");
            var text = string.Concat(Directory.GetFiles(folder).Select(File.ReadAllText));
            Assert.DoesNotContain("Secret-123", text);
            Assert.Contains("--password ***", text);
        }
        finally
        {
            HookLog.PrimaryFolderOverride = null;
            try { Directory.Delete(folder, true); } catch { }
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Deluno.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }
}
