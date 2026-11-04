import { describe, expect, it } from "vitest";
import type { DashboardSummary } from "../../../lib/api";
import type { SystemOverviewInstance } from "../../../lib/api/types/system-overview";
import { instanceFacts } from "./facts";
import { sizeText } from "./logic";

const NOW = Date.parse("2026-11-04T12:00:00Z");

function instance(overrides: Partial<SystemOverviewInstance> = {}): SystemOverviewInstance {
  return {
    version: "1.2.3", latest: true, channel: "stable", startedUtc: "2026-11-01T01:00:00Z", address: "http://localhost:5099/", runsAs: "service",
    cpuPercent: 12.4, memoryBytes: 500_000_000, workersBusy: 2, oldestWaitingSeconds: null, answerMs: 40.4, answerSlowestMs: 200.6,
    jobsToday: 1200, jobsFailedToday: 0, requestsToday: 5000, requestErrorsToday: 0, dataBytes: 3_000_000_000, databaseCount: 4,
    lastBackupUtc: "2026-11-04T09:00:00Z", lastBackupBytes: 70_000_000, restartsThisWeek: 0, browsersLive: 1, ...overrides
  };
}

function summary(system: Record<string, unknown> | null): DashboardSummary {
  return {
    system: system === null ? undefined : {
      version: "1.2.3", updateAvailable: false, jobs: { running: 1, queued: 0 }, lastBackupUtc: "2026-11-04T09:00:00Z", backupsEnabled: true,
      machine: { cpuPercent: 9.6, memoryBytes: 800_000_000 }, ...system
    }
  } as unknown as DashboardSummary;
}

const byLabel = (facts: ReturnType<typeof instanceFacts>, label: string) => facts.find((fact) => fact.label === label)!;

describe("the fact tiles of This instance", () => {
  it("gives twelve tiles in the order a person reads them", () => {
    expect(instanceFacts(instance(), undefined, NOW).map((fact) => fact.label)).toEqual([
      "Version", "Usage", "Answers in", "Workers", "Jobs today", "Requests", "Address", "Runs as", "Data", "Last backup", "Restarts", "Browsers"
    ]);
  });

  it("says the version is the latest, or that a newer one is ready", () => {
    expect(byLabel(instanceFacts(instance(), undefined, NOW), "Version")).toEqual({ label: "Version", value: "1.2.3", sub: "latest version", tone: "good" });
    expect(byLabel(instanceFacts(instance({ latest: false }), undefined, NOW), "Version")).toEqual({ label: "Version", value: "1.2.3", sub: "a newer version is ready", tone: undefined });
  });

  it("rounds the usage and answer times", () => {
    const facts = instanceFacts(instance(), undefined, NOW);
    expect(byLabel(facts, "Usage")).toEqual({ label: "Usage", value: "12% CPU", sub: `${sizeText(500_000_000)} RAM` });
    expect(byLabel(facts, "Answers in")).toEqual({ label: "Answers in", value: "40 ms", sub: "slowest 201 ms" });
  });

  it("names how long the oldest waiting job has waited, or says none is waiting", () => {
    expect(byLabel(instanceFacts(instance({ oldestWaitingSeconds: 12.6 }), undefined, NOW), "Workers")).toEqual({ label: "Workers", value: "2 busy", sub: "oldest waiting 13 s" });
    expect(byLabel(instanceFacts(instance({ oldestWaitingSeconds: 0 }), undefined, NOW), "Workers").sub).toBe("none waiting");
    expect(byLabel(instanceFacts(instance({ oldestWaitingSeconds: null }), undefined, NOW), "Workers").sub).toBe("none waiting");
  });

  it("marks failed jobs and request errors bad, and a clean day good", () => {
    const clean = instanceFacts(instance(), undefined, NOW);
    expect(byLabel(clean, "Jobs today")).toMatchObject({ sub: "none failed", tone: "good" });
    expect(byLabel(clean, "Requests")).toMatchObject({ sub: "no errors", tone: "good" });
    const bad = instanceFacts(instance({ jobsFailedToday: 3, requestErrorsToday: 2 }), undefined, NOW);
    expect(byLabel(bad, "Jobs today")).toMatchObject({ sub: "3 failed", tone: "bad" });
    expect(byLabel(bad, "Requests")).toMatchObject({ sub: "2 errors", tone: "bad" });
  });

  it("splits the address into host and port, and leaves the port line empty when there is none", () => {
    expect(byLabel(instanceFacts(instance(), undefined, NOW), "Address")).toEqual({ label: "Address", value: "localhost", sub: "port 5099" });
    expect(byLabel(instanceFacts(instance({ address: "deluno.local" }), undefined, NOW), "Address")).toEqual({ label: "Address", value: "deluno.local", sub: "" });
    expect(byLabel(instanceFacts(instance({ address: "" }), undefined, NOW), "Address").value).toBe("–");
  });

  it("says how Deluno runs and since when, with a dash for an unknown way of running", () => {
    expect(byLabel(instanceFacts(instance({ runsAs: "docker" }), undefined, NOW), "Runs as").value).toBe("Docker");
    expect(byLabel(instanceFacts(instance({ runsAs: "app" }), undefined, NOW), "Runs as").value).toBe("App");
    expect(byLabel(instanceFacts(instance({ runsAs: "service" }), undefined, NOW), "Runs as")).toMatchObject({ value: "Service", sub: expect.stringMatching(/^since /) });
    expect(byLabel(instanceFacts(instance({ runsAs: "other" as never, startedUtc: "nonsense" }), undefined, NOW), "Runs as")).toEqual({ label: "Runs as", value: "–", sub: "" });
  });

  it("sizes the data, counts the databases and dashes an empty reading", () => {
    expect(byLabel(instanceFacts(instance(), undefined, NOW), "Data")).toEqual({ label: "Data", value: sizeText(3_000_000_000), sub: "4 databases" });
    expect(byLabel(instanceFacts(instance({ dataBytes: Number.NaN, databaseCount: 1 }), undefined, NOW), "Data")).toEqual({ label: "Data", value: "–", sub: "1 database" });
  });

  it("says when the last backup was and how big, or that none has been made", () => {
    expect(byLabel(instanceFacts(instance(), undefined, NOW), "Last backup")).toEqual({ label: "Last backup", value: "3 h ago", sub: sizeText(70_000_000) });
    expect(byLabel(instanceFacts(instance({ lastBackupBytes: null }), undefined, NOW), "Last backup").sub).toBe("");
    expect(byLabel(instanceFacts(instance({ lastBackupUtc: null, lastBackupBytes: null }), undefined, NOW), "Last backup")).toEqual({ label: "Last backup", value: "None yet", sub: "back up below" });
  });

  it("counts restarts this week, bad when there were any", () => {
    expect(byLabel(instanceFacts(instance(), undefined, NOW), "Restarts")).toEqual({ label: "Restarts", value: "None", sub: "this week", tone: "good" });
    expect(byLabel(instanceFacts(instance({ restartsThisWeek: 2 }), undefined, NOW), "Restarts")).toEqual({ label: "Restarts", value: "2", sub: "this week", tone: "bad" });
  });

  it("counts the browsers live now", () => {
    expect(byLabel(instanceFacts(instance({ browsersLive: 3 }), undefined, NOW), "Browsers")).toEqual({ label: "Browsers", value: "3 live", sub: "live now" });
  });
});

describe("the fact tiles before the overview has arrived", () => {
  it("shows nothing at all when the summary has no system reading either", () => {
    expect(instanceFacts(null, undefined, NOW)).toEqual([]);
    expect(instanceFacts(undefined, summary(null), NOW)).toEqual([]);
  });

  it("shows the four the summary can answer", () => {
    expect(instanceFacts(null, summary({}), NOW)).toEqual([
      { label: "Version", value: "1.2.3", sub: "latest version", tone: "good" },
      { label: "Usage", value: "10% CPU", sub: `${sizeText(800_000_000)} RAM` },
      { label: "Workers", value: "1 busy", sub: "none waiting" },
      { label: "Last backup", value: "3 h ago", sub: "automatic backups on" }
    ]);
  });

  it("says a newer version is ready, how many jobs wait, and that backups are off or never made", () => {
    const facts = instanceFacts(null, summary({ updateAvailable: true, jobs: { running: 0, queued: 4 }, lastBackupUtc: null, backupsEnabled: false }), NOW);
    expect(facts[0]).toEqual({ label: "Version", value: "1.2.3", sub: "a newer version is ready", tone: undefined });
    expect(facts[2]).toEqual({ label: "Workers", value: "0 busy", sub: "4 waiting" });
    expect(facts[3]).toEqual({ label: "Last backup", value: "None yet", sub: "automatic backups off" });
  });

  it("leaves the usage tile out when the machine cannot be read", () => {
    expect(instanceFacts(null, summary({ machine: undefined }), NOW).map((fact) => fact.label)).toEqual(["Version", "Workers", "Last backup"]);
  });
});
