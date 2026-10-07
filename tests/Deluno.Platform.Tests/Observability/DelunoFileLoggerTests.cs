using System.Text.Json;
using Deluno.Infrastructure.Observability;
using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Deluno.Platform.Tests.Observability;

/// <summary>
/// Deluno's durable log. Seen on the physical rig: a headless host kept no
/// application log anywhere, because the console it wrote to was discarded.
/// </summary>
public sealed class DelunoFileLoggerTests : IDisposable
{
    private readonly string dataRoot = Path.Combine(Path.GetTempPath(), "deluno-file-log-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void A_logged_event_becomes_one_json_line_with_its_properties_and_trace_scope()
    {
        using (var provider = CreateProvider())
        using (var factory = LoggerFactory.Create(builder => builder.AddProvider(provider)))
        {
            var logger = factory.CreateLogger("Deluno.Filesystem.ImportPipelineService");
            using (logger.BeginScope(new Dictionary<string, object> { ["TraceId"] = "trace-123", ["Path"] = "/api/imports" }))
            {
                logger.LogInformation(
                    "Imported {ReleaseTitle} for dispatch {DispatchId} to {DestinationPath}",
                    "Sintel.2010.1080p.NF.WEB-DL.DDP5.1.H.264-DELUNO",
                    "dispatch-42",
                    @"\\nas\library\Sintel (2010)\Sintel (2010).mkv");
            }
        }

        var line = Assert.Single(ReadLines());
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        Assert.Equal("Information", root.GetProperty("level").GetString());
        Assert.Equal("Deluno.Filesystem.ImportPipelineService", root.GetProperty("category").GetString());
        Assert.Contains("dispatch-42", root.GetProperty("message").GetString());
        var properties = root.GetProperty("properties");
        Assert.Equal("Sintel.2010.1080p.NF.WEB-DL.DDP5.1.H.264-DELUNO", properties.GetProperty("ReleaseTitle").GetString());
        Assert.Equal("dispatch-42", properties.GetProperty("DispatchId").GetString());
        Assert.Equal("Imported {ReleaseTitle} for dispatch {DispatchId} to {DestinationPath}", properties.GetProperty("template").GetString());
        Assert.Equal("trace-123", root.GetProperty("scope").GetProperty("TraceId").GetString());
    }

    [Fact]
    public void Secrets_are_masked_by_name_and_inside_messages()
    {
        using (var provider = CreateProvider())
        using (var factory = LoggerFactory.Create(builder => builder.AddProvider(provider)))
        {
            factory.CreateLogger("Deluno.Integrations").LogWarning(
                "Indexer {Indexer} refused http://indexer.test/api?t=search&apikey={Leak} with key {ApiKey}",
                "Fixture",
                "abc123secret",
                "zzz-secret");
        }

        var line = Assert.Single(ReadLines());
        Assert.DoesNotContain("abc123secret", line);
        Assert.DoesNotContain("zzz-secret", line);
        using var document = JsonDocument.Parse(line);
        Assert.Contains("apikey=[redacted]", document.RootElement.GetProperty("message").GetString());
        Assert.Equal("[redacted]", document.RootElement.GetProperty("properties").GetProperty("ApiKey").GetString());
        Assert.Equal("Fixture", document.RootElement.GetProperty("properties").GetProperty("Indexer").GetString());
    }

    [Fact]
    public void A_full_file_rolls_to_a_numbered_sibling()
    {
        using (var provider = CreateProvider(new DelunoFileLogOptions { MaxFileBytes = 400 }))
        using (var factory = LoggerFactory.Create(builder => builder.AddProvider(provider)))
        {
            var logger = factory.CreateLogger("Deluno.Test");
            for (var index = 0; index < 12; index++)
            {
                logger.LogInformation("Line {Index} with enough words to fill a small file quickly", index);
            }
        }

        var files = Directory.GetFiles(LogDirectory, "deluno-*.jsonl");
        Assert.True(files.Length > 1, $"Expected rolled files, found {files.Length}.");
        Assert.Equal(12, ReadLines().Count);
    }

    [Fact]
    public void Only_Deluno_log_files_past_retention_are_removed()
    {
        Directory.CreateDirectory(LogDirectory);
        var expired = Path.Combine(LogDirectory, "deluno-20200101.jsonl");
        var recent = Path.Combine(LogDirectory, "deluno-20990101.jsonl");
        var foreign = Path.Combine(LogDirectory, "notes.txt");
        foreach (var file in new[] { expired, recent, foreign })
        {
            File.WriteAllText(file, "{}");
        }

        File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-30));
        File.SetLastWriteTimeUtc(foreign, DateTime.UtcNow.AddDays(-30));

        using (var provider = CreateProvider(new DelunoFileLogOptions { RetainDays = 14 }))
        using (var factory = LoggerFactory.Create(builder => builder.AddProvider(provider)))
        {
            factory.CreateLogger("Deluno.Test").LogInformation("started");
        }

        Assert.False(File.Exists(expired));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(foreign));
    }

    [Fact]
    public void Disabled_writes_nothing()
    {
        using (var provider = CreateProvider(new DelunoFileLogOptions { Enabled = false }))
        using (var factory = LoggerFactory.Create(builder => builder.AddProvider(provider)))
        {
            factory.CreateLogger("Deluno.Test").LogInformation("not kept");
        }

        Assert.False(Directory.Exists(LogDirectory));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(dataRoot, recursive: true);
        }
        catch (IOException)
        {
            // Includes a folder that was never created.
        }
    }

    private string LogDirectory => Path.Combine(dataRoot, "logs");

    /// <summary>
    /// #866: changing the Log Level setting takes effect immediately for
    /// new log lines, without a restart, by consulting the same
    /// IRuntimeLogLevelStore instance the Settings endpoint writes to.
    /// </summary>
    [Fact]
    public void Raising_the_runtime_log_level_stops_lower_level_lines_without_a_restart()
    {
        var store = new RecordingRuntimeLogLevelStore(LogLevel.Information);
        using (var provider = CreateProvider(runtimeLogLevelStore: store))
        using (var factory = LoggerFactory.Create(builder => builder.AddProvider(provider)))
        {
            var logger = factory.CreateLogger("Deluno.Test");

            logger.LogInformation("Before raising the level.");
            store.Current = LogLevel.Warning;
            logger.LogInformation("After raising the level -- must not appear.");
            logger.LogWarning("After raising the level -- must appear.");
        }

        var lines = ReadLines();
        Assert.Contains(lines, line => line.Contains("Before raising the level."));
        Assert.DoesNotContain(lines, line => line.Contains("must not appear"));
        Assert.Contains(lines, line => line.Contains("must appear"));
    }

    /// <summary>
    /// #1152: every client poll and UI request used to be written at
    /// Information, about 70 MB in a few hours on an idle install.
    /// </summary>
    [Fact]
    public void Routine_http_and_request_lines_are_kept_out_until_the_log_level_is_turned_up()
    {
        var store = new RecordingRuntimeLogLevelStore(LogLevel.Information);
        using (var provider = CreateProvider(runtimeLogLevelStore: store))
        using (var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(provider)))
        {
            var client = factory.CreateLogger("System.Net.Http.HttpClient.download-clients.LogicalHandler");
            var request = factory.CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics");
            var routing = factory.CreateLogger("Microsoft.AspNetCore.Routing.EndpointMiddleware");
            var own = factory.CreateLogger("Deluno.Test");

            client.LogInformation("poll quiet");
            request.LogInformation("request quiet");
            routing.LogInformation("endpoint quiet");
            request.LogWarning("slow request kept");
            own.LogInformation("own line kept");

            store.Current = LogLevel.Debug;
            client.LogInformation("poll turned up");
        }

        var lines = ReadLines();
        Assert.DoesNotContain(lines, line => line.Contains("quiet"));
        Assert.Contains(lines, line => line.Contains("slow request kept"));
        Assert.Contains(lines, line => line.Contains("own line kept"));
        Assert.Contains(lines, line => line.Contains("poll turned up"));
    }

    [Fact]
    public void The_logs_folder_stays_under_its_size_cap_by_removing_the_oldest_files()
    {
        Directory.CreateDirectory(LogDirectory);
        var oldest = Path.Combine(LogDirectory, "deluno-20990101.jsonl");
        var middle = Path.Combine(LogDirectory, "deluno-20990102.jsonl");
        var newest = Path.Combine(LogDirectory, "deluno-20990103.jsonl");
        foreach (var file in new[] { oldest, middle, newest })
        {
            File.WriteAllText(file, new string('x', 1000));
        }

        File.SetLastWriteTimeUtc(oldest, DateTime.UtcNow.AddDays(-3));
        File.SetLastWriteTimeUtc(middle, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(newest, DateTime.UtcNow.AddDays(-1));

        using (var provider = CreateProvider(new DelunoFileLogOptions { MaxTotalBytes = 2500 }))
        using (var factory = LoggerFactory.Create(builder => builder.AddProvider(provider)))
        {
            factory.CreateLogger("Deluno.Test").LogInformation("started");
        }

        Assert.False(File.Exists(oldest));
        Assert.True(File.Exists(middle));
        Assert.True(File.Exists(newest));
    }

    private sealed class RecordingRuntimeLogLevelStore(LogLevel initial) : IRuntimeLogLevelStore
    {
        public LogLevel Current { get; set; } = initial;

        public Task SetAsync(LogLevel level, CancellationToken cancellationToken)
        {
            Current = level;
            return Task.CompletedTask;
        }
    }

    private DelunoFileLoggerProvider CreateProvider(DelunoFileLogOptions? options = null, IRuntimeLogLevelStore? runtimeLogLevelStore = null)
        => new(
            Options.Create(new StoragePathOptions { DataRoot = dataRoot }),
            Options.Create(options ?? new DelunoFileLogOptions()),
            TimeProvider.System,
            runtimeLogLevelStore);

    private List<string> ReadLines()
        => Directory.GetFiles(LogDirectory, "deluno-*.jsonl")
            .OrderBy(path => path, StringComparer.Ordinal)
            .SelectMany(File.ReadAllLines)
            .Where(line => line.Length > 0)
            .ToList();
}
