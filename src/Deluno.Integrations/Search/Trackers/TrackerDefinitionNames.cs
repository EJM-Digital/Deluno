using Deluno.Connections.Contracts;

namespace Deluno.Integrations.Search.Trackers;

/// <summary>#1076: an indexer added from a tracker definition is named after the site the definition describes.</summary>
public sealed class TrackerDefinitionNames(ITrackerDefinitionCatalog catalog) : ITrackerDefinitionNames
{
    public string? DisplayName(string definitionId)
    {
        var name = catalog.Find(definitionId)?.Name;
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    /// <summary>#1240: the definition's own type, read the way Prowlarr reads a Cardigann one.</summary>
    public string? Privacy(string definitionId)
        => catalog.Find(definitionId) is { } definition ? IndexerPrivacy.FromDefinitionType(definition.Type) : null;
}
