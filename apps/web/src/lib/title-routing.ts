/**
 * #990: says, in plain words, which of a title's linked indexers and
 * download clients Deluno will actually use for it, and why — mirroring the
 * backend's tag-routing rule (Deluno.Contracts.TagRoutingMatch / #808):
 * an untagged connection is used for any title; a tagged connection is used
 * only for a title sharing one of its tags.
 */

export interface TitleRoutingConnection {
  id: string;
  name: string;
  /** Comma-separated tag names on the connection itself (indexer/client Tags field). Empty/undefined means untagged. */
  tags?: string | null;
  /**
   * Download clients only: what the client can be handed ("usenet" or
   * "torrent"). A client tagged for the title is chosen before untagged
   * clients of the same kind (Radarr/Sonarr's rule, #1041).
   */
  group?: string | null;
}

export interface TitleRoutingEntry {
  id: string;
  name: string;
  used: boolean;
  reason: string;
}

function splitTags(csv: string | null | undefined): string[] {
  return (csv ?? "")
    .split(",")
    .map((tag) => tag.trim())
    .filter(Boolean);
}

/**
 * One connection kind (indexers, or download clients) linked to a title's
 * library, resolved against the title's own tags.
 */
export function describeTitleRouting(
  connections: readonly TitleRoutingConnection[],
  titleTags: readonly string[]
): TitleRoutingEntry[] {
  const held = new Set(titleTags.map((tag) => tag.toLowerCase()));

  const matchedTagOf = (connection: TitleRoutingConnection) =>
    splitTags(connection.tags).find((tag) => held.has(tag.toLowerCase()));
  const groupsWithTaggedMatch = new Set(
    connections
      .filter((connection) => connection.group != null && matchedTagOf(connection))
      .map((connection) => connection.group)
  );

  return connections.map((connection) => {
    const required = splitTags(connection.tags);
    if (required.length === 0) {
      if (connection.group != null && groupsWithTaggedMatch.has(connection.group)) {
        return {
          id: connection.id,
          name: connection.name,
          used: false,
          reason: `${connection.name} has no tags. A client tagged for this title is chosen first, so it only steps in if that one is unavailable.`
        };
      }
      return {
        id: connection.id,
        name: connection.name,
        used: true,
        reason: `${connection.name} has no tags, so it is used for any title.`
      };
    }

    const matched = matchedTagOf(connection);
    if (matched) {
      return {
        id: connection.id,
        name: connection.name,
        used: true,
        reason:
          connection.group != null
            ? `${connection.name} is tagged "${matched}" like this title, so it is chosen before clients with no tag.`
            : `${connection.name} only takes titles tagged "${matched}", and this one is.`
      };
    }

    const tagList = required.map((tag) => `"${tag}"`).join(" or ");
    return {
      id: connection.id,
      name: connection.name,
      used: false,
      reason: `${connection.name} only takes titles tagged ${tagList}, and this title is not.`
    };
  });
}
