using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The outside-automation bulk add: when the catalogue throws for one title, that title comes back failed with the error,
/// the others in the same call are still created, and the counts add up.
/// </summary>
public sealed class AFailingTitleInABulkAddFailsAloneTests
{
    /// <summary>The real movie catalogue, except that adding the one named film throws.</summary>
    public class ThrowsForOneTitle : DispatchProxy
    {
        public IMovieCatalogRepository Inner { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IMovieCatalogRepository.AddAsync) && args![0] is CreateMovieRequest { Title: "Broken Film" })
            {
                return Task.FromException<MovieListItem>(new InvalidOperationException("The catalogue could not write Broken Film."));
            }

            try
            {
                return targetMethod.Invoke(Inner, args);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                throw exception.InnerException;
            }
        }
    }

    [Fact]
    public async Task The_title_the_catalogue_cannot_write_is_reported_failed_with_the_error_and_the_others_are_created()
    {
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            for (var i = services.Count - 1; i >= 0; i--)
            {
                if (services[i].ServiceType == typeof(IMovieCatalogRepository)) services.RemoveAt(i);
            }

            services.AddSingleton<SqliteMovieCatalogRepository>();
            services.AddSingleton<IMovieCatalogRepository>(provider =>
            {
                var proxy = DispatchProxy.Create<IMovieCatalogRepository, ThrowsForOneTitle>();
                ((ThrowsForOneTitle)(object)proxy).Inner = provider.GetRequiredService<SqliteMovieCatalogRepository>();
                return proxy;
            });
        });
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        using var library = await app.Client.PostAsJsonAsync("/api/libraries/", new { name = "Films", mediaType = "movies", purpose = "collection", rootPath = root, importWorkflow = "copy" });
        Assert.True(library.IsSuccessStatusCode, await library.Content.ReadAsStringAsync());

        using var response = await app.Client.PostAsJsonAsync("/api/automation/catalogue/bulk", new
        {
            items = new object[]
            {
                new { clientItemId = "a", mediaType = "movie", title = "First Film", year = 2001 },
                new { clientItemId = "b", mediaType = "movie", title = "Broken Film", year = 2002 },
                new { clientItemId = "c", mediaType = "movie", title = "Third Film", year = 2003 }
            }
        });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToDictionary(item => item.GetProperty("clientItemId").GetString()!);
        Assert.Equal("created", items["a"].GetProperty("status").GetString());
        Assert.Equal("failed", items["b"].GetProperty("status").GetString());
        Assert.Equal("The catalogue could not write Broken Film.", items["b"].GetProperty("error").GetString());
        Assert.Equal("created", items["c"].GetProperty("status").GetString());
        Assert.Equal(2, body.GetProperty("createdCount").GetInt32());
        Assert.Equal(1, body.GetProperty("failedCount").GetInt32());
        Assert.Equal(2, await ApiScenes.ScalarAsync(app, "movies", "SELECT COUNT(*) FROM movie_entries;"));
    }
}
