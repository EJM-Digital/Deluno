using System.Reflection;
using Deluno.Filesystem;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Quality;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>
/// The existing-library scan (coverage register, engines) when a folder cannot be read: a title folder that cannot be listed becomes an
/// "unreadable" issue with the system's own words, and a root that cannot be listed gives an empty list; both are logged as a warning
/// and neither fails the scan. The unreadable folder is a real path the operating system refuses to list (one that sits under a file),
/// so the real exception comes from the real file system; the private detection methods are called directly because the preview
/// route checks the root first.
/// </summary>
public sealed class AnUnreadableFolderDuringALibraryScanIsReportedNotFatalTests
{
    private sealed class CapturingLogger : ILogger<ExistingLibraryImportService>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add((logLevel, formatter(state, exception), exception));
    }

    private static async Task<(ExistingLibraryImportService Service, CapturingLogger Log, string Root, TestStorage Storage)> StartAsync()
    {
        var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-15T08:00:00Z"));
        var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
        await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new MoviesSchemaInitializer(storage.Factory, migrator, NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new SeriesSchemaInitializer(storage.Factory, migrator, NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var root = Directory.CreateDirectory(Path.Combine(storage.DataRoot, "Library")).FullName;
        var log = new CapturingLogger();
        var service = new ExistingLibraryImportService(
            new SqliteLibrariesRepository(storage.Factory, time),
            new SqliteLibraryImportRunsRepository(storage.Factory, time),
            new SqliteMovieCatalogRepository(storage.Factory, time),
            new SqliteSeriesCatalogRepository(storage.Factory, time),
            new MediaDecisionService(new VersionedMediaPolicyEngine()),
            new QualityModelService(storage.Factory, time),
            time,
            log,
            null);
        return (service, log, root, storage);
    }

    private static object Call(ExistingLibraryImportService service, string method, params object?[] args)
        => typeof(ExistingLibraryImportService).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, args)!;

    private static object NoKnownFiles()
        => typeof(ExistingLibraryImportService).GetNestedType("KnownLibraryFiles", BindingFlags.NonPublic)!
            .GetProperty("None", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

    private static (string? Kind, string? Detail) IssueOf(object detection)
    {
        var issue = detection.GetType().GetProperty("Issue")!.GetValue(detection);
        return issue is null
            ? (null, null)
            : ((string?)issue.GetType().GetProperty("Kind")!.GetValue(issue), (string?)issue.GetType().GetProperty("Detail")!.GetValue(issue));
    }

    [Theory]
    [InlineData("DetectMovie")]
    [InlineData("DetectSeries")]
    public async Task A_title_folder_the_system_cannot_list_becomes_an_unreadable_issue_with_its_own_words_and_a_warning(string method)
    {
        var (service, log, root, storage) = await StartAsync();
        using var _ = storage;
        var aFile = Path.Combine(root, "not-a-folder.txt");
        await File.WriteAllTextAsync(aFile, "x");
        var cannotBeListed = Path.Combine(aFile, "Some Title (2001)");

        var detection = Call(service, method, cannotBeListed, NoKnownFiles());

        var (kind, detail) = IssueOf(detection);
        Assert.Equal("unreadable", kind);
        Assert.False(string.IsNullOrWhiteSpace(detail));
        Assert.Contains(log.Lines, line => line.Level == LogLevel.Warning && line.Exception is IOException or UnauthorizedAccessException && line.Message.Contains(cannotBeListed, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_root_that_cannot_be_listed_gives_an_empty_list_and_a_warning_so_an_unreachable_root_looks_like_an_empty_library()
    {
        var (service, log, root, storage) = await StartAsync();
        using var _ = storage;
        var aFile = Path.Combine(root, "not-a-folder.txt");
        await File.WriteAllTextAsync(aFile, "x");

        var entries = (List<string>)Call(service, "ListTopLevelEntries", aFile);

        Assert.Empty(entries);
        Assert.Contains(log.Lines, line => line.Level == LogLevel.Warning && line.Message.Contains(aFile, StringComparison.Ordinal));
    }
}
