using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>#1370: a Windows service (LocalSystem) has its own profile, so without help it opened on an empty data folder.</summary>
public sealed class ServiceKeepsTheDataFolderTests : IDisposable
{
    private readonly string? _local = Environment.GetEnvironmentVariable("DELUNO_TEST_LOCALAPPDATA");
    private readonly string? _common = Environment.GetEnvironmentVariable("DELUNO_TEST_COMMONAPPDATA");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deluno-tray-tests", Guid.NewGuid().ToString("N"));

    private string Person => Path.Combine(_root, "person");
    private string ServiceProfile => Path.Combine(_root, "systemprofile");

    private void AsPerson() => Environment.SetEnvironmentVariable("DELUNO_TEST_LOCALAPPDATA", Person);
    private void AsService() => Environment.SetEnvironmentVariable("DELUNO_TEST_LOCALAPPDATA", ServiceProfile);

    public ServiceKeepsTheDataFolderTests()
        => Environment.SetEnvironmentVariable("DELUNO_TEST_COMMONAPPDATA", Path.Combine(_root, "common"));

    private string SetUpPersonWithData()
    {
        AsPerson();
        var data = Path.Combine(_root, "my media data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "platform.db"), "libraries");
        Assert.Null(AppSettings.Update(s => { s.DataRoot = data; s.Port = 7890; }));
        return data;
    }

    [Fact]
    public void Without_sharing_the_service_opens_on_an_empty_default_folder()
    {
        var data = SetUpPersonWithData();
        AsService();
        AppSettings.ForgetLastRead();

        var seen = AppSettings.Load();
        Assert.NotEqual(data, seen.DataRoot); // the defect, as the golden VM saw it
    }

    [Fact]
    public void After_sharing_the_service_uses_the_same_data_folder_and_port()
    {
        var data = SetUpPersonWithData();
        Assert.Null(AppSettings.ShareWithService());

        AsService();
        AppSettings.ForgetLastRead();
        var seen = AppSettings.Load();

        Assert.Equal(data, seen.DataRoot);
        Assert.Equal(7890, seen.Port);
        Assert.True(File.Exists(Path.Combine(seen.DataRoot, "platform.db")));
    }

    [Fact]
    public void A_file_that_was_there_is_kept_beside_the_new_one()
    {
        SetUpPersonWithData();
        var shared = Path.Combine(_root, "common", "Deluno", "data", "deluno.json");
        Directory.CreateDirectory(Path.GetDirectoryName(shared)!);
        File.WriteAllText(shared, "{ \"port\": 1234 }");

        Assert.Null(AppSettings.ShareWithService());

        Assert.Equal("{ \"port\": 1234 }", File.ReadAllText(shared + ".before-service"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DELUNO_TEST_LOCALAPPDATA", _local);
        Environment.SetEnvironmentVariable("DELUNO_TEST_COMMONAPPDATA", _common);
        AppSettings.ForgetLastRead();
        try { Directory.Delete(_root, true); } catch { }
    }
}
