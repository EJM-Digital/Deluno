import type { TagUsageItem } from "./api";

/** Plain words for each kind of thing that can carry a tag (#1039). */
const KIND_LABELS: Record<string, string> = {
  indexer: "Indexer",
  downloadClient: "Download client",
  notification: "Notification",
  sharingRule: "Seeding rule",
  librarySource: "Library source",
  acquisitionRule: "Release rule",
  destinationRule: "Destination rule",
  seerrConnection: "Seerr connection",
  list: "List",
  collection: "Collection",
  listSetting: "List setting",
  autoTagRule: "Auto-tagging rule",
  retentionRule: "Retention rule"
};

export function tagUserKindLabel(kind: string): string {
  return KIND_LABELS[kind] ?? "Setting";
}

const KIND_NOUNS: Record<string, [string, string]> = {
  indexer: ["indexer", "indexers"],
  downloadClient: ["download client", "download clients"],
  notification: ["notification", "notifications"],
  sharingRule: ["seeding rule", "seeding rules"],
  librarySource: ["library source rule", "library source rules"],
  acquisitionRule: ["release rule", "release rules"],
  destinationRule: ["destination rule", "destination rules"],
  seerrConnection: ["Seerr connection", "Seerr connections"],
  list: ["list", "lists"],
  collection: ["collection", "collections"],
  listSetting: ["list setting", "list settings"],
  autoTagRule: ["auto-tagging rule", "auto-tagging rules"],
  retentionRule: ["retention rule", "retention rules"]
};

/** "3 titles and 1 download client": what still uses a tag, in plain words. */
export function describeTagInUse(usage: TagUsageItem): string {
  const parts: string[] = [];
  if (usage.totalCount > 0) parts.push(`${usage.totalCount} ${usage.totalCount === 1 ? "title" : "titles"}`);
  const counts = new Map<string, number>();
  for (const user of usage.users) counts.set(user.kind, (counts.get(user.kind) ?? 0) + 1);
  for (const [kind, count] of counts) {
    const [one, many] = KIND_NOUNS[kind] ?? ["setting", "settings"];
    parts.push(`${count} ${count === 1 ? one : many}`);
  }
  if (parts.length <= 1) return parts[0] ?? "nothing";
  return `${parts.slice(0, -1).join(", ")} and ${parts[parts.length - 1]}`;
}

/** "3 titles, 2 settings", or "Not used". */
export function describeTagUsage(usage: TagUsageItem | undefined): string {
  if (!usage || usage.isUnused) return "Not used";
  const parts: string[] = [];
  if (usage.totalCount > 0) parts.push(`${usage.totalCount} title${usage.totalCount === 1 ? "" : "s"}`);
  const others = usage.users.length;
  if (others > 0) parts.push(`${others} ${others === 1 ? "setting" : "settings"}`);
  return parts.join(", ");
}

export function unusedTags<T extends { id: string }>(tags: T[], usage: TagUsageItem[]): T[] {
  const unused = new Set(usage.filter((item) => item.isUnused).map((item) => item.id));
  return tags.filter((tag) => unused.has(tag.id));
}
