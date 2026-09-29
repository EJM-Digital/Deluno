namespace Deluno.Connections.Contracts;

/// <summary>
/// The display name of a tracker definition ("1337x"), so an indexer added from a
/// definition is named after its site (#1076). The tracker engine, which
/// Connections does not reference, provides it.
/// </summary>
public interface ITrackerDefinitionNames
{
    string? DisplayName(string definitionId);
}
