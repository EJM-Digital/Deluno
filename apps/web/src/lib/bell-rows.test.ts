import { describe, expect, it } from "vitest";
import { computeAttention } from "../routes/dashboard/attention";
import { emptySummary, fullSummary } from "../routes/dashboard/summary-fixtures";
import { ALERT_LABEL_MAX } from "./alert-actions";
import { bellRows, OLD_AFTER_MS, PILE_FROM } from "./bell-rows";

const NOW = Date.parse("2026-10-01T09:00:00Z");
const hoursAgo = (hours: number) => new Date(NOW - hours * 3_600_000).toISOString();

function stuck(id: string, hours: number, over: Record<string, unknown> = {}) {
  return {
    id,
    clientId: "qb",
    clientName: "qBittorrent",
    title: `Show ${id}`,
    releaseName: `Show.${id}.1080p`,
    status: "stalled",
    errorMessage: null,
    healthFindings: [{ kind: "stalled", summary: "Not moving for 2 days." }],
    addedUtc: hoursAgo(hours),
    ...over
  };
}

function attentionWith(lists: Record<string, unknown>, summary = emptySummary()) {
  return computeAttention(summary, lists as never);
}

describe("the bell's rows are two sections: your media and Deluno (#1098)", () => {
  it("is empty when nothing needs you", () => {
    expect(bellRows(attentionWith({}), NOW)).toEqual({ media: [], deluno: [] });
  });

  it("names each stuck download on its own row with the reason, and gives it Find another", () => {
    const rows = bellRows(attentionWith({ telemetry: { clients: [{ queue: [stuck("a", 2), stuck("b", 3, { status: "downloading", healthFindings: null, errorMessage: "Disk full on the client" })] }] } }), NOW);

    expect(rows.deluno).toEqual([]);
    expect(rows.media.map((row) => row.title)).toEqual(["Show a", "Show b"]);
    const [first, second] = rows.media;
    expect(first.text).toBe("Not moving for 2 days.");
    expect(second.text).toBe("Disk full on the client");
    expect(first.actions[0].label).toBe("Find another");
    expect(first.actions[0].hint).toMatch(/blocklists this release/);
  });

  it("puts failed imports, refused hand-offs and titles with no acceptable release in Your media", () => {
    const rows = bellRows(
      attentionWith({
        failedJobs: [{ id: "j1", jobType: "filesystem.import.execute", status: "failed", lastError: "Disk was full", attempts: 3, maxAttempts: 3, nextAttemptUtc: null, createdUtc: hoursAgo(1) }],
        handoffs: [{ id: "h1", releaseName: "Metropolis (1927)", status: "failed", failureMessage: "The processor timed out.", processorName: "Cleaner", updatedUtc: hoursAgo(1) }],
        noRelease: [{ mediaType: "movie", id: "m1", title: "Obscure Film", year: 1971, libraryId: "lib", lastSearchUtc: hoursAgo(1), missingSinceUtc: hoursAgo(2), reason: null }]
      }),
      NOW
    );

    expect(rows.media.map((row) => row.kind).sort()).toEqual(["handoff", "import", "no-release"]);
    const noRelease = rows.media.find((row) => row.kind === "no-release")!;
    expect(noRelease.title).toBe("Obscure Film (1971)");
    expect(noRelease.actions.map((action) => action.label)).toEqual(["Search now", "Dismiss"]);
    expect(rows.media.find((row) => row.kind === "import")!.actions.map((action) => action.label)).toEqual(["Try again", "Dismiss"]);
    expect(rows.media.find((row) => row.kind === "handoff")!.actions.map((action) => action.label)).toEqual(["Retry", "Dismiss"]);
  });

  it("puts a connection down, a Machine problem and an update in Deluno, linking to System's Health", () => {
    const summary = fullSummary();
    summary.system = { ...summary.system!, updateAvailable: true };
    summary.health = {
      ...summary.health!,
      problems: 2,
      topProblems: [
        { id: "machine:sleep", title: "Computer sleep", category: "machine", message: "This computer goes to sleep after 30 minutes on mains power.", sinceUtc: hoursAgo(1), link: "/system", snoozed: false },
        { id: "machine:drive:WD Red", title: "Drive health: WD Red", category: "machine", message: "WD Red is reporting a health warning.", sinceUtc: hoursAgo(1), link: "/system", snoozed: false }
      ]
    };
    const rows = bellRows(attentionWith({ indexers: [{ id: "ix1", name: "NZBgeek", isEnabled: true, healthStatus: "unreachable", lastHealthMessage: "Timed out." }] }, summary), NOW);

    expect(rows.media).toEqual([]);
    const byKey = Object.fromEntries(rows.deluno.map((row) => [row.key, row]));
    expect(byKey["indexer:ix1"].actions.map((action) => action.label)).toEqual(["Test again", "Switch off"]);
    expect(byKey["indexer:ix1"].text).toBe("Timed out.");
    expect(byKey["health:machine:sleep"].actions.map((action) => action.label)).toEqual(["Keep awake", "Snooze 1 day"]);
    expect(byKey["health:machine:drive:WD Red"].actions.map((action) => action.label)).toEqual(["Back up now", "Snooze 1 day"]);
    expect(byKey.update.actions.map((action) => action.label)).toEqual(["Open Updates"]);
    // Deluno rows link to System's Health.
    for (const row of rows.deluno.filter((item) => item.key.startsWith("indexer:") || item.key.startsWith("health:"))) expect(row.to).toBe("/system");
  });

  it("every row has one or two buttons, each with a hover note and a label that fits one width", () => {
    const summary = fullSummary();
    summary.health = { ...summary.health!, problems: 1, topProblems: [{ id: "system:disk-space", title: "Deluno’s own storage", category: "system", message: "Only 4% free.", sinceUtc: null, link: "/system", snoozed: false }] };
    const rows = bellRows(
      attentionWith({
        telemetry: { clients: [{ queue: [stuck("a", 1)] }] },
        failedJobs: [{ id: "j1", jobType: "filesystem.import.execute", status: "failed", lastError: "x", attempts: 3, maxAttempts: 3, nextAttemptUtc: null, createdUtc: hoursAgo(1) }],
        indexers: [{ id: "ix1", name: "NZBgeek", isEnabled: true, healthStatus: "unreachable" }],
        noRelease: [{ mediaType: "series", id: "s1", title: "Some Show", year: null, libraryId: "lib", lastSearchUtc: hoursAgo(1), missingSinceUtc: null, reason: null }]
      }, summary),
      NOW
    );

    const all = [...rows.media, ...rows.deluno];
    expect(all.length).toBeGreaterThan(4);
    for (const row of all) {
      expect(row.actions.length === 1 || row.actions.length === 2).toBe(true);
      for (const action of row.actions) {
        expect(action.hint.length).toBeGreaterThan(10);
        expect(action.label.length).toBeLessThanOrEqual(ALERT_LABEL_MAX + 1);
        expect(Boolean(action.request) !== Boolean(action.to)).toBe(true);
      }
    }
    // "Give up" is Dismiss, everywhere.
    expect(all.flatMap((row) => row.actions).some((action) => /give up/i.test(action.label))).toBe(false);
  });
});

describe("a pile of the same old problem is one row; a new one is its own row (#1098)", () => {
  it("groups three or more stuck downloads that have stood for over a day, and leaves the new one apart", () => {
    const queue = [stuck("old1", 30), stuck("old2", 48), stuck("old3", 100), stuck("fresh", 1)];
    const rows = bellRows(attentionWith({ telemetry: { clients: [{ queue }] } }), NOW);

    expect(rows.media).toHaveLength(2);
    const pile = rows.media.find((row) => row.key === "pile:stuck")!;
    expect(pile.title).toBe("3 downloads have been stuck for over a day");
    expect(pile.count).toBe(3);
    expect(pile.actions.map((action) => action.label)).toEqual(["Open Transfers"]);
    expect(rows.media.find((row) => row.title === "Show fresh")).toBeDefined();
  });

  it("keeps old ones separate while there are fewer than the pile size, and a problem with no time is new", () => {
    expect(PILE_FROM).toBe(3);
    const two = bellRows(attentionWith({ telemetry: { clients: [{ queue: [stuck("o1", 30), stuck("o2", 40)] }] } }), NOW);
    expect(two.media.map((row) => row.title)).toEqual(["Show o1", "Show o2"]);

    const untimed = bellRows(attentionWith({ telemetry: { clients: [{ queue: [stuck("a", 0, { addedUtc: null }), stuck("b", 0, { addedUtc: null }), stuck("c", 0, { addedUtc: null })] }] } }), NOW);
    expect(untimed.media).toHaveLength(3);
    expect(OLD_AFTER_MS).toBe(24 * 3_600_000);
  });

  it("groups old health problems of one category in Deluno, and keeps a new one on its own row", () => {
    const summary = fullSummary();
    const problem = (id: string, hours: number) => ({ id, title: id, category: "indexer", message: "m", sinceUtc: hoursAgo(hours), link: "/system", snoozed: false });
    summary.health = { ...summary.health!, problems: 4, topProblems: [problem("a", 40), problem("b", 50), problem("c", 60), problem("d", 2)] };
    const rows = bellRows(attentionWith({}, summary), NOW);

    const pile = rows.deluno.find((row) => row.key === "pile:health:indexer")!;
    expect(pile.title).toBe("3 indexer problems for over a day");
    expect(rows.deluno.find((row) => row.key === "health:d")).toBeDefined();
    expect(rows.deluno.some((row) => row.key === "health:a")).toBe(false);
  });

  it("gives two rows about the same title different accessible names", () => {
    const rows = bellRows(attentionWith({ telemetry: { clients: [{ queue: [stuck("x", 1, { title: "Same Title" }), stuck("y", 1, { title: "Same Title" })] }] } }), NOW);
    expect(new Set(rows.media.map((row) => row.subject ?? row.title)).size).toBe(2);
  });
});
