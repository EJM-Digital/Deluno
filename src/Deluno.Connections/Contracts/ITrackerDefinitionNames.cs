namespace Deluno.Connections.Contracts;

/// <summary>
/// The display name of a tracker definition ("1337x"), so an indexer added from a
/// definition is named after its site (#1076). The tracker engine, which
/// Connections does not reference, provides it.
/// </summary>
public interface ITrackerDefinitionNames
{
    string? DisplayName(string definitionId);

    /// <summary>
    /// What a tracker definition says about itself (#1240): <see cref="IndexerPrivacy.Private"/>, <see cref="IndexerPrivacy.Public"/>
    /// or <see cref="IndexerPrivacy.SemiPrivate"/> (Cardigann's own rule: anything that is neither is semi-private). Null when
    /// the definition is not known.
    /// </summary>
    string? Privacy(string definitionId) => null;
}
