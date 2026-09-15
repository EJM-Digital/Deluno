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

    private DelunoFileLoggerProvider CreateProvider(DelunoFileLogOptions? options = null)
        => new(
            Options.Create(new StoragePathOptions { DataRoot = dataRoot }),
            Options.Create(options ?? new DelunoFileLogOptions()),
            TimeProvider.System);

    private List<string> ReadLines()
        => Directory.GetFiles(LogDirectory, "deluno-*.jsonl")
            .OrderBy(path => path, StringComparer.Ordinal)
            .SelectMany(File.ReadAllLines)
            .Where(line => line.Length > 0)
            .ToList();
}
