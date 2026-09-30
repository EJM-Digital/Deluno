import { describe, expect, it } from "vitest";
import { computeAttention } from "./attention";
import { emptySummary, fullSummary } from "./summary-fixtures";

const NOW = "2026-09-30T10:00:00Z";
const job = (over: Record<string, unknown>) => ({
  id: "j", jobType: "filesystem.import.execute", status: "failed", attempts: 3, maxAttempts: 3, nextAttemptUtc: null, lastError: "boom", ...over
}) as never;

describe("computeAttention", () => {
  it("is zero everywhere on a new install and with no summary", () => {
    expect(computeAttention(emptySummary()).counts).toEqual({ overview: 0, library: 0, downloads: 0, system: 0 });
    expect(computeAttention(undefined).counts).toEqual({ overview: 0, library: 0, downloads: 0, system: 0 });
  });

  it("makes the Overview the sum of the tabs plus what only it holds", () => {
    const attention = computeAttention(fullSummary(), {}, [{ id: "a", title: "t", text: "x", to: "/", action: "Go" }]);
    const { overview, library, downloads, system } = attention.counts;
    const overviewOnly = attention.items.filter((item) => item.tab === "overview").reduce((sum, item) => sum + item.count, 0);
    expect(overview).toBe(library + downloads + system + overviewOnly);
    expect(overview).toBe(attention.items.reduce((sum, item) => sum + item.count, 0));
    expect(overviewOnly).toBeGreaterThanOrEqual(1);
  });

  it("counts a failed import once, on Downloads, and not again as a job that gave up", () => {
    const attention = computeAttention(fullSummary(), { failedJobs: [job({ id: "imp" }), job({ id: "other", jobType: "library.scan" })] });
    expect(attention.downloads.failedImportJobs.map((j) => j.id)).toEqual(["imp"]);
    expect(attention.jobsGaveUp).toBe(1);
    expect(attention.items.find((item) => item.id === "failed-imports")?.count).toBe(1);
    expect(attention.items.find((item) => item.id === "failed-jobs")?.count).toBe(1);
  });

  it("does not count a job Deluno will retry by itself", () => {
    const retrying = job({ id: "r", jobType: "library.scan", attempts: 1, nextAttemptUtc: NOW });
    const attention = computeAttention(emptySummary(), { failedJobs: [retrying, job({ id: "r2", attempts: 1, nextAttemptUtc: NOW })] });
    expect(attention.jobsGaveUp).toBe(0);
    expect(attention.counts.overview).toBe(0);
  });

  it("counts a failed hand-off a recovery case is about once, as the hand-off", () => {
    const handoff = { id: "h1", status: "failed", failureMessage: "no", releaseName: "R" } as never;
    const withCase = { recentCases: [{ id: "c1", detailsJson: JSON.stringify({ HandoffId: "h1" }) }] } as never;
    const attention = computeAttention(emptySummary(), { handoffs: [handoff], movieRecovery: withCase });
    expect(attention.downloads.handoffs).toHaveLength(1);
    expect(attention.downloads.recoveryCases).toHaveLength(0);
    expect(attention.counts.downloads).toBe(1);
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

  it("puts the tab's own rows and the Needs you rows on the same count", () => {
    const attention = computeAttention(fullSummary());
    for (const tab of ["library", "downloads", "system"] as const) {
      expect(attention.counts[tab]).toBe(attention.items.filter((item) => item.tab === tab).reduce((sum, item) => sum + item.count, 0));
    }
  });
});
