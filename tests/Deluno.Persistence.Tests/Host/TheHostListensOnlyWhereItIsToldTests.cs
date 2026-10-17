using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Deluno.Persistence.Tests.Host;

/// <summary>
/// Platform AUT-21 and DOK-14: the real Deluno.Host process, started as its own program on a real temp data folder, listens on
/// the machine itself unless Server:AllowLan is set; the Docker image and the compose file set it on purpose.
/// </summary>
public sealed class TheHostListensOnlyWhereItIsToldTests
{
    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string RepositoryFile(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Deluno.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, name);
    }

    /// <summary>Starts Deluno.Host.dll, returns the lines it printed up to "Application started", and stops it.</summary>
    private static async Task<(IReadOnlyList<string> Lines, int Port)> StartHostAsync(string? allowLan)
    {
        var port = FreePort();
        var root = Path.Combine(Path.GetTempPath(), "deluno-host-listen", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "work"));
        var start = new ProcessStartInfo("dotnet", $"\"{Path.Combine(AppContext.BaseDirectory, "Deluno.Host.dll")}\"")
        {
            WorkingDirectory = Path.Combine(root, "work"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.Environment["Storage__DataRoot"] = Path.Combine(root, "data");
        start.Environment["Server__Port"] = port.ToString();
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment.Remove("ASPNETCORE_URLS");
        start.Environment.Remove("Server__AllowLan");
        if (allowLan is not null)
        {
            start.Environment["Server__AllowLan"] = allowLan;
        }

        using var process = Process.Start(start)!;
        var lines = new List<string>();
        var started = new TaskCompletionSource();
        void Take(string? line)
        {
            if (line is null) return;
            lock (lines) lines.Add(line);
            if (line.Contains("Application started", StringComparison.Ordinal)) started.TrySetResult();
        }

        process.OutputDataReceived += (_, e) => Take(e.Data);
        process.ErrorDataReceived += (_, e) => Take(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            var finished = await Task.WhenAny(started.Task, Task.Delay(TimeSpan.FromSeconds(90))); // reason: the 90 s limit on waiting for the host's start line, not a pause
            Assert.True(finished == started.Task, "Deluno.Host did not report 'Application started'. It printed:\n" + string.Join('\n', lines));
            return (lines.ToArray(), port);
        }
        finally
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task With_no_setting_the_host_listens_on_the_machine_itself_and_not_on_every_address()
    {
        var (lines, port) = await StartHostAsync(allowLan: null);

        var listening = lines.Where(line => line.Contains("Now listening on:", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(listening);
        Assert.All(listening, line => Assert.Contains($":{port}", line, StringComparison.Ordinal));
        Assert.DoesNotContain(listening, line => line.Contains("[::]", StringComparison.Ordinal) || line.Contains("0.0.0.0", StringComparison.Ordinal));
        Assert.Contains(listening, line => line.Contains("localhost", StringComparison.OrdinalIgnoreCase) || line.Contains("127.0.0.1", StringComparison.Ordinal) || line.Contains("[::1]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Turning_the_lan_setting_on_makes_the_host_listen_on_every_address()
    {
        var (lines, port) = await StartHostAsync(allowLan: "true");

        var listening = lines.Where(line => line.Contains("Now listening on:", StringComparison.Ordinal)).ToArray();
        Assert.Contains(listening, line => (line.Contains("[::]", StringComparison.Ordinal) || line.Contains("0.0.0.0", StringComparison.Ordinal)) && line.Contains($":{port}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Turning_the_lan_setting_off_by_name_keeps_it_on_the_machine_itself()
    {
        var (lines, _) = await StartHostAsync(allowLan: "false");

        var listening = lines.Where(line => line.Contains("Now listening on:", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(listening);
        Assert.DoesNotContain(listening, line => line.Contains("[::]", StringComparison.Ordinal) || line.Contains("0.0.0.0", StringComparison.Ordinal));
    }

    [Fact]
    public void The_image_has_no_PUID_or_PGID_setting_on_purpose_and_the_readme_says_so_and_names_the_user_setting_its_examples_use()
    {
        var readme = File.ReadAllText(RepositoryFile("README.md"));

        Assert.DoesNotContain("PUID", File.ReadAllText(RepositoryFile("Dockerfile")), StringComparison.Ordinal);
        Assert.DoesNotContain("PGID", File.ReadAllText(RepositoryFile("Dockerfile")), StringComparison.Ordinal);
        Assert.DoesNotContain("PUID", File.ReadAllText(RepositoryFile("compose.yaml")), StringComparison.Ordinal);
        Assert.Contains("It does not have the `PUID`/`PGID` settings", readme, StringComparison.Ordinal);
        Assert.Contains("tell Docker who to run as with `user:` (or `--user`)", readme, StringComparison.Ordinal);
        Assert.Contains("user: \"1000:1000\"", readme, StringComparison.Ordinal);
        // The image itself runs as the non-root account the README promises.
        Assert.Contains("USER app", File.ReadAllText(RepositoryFile("Dockerfile")), StringComparison.Ordinal);
    }

    [Fact]
    public void The_compose_file_restarts_the_container_unless_it_was_stopped_and_checks_its_health_on_the_port_the_host_listens_on()
    {
        var compose = File.ReadAllText(RepositoryFile("compose.yaml"));

        Assert.Contains("restart: unless-stopped", compose, StringComparison.Ordinal);
        Assert.Contains("http://127.0.0.1:8080/health", compose, StringComparison.Ordinal);
        Assert.Contains("HEALTHCHECK", File.ReadAllText(RepositoryFile("Dockerfile")), StringComparison.Ordinal);
    }

    [Fact]
    public void The_docker_image_and_the_compose_file_turn_the_lan_setting_on_and_publish_the_port_the_host_listens_on()
    {
        var dockerfile = File.ReadAllText(RepositoryFile("Dockerfile"));
        var compose = File.ReadAllText(RepositoryFile("compose.yaml"));

        Assert.Contains("Server__AllowLan=true", dockerfile, StringComparison.Ordinal);
        Assert.Contains("Server__Port=8080", dockerfile, StringComparison.Ordinal);
        Assert.Contains("ASPNETCORE_URLS=http://+:8080", dockerfile, StringComparison.Ordinal);
        Assert.Contains("Server__AllowLan: \"true\"", compose, StringComparison.Ordinal);
        Assert.Contains("8080", compose, StringComparison.Ordinal);
        // The README tells people to narrow it to the machine by how the port is published, not by an image setting.
        Assert.Contains("127.0.0.1:5099:8080", File.ReadAllText(RepositoryFile("README.md")), StringComparison.Ordinal);
    }
}
