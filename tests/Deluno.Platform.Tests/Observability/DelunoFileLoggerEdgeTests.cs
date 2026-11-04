using System.Text.Json;
using Deluno.Infrastructure.Observability;
using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Deluno.Platform.Tests.Observability;

/// <summary>The durable log's less common paths: every kind of property value, scopes, exceptions, a folder that cannot be made and the card buffer (#1268).</summary>
public sealed class DelunoFileLoggerEdgeTests : IDisposable
{
    private readonly string dataRoot = Path.Combine(Path.GetTempPath(), "deluno-file-log-edge-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(dataRoot))
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Fact]
    public void Every_kind_of_property_value_is_written_as_its_own_json_kind()
    {
        var moment = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.FromHours(11));
        var day = new DateTime(2026, 11, 5, 1, 2, 3, DateTimeKind.Utc);
        var state = new List<KeyValuePair<string, object?>>
        {
            new("Flag", true),
            new("Count", 3),
            new("Big", 5_000_000_000L),
            new("Ratio", 1.5),
            new("Money", 2.25m),
            new("Moment", moment),
            new("Day", day),
            new("Nothing", null),
            new("Address", new Uri("http://example.test/a")),
            new("", "an empty name is skipped"),
            new("{OriginalFormat}", "the template"),
        };

        using (var provider = CreateProvider())
        using (var factory = LoggerFactory.Create(builder => builder.AddProvider(provider)))
        {
            factory.CreateLogger("Deluno.Test").Log(LogLevel.Information, new EventId(7, "Seven"), state, null, (_, _) => "hello");
        }

        using var document = JsonDocument.Parse(Assert.Single(ReadLines()));
        var root = document.RootElement;
        Assert.Equal(7, root.GetProperty("eventId").GetInt32());
        Assert.Equal("Seven", root.GetProperty("eventName").GetString());
        var properties = root.GetProperty("properties");
        Assert.Equal(JsonValueKind.True, properties.GetProperty("Flag").ValueKind);
        Assert.Equal(3, properties.GetProperty("Count").GetInt64());
        Assert.Equal(5_000_000_000L, properties.GetProperty("Big").GetInt64());
        Assert.Equal(1.5, properties.GetProperty("Ratio").GetDouble());
        Assert.Equal(2.25, properties.GetProperty("Money").GetDouble());
        Assert.Equal("2026-11-05T01:00:00.0000000+00:00", properties.GetProperty("Moment").GetString());
        Assert.Equal("2026-11-05T01:02:03.0000000Z", properties.GetProperty("Day").GetString());
        Assert.Equal(JsonValueKind.Null, properties.GetProperty("Nothing").ValueKind);
        Assert.Equal("http://example.test/a", properties.GetProperty("Address").GetString());
        Assert.Equal("the template", properties.GetProperty("template").GetString());
        Assert.False(properties.TryGetProperty("", out _));
    }

    [Fact]
    public void A_scope_that_is_not_a_set_of_pairs_is_written_as_text_and_an_exception_is_kept()
    {
        using (var provider = CreateProvider())
        using (var factory = LoggerFactory.Create(builder => builder.AddProvider(provider)))
        {
            var logger = factory.CreateLogger("Deluno.Test");
            using (logger.BeginScope("importing apikey=hunter2hunter2"))
            {
                logger.LogError(new InvalidOperationException("it broke"), "Failed");
            }
        }

        using var document = JsonDocument.Parse(Assert.Single(ReadLines()));
        var root = document.RootElement;
        var scope = root.GetProperty("scope").GetProperty("scope0").GetString();
        Assert.Contains("importing", scope);
        Assert.DoesNotContain("hunter2hunter2", scope);
        Assert.Contains("it broke", root.GetProperty("exception").GetString());
    }

    [Fact]
    public void A_level_of_none_is_never_written_and_the_data_protection_lines_are_not_routine_traffic()
    {
        using (var provider = CreateProvider())
        using (var factory = LoggerFactory.Create(builder => builder.AddProvider(provider)))
        {
            var logger = factory.CreateLogger("Deluno.Test");
            Assert.False(logger.IsEnabled(LogLevel.None));
            logger.Log(LogLevel.None, "nothing");
        }

        Assert.Empty(ReadLines());
        Assert.False(DelunoFileLoggerProvider.IsRoutineTraffic("Microsoft.AspNetCore.DataProtection.KeyManagement"));
        Assert.True(DelunoFileLoggerProvider.IsRoutineTraffic("Microsoft.AspNetCore.Routing"));
        Assert.True(DelunoFileLoggerProvider.IsRoutineTraffic("System.Net.Http.HttpClient.x"));
        Assert.True(DelunoFileLoggerProvider.IsRoutineTraffic("Microsoft.Extensions.Http.y"));
        Assert.False(DelunoFileLoggerProvider.IsRoutineTraffic("Deluno.Api"));
    }

    [Fact]
    public void A_log_folder_that_cannot_be_made_never_stops_the_caller()
    {
        Directory.CreateDirectory(dataRoot);
        File.WriteAllText(Path.Combine(dataRoot, "logs"), "a file where the folder should be");

        using var provider = CreateProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var logger = factory.CreateLogger("Deluno.Test");

        // The writer gives up at once; logging goes on without a sound.
        for (var i = 0; i < 25_000; i++)
        {
            logger.LogInformation("line {Number}", i);
        }

        Assert.True(File.Exists(Path.Combine(dataRoot, "logs")));
    }

    [Fact]
    public void Notable_lines_also_reach_the_log_cards_buffer_and_routine_ones_do_not()
    {
        var buffer = new DelunoLogBuffer();
        using (var provider = new DelunoFileLoggerProvider(
                   Options.Create(new StoragePathOptions { DataRoot = dataRoot }),
                   Options.Create(new DelunoFileLogOptions()),
                   TimeProvider.System,
                   null,
                   buffer))
        using (var factory = LoggerFactory.Create(builder => builder.AddProvider(provider)))
        {
            var logger = factory.CreateLogger("Deluno.Test");
            logger.LogWarning("a warning");
            logger.LogInformation("a routine line");
        }

        Assert.Equal(["a warning"], buffer.Recent("all", 10).Select(entry => entry.Message));
    }

    private string LogDirectory => Path.Combine(dataRoot, "logs");

    private DelunoFileLoggerProvider CreateProvider()
        => new(
            Options.Create(new StoragePathOptions { DataRoot = dataRoot }),
            Options.Create(new DelunoFileLogOptions()),
            TimeProvider.System);

    private List<string> ReadLines()
        => Directory.Exists(LogDirectory)
            ? Directory.GetFiles(LogDirectory, "deluno-*.jsonl").SelectMany(File.ReadAllLines).Where(line => line.Length > 0).ToList()
            : [];
}
