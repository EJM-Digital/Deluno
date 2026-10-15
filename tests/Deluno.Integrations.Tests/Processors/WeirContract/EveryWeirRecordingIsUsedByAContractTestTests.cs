using System.Text.RegularExpressions;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.Processors.WeirContract;

/// <summary>
/// #1239: the guard that keeps the Deluno-Weir contract from drifting silently. Every file under
/// tests/fixtures/recorded/weir/&lt;version&gt;/ must be named by a contract test (so a new recording that no test replays
/// fails the build), and must have its row in docs/deluno-weir-contract.md (so the document cannot fall behind the tests).
/// A recording of an older Weir version is named with its version as well, because the helpers load the newest one by default.
/// </summary>
public sealed class EveryWeirRecordingIsUsedByAContractTestTests
{
    [Fact]
    public void Every_Weir_recording_is_named_by_a_contract_test()
    {
        var sources = ContractTestSources();
        var unused = new List<string>();
        var newest = RecordedReplies.LatestVersion("weir");
        foreach (var version in RecordedReplies.Versions("weir"))
        {
            foreach (var state in RecordedReplies.States("weir", version))
            {
                var named = sources.Contains($"\"{state}\"", StringComparison.Ordinal);
                if (version != newest)
                {
                    named = named && sources.Contains($"\"{version}\"", StringComparison.Ordinal);
                }

                if (!named)
                {
                    unused.Add($"weir/{version}/{state}.json");
                }
            }
        }

        Assert.True(
            unused.Count == 0,
            "No contract test in tests/Deluno.Integrations.Tests/Processors/WeirContract replays: " + string.Join(", ", unused)
            + ". Add a test that replays each through the real client and says what Deluno does with it, and a row in docs/deluno-weir-contract.md.");
    }

    [Fact]
    public void Every_Weir_recording_has_a_row_in_the_contract_document()
    {
        var document = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "deluno-weir-contract.md"));
        var missing = RecordedReplies.Versions("weir")
            .SelectMany(version => RecordedReplies.States("weir", version).Select(state => (version, state)))
            .Where(recording => !document.Contains($"`{recording.state}`", StringComparison.Ordinal))
            .Select(recording => $"weir/{recording.version}/{recording.state}.json")
            .ToArray();

        Assert.True(missing.Length == 0, "docs/deluno-weir-contract.md has no row for: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_state_a_contract_test_replays_is_a_recording_that_exists()
    {
        // A state named in a test and not recorded fails when the test runs; this names every one at once.
        var states = Regex.Matches(ContractTestSources(), @"(?:Rec|ReplayOf)\(""([a-z0-9-]+)""|InlineData\(""([a-z0-9-]+)""")
            .SelectMany(match => new[] { match.Groups[1].Value, match.Groups[2].Value })
            .Where(state => state.Length > 0 && state.Contains('-'))
            .Distinct()
            .ToArray();
        // A state is found where the helpers find it: in the newest version that was recorded giving it (an older version's remains
        // readable until a newer recording of the same state replaces it).
        var recorded = RecordedReplies.Versions("weir").SelectMany(version => RecordedReplies.States("weir", version))
            .Concat(RecordedReplies.Versions("deluno").SelectMany(version => RecordedReplies.States("deluno", version)))
            .ToArray();
        var neither = states.Where(state => !recorded.Contains(state)).ToArray();

        Assert.True(neither.Length == 0, "Named in a contract test but not recorded: " + string.Join(", ", neither));
    }


    [Fact]
    public void Every_Deluno_recording_is_named_by_a_test_and_has_a_row_in_the_contract_document()
    {
        // Deluno's own side of the talk (what it sends Weir, what Weir reports to it) was written down by the recording proxy.
        var sources = ContractTestSources() + "\n" + PersistenceTestSources();
        var document = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "deluno-weir-contract.md"));
        var problems = RecordedReplies.Versions("deluno")
            .SelectMany(version => RecordedReplies.States("deluno", version).Select(state => (version, state)))
            .Where(recording => !sources.Contains($"\"{recording.state}\"", StringComparison.Ordinal) || !document.Contains($"`{recording.state}`", StringComparison.Ordinal))
            .Select(recording => $"deluno/{recording.version}/{recording.state}.json")
            .ToArray();

        Assert.True(problems.Length == 0, "Not named by a test (here, in WeirRealReportsTests.cs or in DelunoRepliesToWeirTests.cs under tests/Deluno.Persistence.Tests/Api) or missing from docs/deluno-weir-contract.md: " + string.Join(", ", problems));
    }

    private static string PersistenceTestSources()
    {
        // The tests that replay Deluno's side of the talk: Weir's reports to it, and Deluno's replies to Weir's calls.
        var folder = Path.Combine(RepositoryRoot(), "tests", "Deluno.Persistence.Tests", "Api");
        return string.Join("\n", new[] { "WeirRealReportsTests.cs", "DelunoRepliesToWeirTests.cs" }
            .Select(name => Path.Combine(folder, name))
            .Where(File.Exists)
            .Select(File.ReadAllText));
    }

    private static string ContractTestSources()
    {
        var folder = Path.Combine(RepositoryRoot(), "tests", "Deluno.Integrations.Tests", "Processors", "WeirContract");
        return string.Join(
            "\n",
            Directory.GetFiles(folder, "*.cs")
                .Where(file => !Path.GetFileName(file).StartsWith("EveryWeirRecording", StringComparison.Ordinal))
                .Select(File.ReadAllText));
    }

    private static string RepositoryRoot()
    {
        var recorded = new DirectoryInfo(RecordedReplies.Root);
        return recorded.Parent!.Parent!.Parent!.FullName;
    }
}
