using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Host;

/// <summary>
/// docs/backup-restore-runbook.md tells a person which screens to open, which buttons to press and what to check afterwards. Each of
/// those claims is held to the app as it is now: the screens exist as routes, the buttons exist with those words, the health
/// addresses answer on the pipeline Deluno ships, and the files named as evidence are really there.
/// </summary>
public sealed class TheBackupRestoreRunbookIsTrueTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Deluno.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([RepositoryRoot(), .. parts]));

    private static readonly string Runbook = Read("docs", "backup-restore-runbook.md");

    private static string[] Quoted() => Regex.Matches(Runbook, "`([^`]+)`").Select(match => match.Groups[1].Value).Distinct().ToArray();

    [Fact]
    public void Every_System_screen_the_runbook_sends_a_person_to_is_a_real_screen()
    {
        var router = Read("apps", "web", "src", "router.tsx");
        var screens = Quoted().Where(item => item.StartsWith("System > ", StringComparison.Ordinal)).ToArray();

        Assert.Contains("System > Backups", screens);
        Assert.Contains("System > Updates", screens);
        foreach (var screen in screens)
        {
            var child = screen["System > ".Length..].ToLowerInvariant();
            Assert.True(Regex.IsMatch(router, "path: \"system\"[\\s\\S]*?path: \"" + child + "\""), $"{screen} is not a route under /system ({child})");
        }
    }

    [Fact]
    public void The_buttons_and_links_the_runbook_names_exist_on_the_Backups_screen_with_those_words()
    {
        var screen = Read("apps", "web", "src", "routes", "system-screen.tsx");

        Assert.Contains("Create backup", Quoted());
        Assert.Contains("            Create backup", screen); // step 2 "Click `Create backup`"
        Assert.Contains("Download latest", screen);          // step 4 "Download the newest backup"
        Assert.Contains("void previewRestore(file)", screen); // "Upload backup archive and run restore preview": choosing the file previews it
        Assert.Contains("Restore backup", screen);            // "Run restore"
    }

    [Fact]
    public async Task The_health_addresses_the_runbook_checks_after_a_restore_answer_on_the_shipped_pipeline()
    {
        Assert.Contains("/health", Quoted());
        Assert.Contains("/api/health/ready", Quoted());
        await using var host = await SharedPipelineHost.StartAsync();

        using var health = await host.Client.GetAsync("/health");
        using var ready = await host.Client.GetAsync("/api/health/ready");

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(JsonValueKind.Array, (await ready.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("checks").ValueKind);
    }

    [Fact]
    public void The_pre_restore_suffix_and_the_evidence_file_the_runbook_names_are_real()
    {
        Assert.Contains(".pre-restore", Quoted());
        Assert.Contains(".pre-restore", Read("src", "Deluno.Infrastructure", "Storage", "StagedRestore.cs"));
        var evidence = Quoted().Single(item => item.StartsWith("tests/", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(RepositoryRoot(), evidence.Replace('/', Path.DirectorySeparatorChar))), $"{evidence} is missing");
    }
}
