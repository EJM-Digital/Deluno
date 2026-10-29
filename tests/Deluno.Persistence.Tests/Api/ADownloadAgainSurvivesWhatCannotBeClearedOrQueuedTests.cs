using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// "Download this title again", asked by another tool: when what is in the way of fetching it cannot be cleared the failure is
/// logged and the search still runs; when the search cannot be queued the file is already in the recycle bin, so the answer is
/// still 202 with no search job and a message that says to search from the title page.
/// </summary>
public sealed class ADownloadAgainSurvivesWhatCannotBeClearedOrQueuedTests
{
    /// <summary>The real service, except that the named method fails while <see cref="Broken"/> is set.</summary>
    public class FailsOn : DispatchProxy
    {
        public object Inner { get; set; } = null!;

        public string Method { get; set; } = string.Empty;

        public static volatile bool Broken;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (Broken && targetMethod!.Name == Method)
            {
                var returns = targetMethod.ReturnType;
                var failure = new InvalidOperationException("the database is busy");
                return returns.IsGenericType
                    ? typeof(Task).GetMethod(nameof(Task.FromException), 1, [typeof(Exception)])!.MakeGenericMethod(returns.GenericTypeArguments[0]).Invoke(null, [failure])
                    : Task.FromException(failure);
            }

            try
            {
                return targetMethod!.Invoke(Inner, args);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                throw exception.InnerException;
            }
        }
    }

    private sealed class Lines : ILoggerProvider
    {
        public List<(LogLevel Level, string Message)> Seen { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Sink(this);

        public void Dispose() { }

        private sealed class Sink(Lines owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner.Seen)
                {
                    owner.Seen.Add((logLevel, formatter(state, exception)));
                }
            }
        }
    }

    private static void Wrap<TService, TImplementation>(IServiceCollection services, string method) where TService : class where TImplementation : class, TService
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(TService)) services.RemoveAt(i);
        }

        services.AddSingleton<TImplementation>();
        services.AddSingleton<TService>(provider =>
        {
            var proxy = DispatchProxy.Create<TService, FailsOn>();
            var wrapper = (FailsOn)(object)proxy;
            wrapper.Inner = provider.GetRequiredService<TImplementation>();
            wrapper.Method = method;
            return proxy;
        });
    }

    private static async Task<(string MovieId, string Path)> FilmWithAFileAsync(ApplicationTestHost app)
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var library = await app.Client.PostAsJsonAsync("/api/libraries/", new { name = "Films", mediaType = "movies", rootPath = root });
        Assert.True(library.IsSuccessStatusCode, await library.Content.ReadAsStringAsync());
        var libraryId = (await library.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var folder = Directory.CreateDirectory(Path.Combine(root, "Example Film (2024)")).FullName;
        var path = Path.Combine(folder, "Example Film (2024) - 1080p.mkv");
        await File.WriteAllBytesAsync(path, new byte[4096]);
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        await movies.ImportExistingAsync(
            libraryId, "Example Film", 2024,
            wantedStatus: "covered", wantedReason: "Imported", currentQuality: "WEB 1080p", targetQuality: "WEB 1080p",
            qualityCutoffMet: true, filePath: path, fileSizeBytes: 4096, cancellationToken: CancellationToken.None);
        return ((await movies.ListAsync(CancellationToken.None)).First(item => item.Title == "Example Film").Id, path);
    }

    [Fact]
    public async Task A_failure_while_clearing_what_is_in_the_way_is_logged_and_the_search_is_still_queued()
    {
        var lines = new Lines();
        FailsOn.Broken = false;
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.AddSingleton<ILoggerProvider>(lines);
            Wrap<IBlockedReleaseRepository, SqliteBlockedReleaseRepository>(services, nameof(IBlockedReleaseRepository.ListForAsync));
        });
        var (movieId, path) = await FilmWithAFileAsync(app);
        FailsOn.Broken = true;
        try
        {
            using var response = await app.Client.PostAsJsonAsync("/api/integrations/external/redownload", new { path, tool = "Weir" });

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            var jobId = body.GetProperty("searchJobId").GetString();
            Assert.False(string.IsNullOrEmpty(jobId));
            var job = await app.Services.GetRequiredService<IJobQueueRepository>().GetAsync(jobId!, CancellationToken.None);
            Assert.Equal("external-redownload", job!.Source);
            Assert.Equal(movieId, job.RelatedEntityId);
            lock (lines.Seen)
            {
                Assert.Contains(lines.Seen, line => line.Level == LogLevel.Warning && line.Message.Contains("Could not clear everything in the way of fetching Example Film again"));
            }
        }
        finally
        {
            FailsOn.Broken = false;
        }
    }

    [Fact]
    public async Task A_search_that_cannot_be_queued_still_answers_202_with_no_job_and_says_to_search_from_the_title_page()
    {
        FailsOn.Broken = false;
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
            Wrap<IJobScheduler, SqliteJobStore>(services, nameof(IJobScheduler.EnqueueAsync)));
        var (_, path) = await FilmWithAFileAsync(app);
        FailsOn.Broken = true;
        try
        {
            using var response = await app.Client.PostAsJsonAsync("/api/integrations/external/redownload", new { path, tool = "Weir" });

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(JsonValueKind.Null, body.GetProperty("searchJobId").ValueKind);
            Assert.True(body.GetProperty("fileRemoved").GetBoolean());
            Assert.Contains("the search could not be queued; search for it from its page", body.GetProperty("message").GetString());
            Assert.False(File.Exists(path));
        }
        finally
        {
            FailsOn.Broken = false;
        }
    }
}
