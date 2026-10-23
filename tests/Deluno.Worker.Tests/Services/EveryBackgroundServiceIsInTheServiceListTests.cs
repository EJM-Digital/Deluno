using System.Reflection;
using Microsoft.Extensions.Hosting;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// docs/background-services.md says it lists every BackgroundService and IHostedService in Deluno and what each does when something goes
/// wrong (coverage register, engines). It only means anything if it is true, so every service the server builds is found by name in it;
/// a new service goes in the list in the same change. Refs #1296.
/// </summary>
public sealed class EveryBackgroundServiceIsInTheServiceListTests
{
    [Fact]
    public void Every_hosted_service_in_the_server_is_named_in_the_service_list()
    {
        var doc = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "background-services.md"));
        var services = AppDomain.CurrentDomain.GetAssemblies()
            .Concat(Directory.EnumerateFiles(AppContext.BaseDirectory, "Deluno.*.dll").Select(TryLoad).OfType<Assembly>())
            .Where(assembly => assembly.GetName().Name is { } name && name.StartsWith("Deluno.", StringComparison.Ordinal) && !name.EndsWith(".Tests", StringComparison.Ordinal))
            .Distinct()
            .SelectMany(Types)
            .Where(type => type is { IsAbstract: false, IsClass: true } && typeof(IHostedService).IsAssignableFrom(type) && !type.IsNested)
            .Select(type => type.Name)
            .Distinct()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(services.Length > 30, $"Only {services.Length} hosted services were found; the scan is broken.");
        var missing = services.Where(name => !doc.Contains($"`{name}`", StringComparison.Ordinal)).ToArray();
        Assert.True(missing.Length == 0, "Not named in docs/background-services.md: " + string.Join(", ", missing));
    }

    private static Assembly? TryLoad(string path)
    {
        try
        {
            return Assembly.LoadFrom(path);
        }
        catch (Exception exception) when (exception is BadImageFormatException or FileLoadException or IOException)
        {
            return null;
        }
    }

    private static IEnumerable<Type> Types(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.OfType<Type>();
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Deluno.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
