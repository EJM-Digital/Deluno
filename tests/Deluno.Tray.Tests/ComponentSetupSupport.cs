using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Deluno.Api.Network;
using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>
/// What the installer seeds and how it starts each client. Every fact about a
/// vendor's package in these tests was read from the real package or its
/// source; the comments on the tests name where. Split by issue across files.
/// </summary>
public sealed partial class ComponentSetupTests
{
    static ComponentSetupTests()
    {
        // Keep the started-clients file out of the real profile.
        Environment.SetEnvironmentVariable("DELUNO_TEST_LOCALAPPDATA",
            Path.Combine(Path.GetTempPath(), "deluno-setup-tests-" + Guid.NewGuid().ToString("N")));
    }

    private static BundledComponentSpec Load(string kind) => BundledComponents.Load().Single(s => s.Kind == kind);

    // ---- helpers -------------------------------------------------------------

    private static async Task<ComponentInstallResult> InstallWith(
        BundledComponentSpec real,
        FakeConfigWriter writer,
        RecordingCredentialStore store,
        string? template = null,
        RecordingStarter? starter = null,
        FakeRegistry? registry = null,
        BundledComponentCredentials? chosenLogin = null)
    {
        var download = WriteTempFile(new byte[20], ".exe");
        var service = new ComponentInstallService(
            new FakePrograms(), new FakePorts(), new FixedDownloader(download), new AlwaysSigned(), new ElevatedRecordingRunner(),
            starter ?? new RecordingStarter(), new ReadyChecker(), writer, templates: new FixedTemplate(template),
            credentialStore: store, startedComponents: registry ?? new FakeRegistry(), installedRecord: new Deluno.Api.Network.NoInstalledComponentRecord(), credentialCaptureWait: TimeSpan.Zero);

        var spec = real with
        {
            Url = "https://example.invalid/installer.exe", GithubRepo = null, DirectoryListingUrl = null,
            Sha256 = null, MustBeSigned = true, ExpectedPublisher = "Test Publisher", MinimumBytes = 1, Prerequisite = null, ReadinessTimeoutSeconds = 1
        };
        return await service.InstallAsync(spec, CancellationToken.None, null, null, chosenLogin);
    }

    private static string WriteTempFile(byte[] content, string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), "deluno-setup-" + Guid.NewGuid().ToString("N") + extension);
        File.WriteAllBytes(path, content);
        return path;
    }

    private sealed class FakePrograms : IInstalledProgramsReader
    {
        public IReadOnlyList<string> ReadDisplayNames() => [];
    }

    private sealed class FakePorts(int? open = null) : IPortProbe
    {
        public Task<bool> IsOpenAsync(string host, int port, CancellationToken cancellationToken) => Task.FromResult(open == port);
    }

    private sealed class FixedDownloader(string path) : IComponentDownloader
    {
        public Task<string?> DownloadAsync(string url, string suggestedFileName, CancellationToken cancellationToken) => Task.FromResult<string?>(path);
    }

    private sealed class AlwaysSigned : IAuthenticodeVerifier
    {
        public bool IsSigned(string path) => true;
    }

    private sealed class ElevatedRecordingRunner : IProcessRunner
    {
        public int ElevatedCalls { get; private set; }
        public int PlainCalls { get; private set; }
        public string? LastPath { get; private set; }
        public string? LastArguments { get; private set; }

        public Task<int?> RunAsync(string path, string? arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            PlainCalls++;
            LastPath = path;
            LastArguments = arguments;
            return Task.FromResult<int?>(0);
        }

        public Task<int?> RunElevatedAsync(string path, string? arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            ElevatedCalls++;
            LastPath = path;
            LastArguments = arguments;
            return Task.FromResult<int?>(0);
        }
    }

    private sealed class RecordingStarter : IProcessStarter
    {
        public List<(string Path, string? Args, bool Hidden)> Calls { get; } = [];
        public bool LastHidden => Calls.Count > 0 && Calls[^1].Hidden;

        public IStartedProcess? Start(string path, string? arguments) => Start(path, arguments, false);

        public IStartedProcess? Start(string path, string? arguments, bool hidden)
        {
            Calls.Add((path, arguments, hidden));
            return new RunningProcess();
        }
    }

    private sealed class RunningProcess : IStartedProcess
    {
        public bool HasExited => false;
        public int ExitCode => 0;
    }

    private sealed class ReadyChecker : IReadinessChecker
    {
        public Task<bool> CheckAsync(string url, string field, string expectedValue, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class FakeConfigWriter(bool result) : IConfigFileWriter
    {
        public string? Content { get; private set; }

        public Task<bool> WriteIfAbsentAsync(string path, string content, CancellationToken cancellationToken)
        {
            Content = content;
            return Task.FromResult(result);
        }
    }

    private sealed class FixedTemplate(string? text) : ITextFileReader
    {
        public string? ReadAllText(string path) => text;
    }

    private sealed class RecordingCredentialStore : IBundledComponentCredentialStore
    {
        public Dictionary<string, BundledComponentCredentials> Saved { get; } = new();
        public bool Has(string kind) => Saved.ContainsKey(kind);
        public BundledComponentCredentials? TryGet(string kind) => Saved.GetValueOrDefault(kind);
        public void Save(string kind, BundledComponentCredentials credentials) => Saved[kind] = credentials;
        public void Remove(string kind) => Saved.Remove(kind);
    }

    private sealed class FakeRegistry(params string[] kinds) : IStartedComponentsRegistry
    {
        public List<string> Kinds { get; } = kinds.ToList();
        public IReadOnlyList<string> Read() => Kinds;
        public void Add(string kind) => Kinds.Add(kind);
    }
}
