using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// What Deluno tells other programs to call matches what the shipped server answers: the Home Assistant recipe's actions reach real
/// routes, the search action works with a key made from the Home Assistant template.
/// </summary>
public sealed class TheDocumentedIntegrationsMatchTheShippedServerTests
{
    [Fact]
    public async Task Every_Home_Assistant_action_in_the_shipped_recipe_calls_a_route_the_server_maps_with_that_method()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var mapped = RouteMap.Read(app.Services).Select(route => $"{route.Method} {NormaliseRoute(route.Pattern)}").ToHashSet(StringComparer.OrdinalIgnoreCase);

        var actions = RecipeActions();

        Assert.True(actions.Count >= 6, $"Only {actions.Count} rest_command actions were read from the recipe.");
        var unknown = actions.Where(action => !mapped.Contains($"{action.Method} {action.Route}")).Select(action => $"{action.Name}: {action.Method} {action.Route}").ToArray();
        Assert.True(unknown.Length == 0, "Recipe actions with no matching route: " + string.Join("; ", unknown));
        Assert.Contains(actions, action => action is { Name: "deluno_search_library", Method: "POST", Route: "/api/libraries/{}/search-now" });
    }

    [Fact]
    public async Task The_recipes_search_action_works_with_a_key_made_from_the_Home_Assistant_template()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "Films")).FullName;
        var library = await app.Client.PostAsJsonAsync("/api/libraries/", new { name = "Films", mediaType = "movies", rootPath = root });
        var libraryId = (await library.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var key = await app.Client.PostAsJsonAsync("/api/api-keys", new { name = "Home Assistant", scopes = "home-assistant" });
        key.EnsureSuccessStatusCode();
        using var homeAssistant = app.CreateAnonymousClient();
        homeAssistant.DefaultRequestHeaders.Add("X-Api-Key", (await key.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/libraries/{libraryId}/search-now") { Content = JsonContent.Create(new { }) };
        using var response = await homeAssistant.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var states = await app.Services.GetRequiredService<IJobQueueRepository>().ListLibraryAutomationStatesAsync(CancellationToken.None);
        Assert.True(states[libraryId].SearchRequested);
    }

    private sealed record RecipeAction(string Name, string Method, string Route);

    private static IReadOnlyList<RecipeAction> RecipeActions()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Deluno.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var recipe = File.ReadAllText(Path.Combine(directory!.FullName, "integrations", "home-assistant", "deluno.yaml"));
        var section = recipe[recipe.IndexOf("rest_command:", StringComparison.Ordinal)..];
        section = section[..section.IndexOf("\nautomation:", StringComparison.Ordinal)];
        var actions = new List<RecipeAction>();
        foreach (Match block in Regex.Matches(section, @"^  (?<name>\w+):\s*\n(?<body>(?:    .*\n?)+)", RegexOptions.Multiline))
        {
            var body = block.Groups["body"].Value;
            var url = Regex.Match(body, @"url:\s*""(?<url>[^""]+)""").Groups["url"].Value;
            var method = Regex.Match(body, @"method:\s*(?<method>\w+)").Groups["method"].Value.ToUpperInvariant();
            // The recipe's own base address and version prefix come off; a Jinja variable becomes the route's own placeholder.
            var path = Regex.Replace(url, @"^\{\{ states\('input_text\.deluno_api_url'\) \}\}/api/v1", "/api");
            path = Regex.Replace(path, @"\{\{ \w+ \}\}", "{id}");
            actions.Add(new RecipeAction(block.Groups["name"].Value, method, path));
        }

        return actions.Select(action => action with { Route = NormaliseRoute(action.Route) }).ToArray();
    }

    /// <summary>The route patterns name their placeholder differently from one route to the next; only their position matters here.</summary>
    private static string NormaliseRoute(string route) => Regex.Replace(route.TrimEnd('/'), @"{[^}]+}", "{}");
}
