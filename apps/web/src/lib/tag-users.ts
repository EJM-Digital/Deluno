import type { TagUsageItem } from "./api";

/** Plain words for each kind of thing that can carry a tag (#1039). */
const KIND_LABELS: Record<string, string> = {
  indexer: "Indexer",
  downloadClient: "Download client",
  notification: "Notification",
  sharingRule: "Seeding rule",
  librarySource: "Library source",
  acquisitionRule: "Release rule",
  destinationRule: "Destination rule"
};

export function tagUserKindLabel(kind: string): string {
  return KIND_LABELS[kind] ?? "Setting";
}

/** What happens to one user when its tag is deleted, in plain words. */
export function tagUserDeleteEffect(kind: string): string {
  switch (kind) {
    case "acquisitionRule":
    case "destinationRule":
      return "This rule is deleted, because it only exists for this tag.";
    case "sharingRule":
      return "The tag is removed. If nothing else selects downloads, the rule is switched off.";
    case "indexer":
    case "downloadClient":
    case "notification":
      return "The tag is removed. If it was the only tag, this is then used for every title.";
    default:
      return "The tag is removed.";
  }
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
