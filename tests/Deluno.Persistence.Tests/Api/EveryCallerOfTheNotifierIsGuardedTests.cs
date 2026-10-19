using System.Text.RegularExpressions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1365: a notifier that throws must never fail the add of a film, the recording of a grab or any other action that
/// told it. Every direct call to the outbound notification service in the product is either wrapped in
/// NotificationOutboundDispatch.GuardAsync, or sits in a try whose catch the two listed files hold.
/// </summary>
public sealed class EveryCallerOfTheNotifierIsGuardedTests
{
    // These two wrap the call in their own try/catch (the grab sender and the update orchestrator).
    private static readonly string[] OwnTryCatch = ["DownloadClientGrabService.cs", "DefaultUpdateOrchestrator.cs"];

    [Fact]
    public void No_call_to_the_notification_service_is_left_bare()
    {
        var src = Path.Combine(RepositoryRoot(), "src");
        var call = new Regex(@"_?\w*[nN]otificationService\.DispatchAsync\(", RegexOptions.Compiled);
        var bare = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || name is "OutboundNotificationService.cs" or "NotificationOutboundDispatch.cs"
                || OwnTryCatch.Contains(name))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (call.IsMatch(lines[i]) && !lines[i].Contains("GuardAsync(", StringComparison.Ordinal))
                {
                    bare.Add($"{Path.GetRelativePath(src, file)}:{i + 1}");
                }
            }
        }

        Assert.True(bare.Count == 0, "These calls can fail the action that made them: " + string.Join(", ", bare));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Deluno.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Deluno.slnx was not found above the test folder.");
    }
}
