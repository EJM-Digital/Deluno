using System.Text;
using Deluno.Infrastructure.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Deluno.Persistence.Tests.Storage;

/// <summary>The System screen's log card: what is kept, how a name and a line are shortened, and how a restart refills it from today's files (#1098).</summary>
public sealed class DelunoLogBufferTests : IDisposable
{
    private static readonly DateTimeOffset Noon = DateTimeOffset.Parse("2026-11-05T12:00:00Z");
    private readonly string directory = Directory.CreateTempSubdirectory("deluno-logbuffer-").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Theory]
    [InlineData("Deluno.Worker.HealthCheckHostedService", "HealthCheck")]
    [InlineData("Deluno.Api.DelunoBackupService", "DelunoBackup")]
    [InlineData("Deluno.Api.Service", "Service")]
    [InlineData("Deluno.Api.Thing", "Thing")]
    [InlineData("NoDots", "NoDots")]
    public void A_category_becomes_a_plain_source_name(string category, string expected)
        => Assert.Equal(expected, DelunoLogBuffer.SourceName(category));

    [Theory]
    [InlineData(LogLevel.Critical, "error")]
    [InlineData(LogLevel.Error, "error")]
    [InlineData(LogLevel.Warning, "warning")]
    [InlineData(LogLevel.Information, "info")]
    public void A_level_is_named_error_warning_or_info(LogLevel level, string expected)
        => Assert.Equal(expected, DelunoLogBuffer.LevelName(level));

    [Fact]
    public void A_message_is_cut_to_its_first_line_and_to_what_one_row_holds()
    {
        Assert.Equal("first", DelunoLogBuffer.OneLine("  first  \nsecond"));
        Assert.Equal(new string('a', 400), DelunoLogBuffer.OneLine(new string('a', 400)));
        Assert.Equal(new string('a', 397) + "...", DelunoLogBuffer.OneLine(new string('a', 401)));
    }

    [Fact]
    public void A_blank_message_is_not_kept()
    {
        var buffer = new DelunoLogBuffer(new FakeTimeProvider(Noon));
        buffer.Add(LogLevel.Error, "Deluno.X", "   ");

        Assert.Empty(buffer.Recent("all", 10));
    }

    [Fact]
    public void Recent_gives_only_today_newest_first_for_the_asked_level_and_count()
    {
        var time = new FakeTimeProvider(Noon.AddDays(-1));
        var buffer = new DelunoLogBuffer(time);
        buffer.Add(LogLevel.Error, "Deluno.X", "yesterday");
        time.SetUtcNow(Noon);
        buffer.Add(LogLevel.Error, "Deluno.X", "early error");
        time.Advance(TimeSpan.FromMinutes(1));
        buffer.Add(LogLevel.Warning, "Deluno.X", "warning");
        time.Advance(TimeSpan.FromMinutes(1));
        buffer.Add(LogLevel.Error, "Deluno.X", "late error");

        Assert.Equal(["late error", "warning", "early error"], buffer.Recent("all", 10).Select(e => e.Message));
        Assert.Equal(["late error", "warning", "early error"], buffer.Recent(null, 10).Select(e => e.Message));
        Assert.Equal(["late error", "early error"], buffer.Recent("error", 10).Select(e => e.Message));
        Assert.Equal(["warning"], buffer.Recent("warning", 10).Select(e => e.Message));
        Assert.Equal(["late error"], buffer.Recent("all", 1).Select(e => e.Message));
        Assert.Single(buffer.Recent("all", 0));
    }

    [Fact]
    public void Seeding_from_a_folder_that_is_not_there_does_nothing_and_is_not_repeated()
    {
        var buffer = new DelunoLogBuffer(new FakeTimeProvider(Noon));

        buffer.SeedFromFiles(Path.Combine(directory, "missing"));
        WriteLog("deluno-20261105.jsonl", Line("Error", "Deluno.Api.Thing", "late file"));
        buffer.SeedFromFiles(directory);

        Assert.Empty(buffer.Recent("all", 10));
    }

    [Fact]
    public void Seeding_reads_only_todays_notable_lines_and_skips_what_it_cannot_read()
    {
        var buffer = new DelunoLogBuffer(new FakeTimeProvider(Noon));
        WriteLog("deluno-20261105.jsonl",
            Line("Error", "Deluno.Api.Thing", "kept error"),
            "not json at all",
            "{\"timestamp\":\"2026-11-05T10:00:00Z\"}",
            "{\"level\":7,\"timestamp\":\"2026-11-05T10:00:00Z\"}",
            Line("NotALevel", "Deluno.Api.Thing", "bad level"),
            "{\"timestamp\":\"not a time\",\"level\":\"Error\",\"category\":\"A\",\"message\":\"bad time\"}",
            Line("Debug", "Deluno.Api.Thing", "not notable"),
            Line("Error", "Deluno.Api.Thing", "  "),
            "{\"timestamp\":\"2026-11-05T10:01:00Z\",\"level\":\"Warning\"}",
            "{\"timestamp\":\"2026-11-05T10:02:00Z\",\"level\":\"Warning\",\"category\":null,\"message\":\"no category\"}");
        WriteLog("deluno-20261105.1.jsonl", Line("Warning", "Deluno.Api.Other", "second part"));
        WriteLog("deluno-20261104.jsonl", Line("Error", "Deluno.Api.Thing", "yesterday"));

        buffer.SeedFromFiles(directory);

        var seeded = buffer.Recent("all", 50).Select(e => (e.Message, e.Source)).OrderBy(e => e.Message).ToArray();
        Assert.Equal([("kept error", "Thing"), ("no category", ""), ("second part", "Other")], seeded);
    }

    [Fact]
    public void A_line_already_logged_since_start_is_not_added_twice_from_the_file()
    {
        var buffer = new DelunoLogBuffer(new FakeTimeProvider(Noon));
        buffer.Add(LogLevel.Error, "Deluno.Api.Thing", "same line");
        WriteLog("deluno-20261105.jsonl",
            Line("Error", "Deluno.Api.Thing", "same line", Noon.AddSeconds(1)),
            Line("Error", "Deluno.Api.Thing", "same line", Noon.AddSeconds(30)));

        buffer.SeedFromFiles(directory);

        Assert.Equal(2, buffer.Recent("all", 10).Count);
    }

    [Fact]
    public void Seeding_keeps_no_more_than_the_capacity_and_the_newest_of_them()
    {
        var buffer = new DelunoLogBuffer(new FakeTimeProvider(Noon));
        var lines = Enumerable.Range(0, DelunoLogBuffer.Capacity + 5)
            .Select(i => Line("Error", "Deluno.Api.Thing", $"line {i}", Noon.AddSeconds(-(DelunoLogBuffer.Capacity + 5) + i)))
            .ToArray();
        WriteLog("deluno-20261105.jsonl", lines);

        buffer.SeedFromFiles(directory);

        var kept = buffer.Recent("all", 10_000);
        Assert.Equal(DelunoLogBuffer.Capacity, kept.Count);
        Assert.DoesNotContain(kept, e => e.Message == "line 0");
        Assert.Contains(kept, e => e.Message == $"line {DelunoLogBuffer.Capacity + 4}");
    }

    [Fact]
    public void A_big_file_is_read_from_its_tail_without_its_cut_first_line()
    {
        var buffer = new DelunoLogBuffer(new FakeTimeProvider(Noon));
        var filler = Line("Debug", "Deluno.Api.Thing", new string('x', 1000));
        var path = Path.Combine(directory, "deluno-20261105.jsonl");
        using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
        {
            writer.WriteLine(Line("Error", "Deluno.Api.Thing", "far too early to be read"));
            for (var i = 0; i < 2200; i++)
            {
                writer.WriteLine(filler);
            }

            writer.WriteLine(Line("Error", "Deluno.Api.Thing", "near the end"));
        }

        Assert.True(new FileInfo(path).Length > 2 * 1024 * 1024);
        buffer.SeedFromFiles(directory);

        Assert.Equal(["near the end"], buffer.Recent("all", 10).Select(e => e.Message));
    }

    [Fact]
    public void A_log_file_that_cannot_be_opened_leaves_what_was_logged_since_start()
    {
        var buffer = new DelunoLogBuffer(new FakeTimeProvider(Noon));
        buffer.Add(LogLevel.Error, "Deluno.Api.Thing", "since start");
        var link = Path.Combine(directory, "deluno-20261105.jsonl");
        try
        {
            File.CreateSymbolicLink(link, Path.Combine(directory, "gone.jsonl"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return; // a Windows account without the symbolic-link right cannot make the dangling link; Linux CI can
        }

        buffer.SeedFromFiles(directory);

        Assert.Equal(["since start"], buffer.Recent("all", 10).Select(e => e.Message));
    }

    private static string Line(string level, string category, string message, DateTimeOffset? at = null)
        => $"{{\"timestamp\":\"{(at ?? Noon).UtcDateTime:O}\",\"level\":\"{level}\",\"category\":\"{category}\",\"message\":\"{message}\"}}";

    private void WriteLog(string name, params string[] lines)
        => File.WriteAllLines(Path.Combine(directory, name), lines);
}
