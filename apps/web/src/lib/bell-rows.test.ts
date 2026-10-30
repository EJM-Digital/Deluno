import { describe, expect, it } from "vitest";
import { ALERT_LABEL_MAX } from "./alert-actions";
import { serverRow, stuckRow, summaryOf } from "./attention-summary-fixtures";
import { bellRows, OLD_AFTER_MS, PILE_FROM } from "./bell-rows";

const NOW = Date.parse("2026-10-01T09:00:00Z");
const hoursAgo = (hours: number) => new Date(NOW - hours * 3_600_000).toISOString();

describe("the bell's rows are two sections: your media and Deluno, drawn from the server's rows (#1098, #1185)", () => {
  it("is empty when nothing needs you", () => {
    expect(bellRows(summaryOf([]), NOW)).toEqual({ media: [], deluno: [], updates: [] });
  });

  it("draws each stuck download on its own row with the server's reason, and gives it Find another", () => {
    const rows = bellRows(
      summaryOf([
        stuckRow("a", 2),
        stuckRow("b", 3, { tone: "bad", text: "Disk full on the client", data: { id: "b", clientId: "qb", clientName: "qBittorrent", title: "Show b", releaseName: "Show.b", status: "downloading", errorMessage: "Disk full on the client", healthFindings: null } })
      ]),
      NOW
    );

    expect(rows.deluno).toEqual([]);
    expect(rows.media.map((row) => row.title)).toEqual(["Show a", "Show b"]);
    const [first, second] = rows.media;
    expect(first.text).toBe("Not moving for 2 days.");
    expect(second.text).toBe("Disk full on the client");
    expect(first.actions[0].label).toBe("Find another");
    expect(first.actions[0].hint).toMatch(/blocklists this release/);
  });

  it("puts failed imports, refused hand-offs and titles with no acceptable release in Your media, each with its own buttons", () => {
    const rows = bellRows(
      summaryOf([
        serverRow({ id: "import:j1", kind: "import", section: "media", tone: "bad", title: "An import didn’t finish", to: "/queue", data: { id: "j1" } }),
        serverRow({ id: "handoff:h1", kind: "handoff", section: "media", tone: "bad", title: "Metropolis (1927)", to: "/queue", data: { id: "h1", releaseName: "Metropolis (1927)", status: "failed", outputPath: null, importJobId: null, outcome: null } }),
        serverRow({
          id: "no-release:movie:m1", kind: "no-release", section: "media", title: "Obscure Film (1971)", to: "/movies/m1",
          data: { mediaType: "movie", id: "m1", title: "Obscure Film", year: 1971, libraryId: "lib", lastSearchUtc: hoursAgo(1), missingSinceUtc: hoursAgo(2), reason: null }
        })
      ]),
      NOW
    );

    expect(rows.media.map((row) => row.kind).sort()).toEqual(["handoff", "import", "no-release"]);
    expect(rows.media.find((row) => row.kind === "no-release")!.actions.map((action) => action.label)).toEqual(["Search now", "Dismiss"]);
    expect(rows.media.find((row) => row.kind === "import")!.actions.map((action) => action.label)).toEqual(["Try again", "Dismiss"]);
    expect(rows.media.find((row) => row.kind === "handoff")!.actions.map((action) => action.label)).toEqual(["Retry", "Dismiss"]);
  });

  it("puts a connection down and a Machine problem in Deluno, linking to System's Health", () => {
    const rows = bellRows(
      summaryOf([
        serverRow({ id: "indexer:ix1", kind: "connection", title: "NZBgeek isn’t answering", text: "Timed out.", data: { id: "ix1", name: "NZBgeek" } }),
        serverRow({ id: "health:machine:sleep", kind: "health:machine", title: "Computer sleep", data: { id: "machine:sleep", category: "machine", name: "Computer sleep", link: "/system" } }),
        serverRow({ id: "health:machine:drive:WD Red", kind: "health:machine", title: "Drive health: WD Red", data: { id: "machine:drive:WD Red", category: "machine", name: "Drive health: WD Red", link: "/system" } })
      ]),
      NOW
    );

    expect(rows.media).toEqual([]);
    const byKey = Object.fromEntries(rows.deluno.map((row) => [row.key, row]));
    expect(byKey["indexer:ix1"].actions.map((action) => action.label)).toEqual(["Test again", "Switch off"]);
    expect(byKey["indexer:ix1"].text).toBe("Timed out.");
    expect(byKey["health:machine:sleep"].actions.map((action) => action.label)).toEqual(["Keep awake", "Snooze 1 day"]);
    expect(byKey["health:machine:drive:WD Red"].actions.map((action) => action.label)).toEqual(["Back up now", "Snooze 1 day"]);
    for (const row of rows.deluno) expect(row.to).toBe("/system");
  });

  it("lists a ready update in its own group, apart from the problems and never counted with them", () => {
    const summary = summaryOf(
      [serverRow({ id: "outage", kind: "outage", tone: "bad", title: "Deluno can't reach the internet" })],
      [
        serverRow({ id: "update", kind: "update", title: "A new version of Deluno is ready", to: "/system/updates", action: "Open Updates" }),
        serverRow({ id: "connection-update:downloadClient:qb1", kind: "connection-update", title: "qBittorrent 5.0.2 is out · you have 4.6.7", to: "/indexers/download-clients?connection=qb1", data: { kind: "downloadClient", id: "qb1" } })
      ]
    );

    const rows = bellRows(summary, NOW);

    expect(summary.count).toBe(1);
    expect(rows.deluno.map((row) => row.key)).toEqual(["outage"]);
    expect(rows.updates.map((row) => row.key)).toEqual(["update", "connection-update:downloadClient:qb1"]);
    expect(rows.updates.every((row) => row.section === "updates")).toBe(true);
    expect(rows.updates[0].actions.map((action) => action.label)).toEqual(["Open Updates"]);
    expect(rows.updates[1].actions.map((action) => action.label)).toEqual(["Open", "Dismiss"]);
    expect(rows.updates[1].actions[1]?.request?.path).toBe("/api/connections/downloadClient/qb1/version/dismiss");
  });

  it("every row has one or two buttons, each with a hover note and a label that fits one width", () => {
    const rows = bellRows(
      summaryOf([
        stuckRow("a", 1),
        serverRow({ id: "import:j1", kind: "import", section: "media", tone: "bad", data: { id: "j1" } }),
        serverRow({ id: "indexer:ix1", kind: "connection", data: { id: "ix1", name: "NZBgeek" } }),
        serverRow({ id: "client:c1", kind: "connection", data: { id: "c1", name: "SABnzbd" } }),
        serverRow({ id: "processor:p1", kind: "connection", data: { id: "p1", name: "Weir" } }),
        serverRow({ id: "health:system:disk-space", kind: "health:system", data: { id: "system:disk-space", category: "system", name: "Deluno’s own storage", link: "/system" } }),
        serverRow({ id: "failed-jobs", kind: "jobs", tone: "bad" }),
        serverRow({ id: "failed-title-jobs", kind: "jobs", tone: "bad", to: "/activity?status=failed" }),
        serverRow({ id: "outage", kind: "outage", tone: "bad" }),
        serverRow({ id: "collections", kind: "collections", to: "/collections", action: "Collections" })
      ]),
      NOW
    );

    const all = [...rows.media, ...rows.deluno];
    expect(all).toHaveLength(10);
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
    const rows = bellRows(summaryOf([stuckRow("old1", 30), stuckRow("old2", 48), stuckRow("old3", 100), stuckRow("fresh", 1)]), NOW);

    expect(rows.media).toHaveLength(2);
    const pile = rows.media.find((row) => row.key === "pile:stuck")!;
    expect(pile.title).toBe("3 downloads have been stuck for over a day");
    expect(pile.count).toBe(3);
    expect(pile.actions.map((action) => action.label)).toEqual(["Open In progress"]);
    expect(rows.media.find((row) => row.title === "Show fresh")).toBeDefined();
  });

  it("keeps old ones separate while there are fewer than the pile size, and a problem with no time is new", () => {
    expect(PILE_FROM).toBe(3);
    const two = bellRows(summaryOf([stuckRow("o1", 30), stuckRow("o2", 40)]), NOW);
    expect(two.media.map((row) => row.title)).toEqual(["Show o1", "Show o2"]);

    const untimed = bellRows(summaryOf([stuckRow("a", 0, { sinceUtc: null }), stuckRow("b", 0, { sinceUtc: null }), stuckRow("c", 0, { sinceUtc: null })]), NOW);
    expect(untimed.media).toHaveLength(3);
    expect(OLD_AFTER_MS).toBe(24 * 3_600_000);
  });

  it("groups old health problems of one category in Deluno, and keeps a new one on its own row", () => {
    const problem = (id: string, hours: number) =>
      serverRow({ id: `health:${id}`, kind: "health:indexer", title: id, sinceUtc: hoursAgo(hours), data: { id, category: "indexer", name: id, link: "/indexers/indexers" } });
    const rows = bellRows(summaryOf([problem("a", 40), problem("b", 50), problem("c", 60), problem("d", 2)]), NOW);

    const pile = rows.deluno.find((row) => row.key === "pile:health:indexer")!;
    expect(pile.title).toBe("3 indexer problems for over a day");
    expect(rows.deluno.find((row) => row.key === "health:d")).toBeDefined();
    expect(rows.deluno.some((row) => row.key === "health:a")).toBe(false);
  });

  it("gives two rows about the same title different accessible names", () => {
    const rows = bellRows(summaryOf([stuckRow("x", 1, { title: "Same Title" }), stuckRow("y", 1, { title: "Same Title" })]), NOW);
    expect(new Set(rows.media.map((row) => row.subject ?? row.title)).size).toBe(2);
  });

  it("a pile keeps the number the server gave: the badge is the server's count, not a sum drawn from the piles", () => {
    const summary = summaryOf([stuckRow("old1", 30), stuckRow("old2", 48), stuckRow("old3", 100)]);
    expect(bellRows(summary, NOW).media).toHaveLength(1);
    expect(summary.count).toBe(3);
  });
});
