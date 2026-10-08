/**
 * What an Activity entry's saved details say, as labelled fields (#1151).
 *
 * The details are stored as JSON because each kind of entry keeps different facts. A person never reads the JSON:
 * every fact is shown under a label in plain words, and ones that carry nothing are left out.
 */

export interface DetailField {
  label: string;
  value: string;
  /** Ids, paths and hashes read better in a fixed-width font. */
  mono?: boolean;
}

const KNOWN_LABELS: Record<string, string> = {
  dispatchid: "Download",
  handoffid: "Hand-off",
  releasename: "Release",
  downloadclientid: "Download client",
  downloadclientname: "Download client",
  clientid: "Download client",
  queueitemid: "Item in the client",
  mediatype: "Kind",
  entityid: "Title",
  libraryid: "Library",
  processorname: "Processor",
  failuremessage: "What went wrong",
  importfailuremessage: "What went wrong",
  importfailurecode: "Reason code",
  sourcepath: "Source folder",
  outputpath: "Output folder",
  destinationpath: "Destination",
  attempts: "Attempts",
  actions: "What Deluno did",
  dryrun: "Practice run"
};

const ID_WORDS = /(^id$|Id$|_id$)/;

function humanise(key: string): string {
  const spaced = key
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .replace(/[_\-.]+/g, " ")
    .trim()
    .toLowerCase();
  return spaced ? spaced.charAt(0).toUpperCase() + spaced.slice(1) : key;
}

function labelFor(key: string): string {
  return KNOWN_LABELS[key.replace(/[^a-z]/gi, "").toLowerCase()] ?? humanise(key);
}

function valueText(value: unknown): string | null {
  if (value === null || value === undefined) return null;
  if (typeof value === "string") return value.trim() === "" ? null : value;
  if (typeof value === "boolean") return value ? "Yes" : "No";
  if (typeof value === "number") return String(value);
  if (Array.isArray(value)) {
    const parts = value.map(valueText).filter((part): part is string => part !== null);
    return parts.length ? parts.join("; ") : null;
  }
  return null;
}

function collect(source: Record<string, unknown>, prefix: string, out: DetailField[], depth: number) {
  for (const [key, raw] of Object.entries(source)) {
    const label = prefix ? `${prefix} ${labelFor(key).toLowerCase()}` : labelFor(key);
    if (raw && typeof raw === "object" && !Array.isArray(raw)) {
      if (depth < 2) collect(raw as Record<string, unknown>, label, out, depth + 1);
      continue;
    }
    const text = valueText(raw);
    if (text === null) continue;
    const mono = ID_WORDS.test(key) || /path|hash|folder/i.test(key);
    out.push({ label, value: text, ...(mono ? { mono: true } : {}) });
  }
}

/**
 * The fields to show for a saved-details string. Text that is not JSON is shown as one "Note" field; JSON that
 * holds nothing worth saying gives no fields.
 */
export function detailFields(detailsJson: string | null | undefined): DetailField[] {
  if (!detailsJson || detailsJson.trim() === "") return [];
  let parsed: unknown;
  try {
    parsed = JSON.parse(detailsJson);
  } catch {
    return [{ label: "Note", value: detailsJson }];
  }
  if (parsed === null || typeof parsed !== "object") return [{ label: "Note", value: String(parsed) }];
  if (Array.isArray(parsed)) {
    const text = valueText(parsed);
    return text ? [{ label: "Details", value: text }] : [];
  }
  const fields: DetailField[] = [];
  collect(parsed as Record<string, unknown>, "", fields, 0);
  // The same label and value twice (a client's id and its name) is one fact said twice.
  const seen = new Set<string>();
  return fields.filter((field) => {
    const key = `${field.label}|${field.value}`;
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

/**
 * The words that make an Activity kind a red one. The same list as the server's `ActivityHistory.ProblemWords`
 * (src/Deluno.Contracts/ActivityHistory.cs); a test reads that file and fails if the two drift apart.
 */
export const ACTIVITY_PROBLEM_WORDS = [
  "failed", "failure", "timeout", "timed-out", "unreachable", "unreadable", "stalled", "lost", "vanished", "missing", "files-gone",
  "abandoned", "dead-letter", "lease-expired", "setup-problem", "not-imported", "exhausted", "unmatched", "misplaced", "forbidden",
  "no-progress", "outage.started", "health.problem", "needs-attention", "script-failed", "rejected", "held-by", "given-up",
  "dead-link", "not-reported", "records-failed", "rate-limited", "provider.paused"
] as const;

/** Whether an Activity kind is a red one, decided the way the server decides it. */
export function isProblemActivity(category: string): boolean {
  const lower = category.trim().toLowerCase();
  if (lower.includes("resolved") || lower.includes("recovered") || lower === "library.reachable") return false;
  return ACTIVITY_PROBLEM_WORDS.some((word) => lower.includes(word));
}

/**
 * Where a red entry can be put right: the screen that holds its fix. Every red kind has one, so a new kind
 * cannot be shown as a problem with nowhere to go. Entries that did not fail have none. `outcome` is the
 * server's own verdict when the caller has it.
 */
export function fixLinkForActivity(category: string, outcome?: string | null): { to: string; label: string } | null {
  const lower = category.toLowerCase();
  if (outcome !== "problem" && !isProblemActivity(lower)) return null;
  if (
    lower.startsWith("processing.") || lower.startsWith("download.") || lower.startsWith("import.") || lower.startsWith("handoff")
    || lower.startsWith("filesystem.") || lower.startsWith("job.") || lower.startsWith("processor.")
  ) {
    return { to: "/queue", label: "Fix it in Transfers" };
  }
  if (lower.startsWith("indexer") || lower.startsWith("trackers.")) return { to: "/indexers", label: "Fix it in Find & Download" };
  return { to: "/system", label: "Fix it in System" };
}
