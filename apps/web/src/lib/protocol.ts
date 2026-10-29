/**
 * The one place that tells Usenet from torrent (#1404). Indexers say "newznab"
 * (or "usenet"), clients say "sabnzbd" or "nzbget" (or "usenet"); torrent
 * indexers say "torznab", "tracker" or "rss" and torrent clients name
 * themselves. Screens ask here and never compare protocol strings themselves.
 */
const USENET_PROTOCOLS = new Set(["newznab", "usenet", "sabnzbd", "nzbget"]);

/** True for a Usenet indexer or client protocol, however the case or spacing. */
export function isUsenetProtocol(protocol?: string | null): boolean {
  return USENET_PROTOCOLS.has((protocol ?? "").trim().toLowerCase());
}

/** "usenet" or "torrent" for grouping a connection by what it fetches. */
export function protocolGroup(protocol?: string | null): "usenet" | "torrent" {
  return isUsenetProtocol(protocol) ? "usenet" : "torrent";
}

/** True when a release's lack of seeders is worth a warning: it is not Usenet and reports exactly 0. */
export function hasNoSeeders(protocol: string | null | undefined, seeders: number | null | undefined): boolean {
  return !isUsenetProtocol(protocol) && seeders === 0;
}
