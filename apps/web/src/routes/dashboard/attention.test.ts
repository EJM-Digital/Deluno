import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import { computeAttention } from "./attention";
import { emptySummary, fullSummary } from "./summary-fixtures";

const NOW = "2026-09-30T10:00:00Z";
const job = (over: Record<string, unknown>) => ({
  id: "j", jobType: "filesystem.import.execute", status: "failed", attempts: 3, maxAttempts: 3, nextAttemptUtc: null, lastError: "boom", ...over
}) as never;

describe("computeAttention", () => {
  it("is zero everywhere on a new install and with no summary", () => {
    expect(computeAttention(emptySummary()).counts).toEqual({ live: 0, system: 0 });
    expect(computeAttention(undefined).counts).toEqual({ live: 0, system: 0 });
    expect(computeAttention(emptySummary()).total).toBe(0);
  });

  it("counts each problem on one tab only, and the total is the two badges added together", () => {
    const attention = computeAttention(fullSummary(), {}, [{ id: "a", title: "t", text: "x", to: "/", action: "Go" }]);
    const { live, system } = attention.counts;
    expect(attention.total).toBe(live + system);
    expect(attention.total).toBe(attention.items.reduce((sum, item) => sum + item.count, 0));
    for (const tab of ["live", "system"] as const) {
      expect(attention.counts[tab]).toBe(attention.items.filter((item) => item.tab === tab).reduce((sum, item) => sum + item.count, 0));
    }
    // The setup step is a System row with its button.
    expect(attention.items.find((item) => item.id === "setup:a")?.tab).toBe("system");
  });

  it("does not count leftover folders, suggestions, a failed download Deluno is searching again for, or a snoozed problem", () => {
    const summary = emptySummary();
    summary.health = { ...summary.health!, problems: 1, suggestions: 2, topProblems: [{ id: "a", title: "A", category: "system", message: "m", sinceUtc: null, link: "/system", snoozed: true }] };
    const attention = computeAttention(summary, {
      orphans: [{ name: "a" }, { name: "b" }] as never,
      dispatches: [{ id: "d1", status: "failed", releaseName: "R", entityType: "movie", entityId: "m1" }] as never,
      healthSummary: { problemCount: 1, unsnoozedProblemCount: 0, unknownCount: 0, suggestionCount: 2, lastRunUtc: null }
    });
    expect(attention.total).toBe(0);
    expect(attention.items).toHaveLength(0);
    // They stay reachable as one quiet line under Needs you.
    expect(attention.alsoWorthALook).toEqual({ leftovers: 2, suggestions: 2 });
  });

  it("counts a failed import once, on Live, and not again as a job that gave up", () => {
    const attention = computeAttention(fullSummary(), { failedJobs: [job({ id: "imp" }), job({ id: "other", jobType: "library.scan" })] });
    expect(attention.downloads.failedImportJobs.map((j) => j.id)).toEqual(["imp"]);
    expect(attention.jobsGaveUp).toBe(1);
    expect(attention.items.find((item) => item.id === "failed-imports")?.count).toBe(1);
    expect(attention.items.find((item) => item.id === "failed-jobs")?.count).toBe(1);
  });

  it("splits jobs that gave up by where they are cleared: background ones on System, ones about a title in Activity (#1114)", () => {
    const attention = computeAttention(emptySummary(), {
      failedJobs: [
        job({ id: "bg", jobType: "library.scan", relatedEntityId: null }),
        job({ id: "bg2", jobType: "backup.create", relatedEntityId: "" }),
        job({ id: "ti", jobType: "movie.search", relatedEntityType: "movie", relatedEntityId: "m1" })
      ]
    });
    expect(attention.backgroundJobsGaveUp).toBe(2);
    expect(attention.titleJobsGaveUp).toBe(1);
    expect(attention.jobsGaveUp).toBe(3);
    const background = attention.items.find((item) => item.id === "failed-jobs");
    expect(background).toMatchObject({ count: 1, to: "/system" });
    expect(background?.title).toBe("2 background jobs gave up");
    expect(attention.items.find((item) => item.id === "failed-title-jobs")).toMatchObject({ count: 1, to: "/activity?status=failed" });
  });

  it("does not count a job Deluno will retry by itself", () => {
    const retrying = job({ id: "r", jobType: "library.scan", attempts: 1, nextAttemptUtc: NOW });
    const attention = computeAttention(emptySummary(), { failedJobs: [retrying, job({ id: "r2", attempts: 1, nextAttemptUtc: NOW })] });
    expect(attention.jobsGaveUp).toBe(0);
    expect(attention.total).toBe(0);
  });

  it("counts a failed hand-off a recovery case is about once, as the hand-off", () => {
    const handoff = { id: "h1", status: "failed", failureMessage: "no", releaseName: "R" } as never;
    const withCase = { recentCases: [{ id: "c1", detailsJson: JSON.stringify({ HandoffId: "h1" }) }] } as never;
    const attention = computeAttention(emptySummary(), { handoffs: [handoff], movieRecovery: withCase });
    expect(attention.downloads.handoffs).toHaveLength(1);
    expect(attention.downloads.recoveryCases).toHaveLength(0);
    expect(attention.counts.live).toBe(1);
  });

  it("counts a client once however many buttons it has, and a stalled download a case covers only as the case", () => {
    const summary = fullSummary();
    summary.downloads!.clients = [
      { ...summary.downloads!.clients[0], id: "c1", reachable: false, paused: true, warnings: 2 },
      { ...summary.downloads!.clients[0], id: "c2", reachable: true, paused: false, warnings: 0 }
    ];
    const attention = computeAttention(summary, {});
    expect(attention.downloads.clients.map((c) => c.id)).toEqual(["c1"]);
  });

  it("does not count a snoozed health problem, and takes the total from the same summary the sidebar reads", () => {
    const summary = fullSummary();
    summary.health!.topProblems = [
      { id: "a", title: "A", category: "system", message: "m", sinceUtc: null, link: "/system", snoozed: true },
      { id: "b", title: "B", category: "system", message: "m", sinceUtc: null, link: "/system", snoozed: false }
    ];
    summary.health!.problems = 5;
    const attention = computeAttention(summary, { healthSummary: { problemCount: 5, unsnoozedProblemCount: 3, unknownCount: 0, suggestionCount: 0, lastRunUtc: null } });
    expect(attention.items.some((item) => item.id === "health:a")).toBe(false);
    expect(attention.items.find((item) => item.id === "health:b")?.count).toBe(1);
    // Three are not snoozed: one is listed, two more are on Health.
    expect(attention.items.find((item) => item.id === "health:more")?.count).toBe(2);
  });

  describe("one problem is one row", () => {
    const problem = (id: string, title: string) => ({ id, title, category: "x", message: "answered HTTP 504", sinceUtc: null, link: "/indexers/indexers", snoozed: false });
    const indexer = { id: "i1", name: "Nyaa.si", isEnabled: true, healthStatus: "unreachable", lastHealthMessage: "nyaa.si answered HTTP 504" } as never;
    const processor = { id: "p1", name: "Cleaner", isEnabled: true, healthStatus: "unreachable", lastHealthMessage: "no answer" } as never;

    it("counts an indexer that is down once, when it is also a health problem", () => {
      const summary = emptySummary();
      summary.health = { ...summary.health!, problems: 1, topProblems: [problem("indexer:i1", "Nyaa.si (indexer)")] };
      const attention = computeAttention(summary, { indexers: [indexer], healthSummary: { problemCount: 1, unsnoozedProblemCount: 1, unknownCount: 0, suggestionCount: 0, lastRunUtc: null } });
      expect(attention.counts).toEqual({ live: 0, system: 1 });
      expect(attention.items).toHaveLength(1);
      // The one row says which indexer and why, and its button is where Test again is.
      expect(attention.items[0]).toMatchObject({ title: "Nyaa.si isn't answering", text: "nyaa.si answered HTTP 504", action: "Open System" });
      expect(attention.healthListed).toHaveLength(0);
    });

    it("does the same for a download client and a processor, and keeps unrelated health problems", () => {
      const summary = fullSummary();
      const client = summary.downloads!.clients[0];
      summary.downloads!.clients = [{ ...client, id: "c1", reachable: false }];
      summary.health = {
        ...summary.health!,
        problems: 4,
        topProblems: [problem("download-client:c1", "qBit (download client)"), problem("processor:p1", "Cleaner (processor)"), problem("indexer:i1", "Nyaa.si (indexer)"), problem("system:disk", "A drive is nearly full")]
      };
      const lists = { indexers: [indexer], processors: [processor], healthSummary: { problemCount: 4, unsnoozedProblemCount: 4, unknownCount: 0, suggestionCount: 0, lastRunUtc: null } };
      const attention = computeAttention(summary, lists);
      expect(attention.healthListed.map((p) => p.id)).toEqual(["system:disk"]);
      expect(attention.items.filter((item) => item.id.startsWith("health:")).map((item) => item.id)).toEqual(["health:system:disk"]);
      expect(attention.items.find((item) => item.id === "clients")?.count).toBe(1);
      expect(attention.items.find((item) => item.id === "indexers")?.count).toBe(1);
      expect(attention.items.find((item) => item.id === "processors")?.count).toBe(1);
      // Nothing left over as "more": all four are accounted for.
      expect(attention.items.some((item) => item.id === "health:more")).toBe(false);
    });

    it("leaves a health problem alone when its connection has no row of its own", () => {
      const summary = emptySummary();
      summary.health = { ...summary.health!, problems: 1, topProblems: [problem("indexer:i9", "Other (indexer)")] };
      const attention = computeAttention(summary, { indexers: [{ ...(indexer as object), healthStatus: "healthy" } as never] });
      expect(attention.healthListed).toHaveLength(1);
      expect(attention.counts.system).toBe(1);
    });
  });
});

/** docs/attention-categories.json is the one list the tray's tests (tests/Deluno.Tray.Tests) read as well. */
const shared = JSON.parse(readFileSync(resolve(process.cwd(), "../../docs/attention-categories.json"), "utf8")) as {
  categories: { id: string; tray: string | null }[];
};
const listed = (id: string) => shared.categories.some((category) => (category.id.endsWith(":") ? id.startsWith(category.id) : id === category.id));

describe("the bell and the tray count the same kinds of thing", () => {
  it("lists every kind of row the bell can add, so a new kind has to be decided for the tray too", () => {
    const source = readFileSync(resolve(process.cwd(), "src/routes/dashboard/attention.ts"), "utf8");
    const ids = [...source.matchAll(/add\(\{\s*id: [`"]([^`"$]+)/g)].map((match) => match[1]);
    expect(ids.length).toBeGreaterThan(15);
    expect(ids.filter((id) => !listed(id))).toEqual([]);
  });

  it("gives, for one of each kind the tray counts, the number the shared list says the tray adds up to", () => {
    const summary = emptySummary();
    summary.library!.collections.syncFailing = 1;
    summary.downloads = {
      ...summary.downloads!, stalled: 1,
      clients: [{ id: "c1", name: "Client", protocol: "torrent", reachable: false, paused: null, version: null, warnings: 0, freeSpaceBytes: null, healthStatus: "failing", active: 0, queued: 0 }]
    };
    summary.imports = { ...summary.imports!, recoveryOpen: 1, failedImportJobs: 1, failedProcessorHandoffs: 1 };
    summary.automation!.queuePause = { ...summary.automation!.queuePause, enabled: true, unreadable: true, unreadableReason: "unread" };
    summary.indexers = { ...summary.indexers!, failing: 1 };
    summary.processors = { ...summary.processors!, failing: 1 };
    summary.requests = { ...summary.requests!, seerrFailing: 1 };
    summary.health = { ...summary.health!, problems: 1, topProblems: [{ id: "library-root:1", title: "T", category: "storage", message: "m", sinceUtc: null, link: "/system", snoozed: false }] };
    summary.system = { ...summary.system!, internetOutage: true, jobs: { running: 0, queued: 0, failed: 2 }, lanes: [{ name: "Searching", state: "failing", activeJobs: 0, lastError: null }] };
    const noRelease = [{ mediaType: "movie", id: "m1", title: "T", year: 2000, libraryId: "l", lastSearchUtc: NOW, missingSinceUtc: null, reason: null }] as never;

    const attention = computeAttention(summary, { noRelease });

    const countedByTray = shared.categories.filter((category) => category.tray !== null);
    expect(attention.total).toBe(countedByTray.length);
    for (const category of countedByTray) {
      expect(attention.items.some((item) => (category.id.endsWith(":") ? item.id.startsWith(category.id) : item.id === category.id))).toBe(true);
    }
  });
});
