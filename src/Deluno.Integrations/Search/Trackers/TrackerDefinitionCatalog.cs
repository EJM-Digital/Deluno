using System.Collections.Concurrent;
using System.Reflection;
using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Deluno.Integrations.Search.Trackers;

/// <summary>What the tracker picker shows about one definition.</summary>
public sealed record TrackerDefinitionSummary(
    string Id,
    string Name,
    string Description,
    string Language,
    string Privacy,
    IReadOnlyList<string> Links,
    bool Movies,
    bool Tv,
    string? LoginMethod,
    IReadOnlyList<TrackerSettingSummary> Settings,
    bool MayNeedCloudflareHelp,
    string? Unsupported,
    string Origin = TrackerDefinitionOrigins.Deluno);

/// <summary>Where a definition the catalog offers came from, for the picker.</summary>
public static class TrackerDefinitionOrigins
{
    /// <summary>Written by Deluno and kept because the bundled snapshot cannot serve that site as well.</summary>
    public const string Deluno = "deluno";

    /// <summary>The bundled snapshot of Prowlarr's public definitions (#986).</summary>
    public const string Bundled = "bundled";

    /// <summary>A downloaded (#986), newer or hand-added file in the data folder's trackers directory.</summary>
    public const string DataFolder = "data-folder";
}

public sealed record TrackerSettingSummary(
    string Name,
    string Type,
    string Label,
    string? Default,
    bool Secret,
    IReadOnlyList<KeyValuePair<string, string>> Options);

public interface ITrackerDefinitionCatalog
{
    IReadOnlyList<TrackerDefinitionSummary> List();

    TrackerDefinition? Find(string? id);

    /// <summary>
    /// Re-reads the built-in and data-folder definitions from disk (#756):
    /// what the scheduled or manual tracker-definition update calls after
    /// writing a fetched YAML file, so a fresh definition is offered without
    /// restarting Deluno.
    /// </summary>
    void Reload();
}

/// <summary>
/// Every tracker Deluno can search (#606): the definitions built into Deluno,
/// each replaced by a newer copy in the data folder's <c>trackers</c>
/// directory when one is there. A definition that does not load, or uses
/// something the engine cannot do, is never offered.
/// </summary>
public sealed class TrackerDefinitionCatalog : ITrackerDefinitionCatalog
{
    private const string ResourcePrefix = "Deluno.Trackers.";
    private const string BundledPrefix = "Deluno.Cardigann.";

    private readonly string? overrideDirectory;
    private readonly ILogger<TrackerDefinitionCatalog> logger;
    private readonly object gate = new();
    private Loaded? loadedValue;

    public TrackerDefinitionCatalog(IOptions<StoragePathOptions> storage, ILogger<TrackerDefinitionCatalog> logger)
        : this(Path.Combine(Path.GetFullPath(storage.Value.DataRoot), "trackers"), logger)
    {
    }

    public TrackerDefinitionCatalog(string? overrideDirectory, ILogger<TrackerDefinitionCatalog> logger)
    {
        this.overrideDirectory = overrideDirectory;
        this.logger = logger;
    }

    private Loaded Current
    {
        get
        {
            lock (gate)
            {
                return loadedValue ??= Load();
            }
        }
    }

    public IReadOnlyList<TrackerDefinitionSummary> List() => Current.Summaries;

    public TrackerDefinition? Find(string? id)
        => id is not null && Current.Definitions.TryGetValue(id.Trim().ToLowerInvariant(), out var definition) ? definition : null;

    public void Reload()
    {
        lock (gate)
        {
            loadedValue = Load();
        }
    }

    /// <summary>The built-in definitions' YAML, by id.</summary>
    public static IEnumerable<(string Id, string Yaml)> BuiltIn()
    {
        var assembly = typeof(TrackerDefinitionCatalog).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal) && name.EndsWith(".yml", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            yield return (name[ResourcePrefix.Length..^4], reader.ReadToEnd());
        }
    }

    /// <summary>
    /// The bundled snapshot of Prowlarr's public tracker definitions (#986),
    /// by id: hundreds of public, semi-private and private sites, read
    /// unchanged. See the NOTICE resource for where they come from.
    /// </summary>
    public static IEnumerable<(string Id, string Yaml)> Bundled()
    {
        var assembly = typeof(TrackerDefinitionCatalog).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith(BundledPrefix, StringComparison.Ordinal) && name.EndsWith(".yml", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            yield return (name[BundledPrefix.Length..^4], reader.ReadToEnd());
        }
    }

    /// <summary>The words of the notice that travels with the bundled snapshot.</summary>
    public static string BundledNotice()
    {
        using var stream = typeof(TrackerDefinitionCatalog).Assembly.GetManifestResourceStream(BundledPrefix + "NOTICE.md");
        if (stream is null)
        {
            return string.Empty;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private Loaded Load()
    {
        // Later layers replace earlier ones with the same id: the bundled
        // snapshot, then any Deluno-written definition kept for a site the
        // snapshot cannot serve as well, then any file in the data folder (a
        // newer update, or one a person dropped in).
        var sources = new Dictionary<string, (string Yaml, string Origin)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, yaml) in Bundled())
        {
            sources[id] = (yaml, TrackerDefinitionOrigins.Bundled);
        }

        foreach (var (id, yaml) in BuiltIn())
        {
            sources[id] = (yaml, TrackerDefinitionOrigins.Deluno);
        }

        if (overrideDirectory is not null && Directory.Exists(overrideDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(overrideDirectory, "*.yml"))
            {
                string text;
                try
                {
                    text = File.ReadAllText(file);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // One unreadable file must not take the whole catalogue down (#1064).
                    logger.LogWarning("Tracker definition file {File} could not be read and is skipped: {Reason}", file, exception.Message);
                    continue;
                }

                sources[Path.GetFileNameWithoutExtension(file)] = (text, TrackerDefinitionOrigins.DataFolder);
            }
        }

        var definitions = new ConcurrentDictionary<string, TrackerDefinition>(StringComparer.OrdinalIgnoreCase);
        var summaries = new List<TrackerDefinitionSummary>();
        foreach (var (id, (yaml, origin)) in sources)
        {
            TrackerDefinition definition;
            try
            {
                definition = TrackerDefinition.Parse(yaml);
            }
            catch (Exception exception)
            {
                // Listed, not hidden: a person can see the site exists and why it is off.
                logger.LogWarning("Tracker definition {Id} could not be read and is not offered: {Reason}", id, exception.Message);
                summaries.Add(new TrackerDefinitionSummary(
                    id, id, string.Empty, "en-US", "private", [], false, false, null, [], false,
                    "Deluno could not read this definition file.", origin));
                continue;
            }

            var unsupported = TrackerDefinitionValidator.Unsupported(definition);
            summaries.Add(Summarize(definition, unsupported, origin));
            if (unsupported is null)
            {
                definitions[definition.Id.ToLowerInvariant()] = definition;
            }
        }

        summaries.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
        logger.LogInformation("{Count} tracker definitions are ready; {Unsupported} are not offered.", definitions.Count, summaries.Count - definitions.Count);
        return new Loaded(definitions, summaries);
    }

    public static TrackerDefinitionSummary Summarize(TrackerDefinition definition, string? unsupported, string origin = TrackerDefinitionOrigins.Deluno)
    {
        var numbers = definition.Caps.CategoryMappings.Select(mapping => TrackerCategories.NumberOf(mapping.Category)).OfType<int>().ToArray();
        return new TrackerDefinitionSummary(
            definition.Id,
            definition.Name,
            definition.Description,
            definition.Language,
            definition.Type,
            definition.Links,
            Movies: numbers.Any(number => number / 1000 == 2),
            Tv: numbers.Any(number => number / 1000 == 5),
            LoginMethod: definition.Login?.Method,
            Settings: definition.Settings
                .Where(setting => !setting.IsInformational || setting.Type == "info")
                .Select(setting => new TrackerSettingSummary(
                    setting.Name,
                    setting.Type,
                    setting.Label ?? setting.Name,
                    setting.IsSecret ? null : setting.Default,
                    setting.IsSecret,
                    setting.Options))
                // Said in Deluno's words (#1137), not those of the apps the definitions were written for.
                .Select(TrackerSettingWording.Reword)
                .OfType<TrackerSettingSummary>()
                .ToArray(),
            MayNeedCloudflareHelp: definition.Settings.Any(setting => setting.Type == "info_flaresolverr"),
            Unsupported: unsupported,
            Origin: origin);
    }

    private sealed record Loaded(
        IReadOnlyDictionary<string, TrackerDefinition> Definitions,
        IReadOnlyList<TrackerDefinitionSummary> Summaries);
}
