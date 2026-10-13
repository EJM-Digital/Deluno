using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>
/// #1260: the tray's own state files, cut short by a writer that was killed, were read as "empty" or "defaults" and then
/// written back over the real file. Each test starts from a real cut-short file.
/// </summary>
public sealed class ACutShortStateFileIsNeverWrittenOverTests : IDisposable
{
    private readonly string? _originalLocalAppData = Environment.GetEnvironmentVariable("DELUNO_TEST_LOCALAPPDATA");
    private readonly string? _originalCommonAppData = Environment.GetEnvironmentVariable("DELUNO_TEST_COMMONAPPDATA");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deluno-tray-cut-" + Guid.NewGuid().ToString("N"));

    public ACutShortStateFileIsNeverWrittenOverTests()
    {
        Environment.SetEnvironmentVariable("DELUNO_TEST_LOCALAPPDATA", Path.Combine(_root, "local"));
        Environment.SetEnvironmentVariable("DELUNO_TEST_COMMONAPPDATA", Path.Combine(_root, "common"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DELUNO_TEST_LOCALAPPDATA", _originalLocalAppData);
        Environment.SetEnvironmentVariable("DELUNO_TEST_COMMONAPPDATA", _originalCommonAppData);
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void A_cut_short_settings_file_is_never_saved_over_with_defaults()
    {
        const string cut = "{\n  \"port\": 7999,\n  \"dataRoot\": \"D:\\Media\\Del";
        var path = Path.Combine(_root, "local", "DelunoConfig", "deluno.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, cut);

        var settings = AppSettings.Load();
        try { settings.Save(); } catch (IOException) { /* saying so is allowed */ }

        Assert.Equal(cut, File.ReadAllText(path));
    }

    [Fact]
    public void Remembering_a_started_client_never_erases_the_others_when_the_file_is_cut_short()
    {
        const string cut = "[\"nzbget\",\"qbitt";
        var path = Path.Combine(_root, "started-components.json");
        File.WriteAllText(path, cut);

        try { new FileStartedComponentsRegistry(path).Add("deluge"); } catch (IOException) { }

        Assert.Equal(cut, File.ReadAllText(path));
    }
}
