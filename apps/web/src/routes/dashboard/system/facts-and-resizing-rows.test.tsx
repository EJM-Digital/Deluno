import { act, renderHook } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { instanceFacts } from "./facts";
import { useResizing } from "./use-resizing";

afterEach(() => {
  vi.useRealTimers();
});

const instance = (extra: object = {}) => ({
  address: "192.168.1.10:5000", version: "1.4.0", latest: true, cpuPercent: 3.2, memoryBytes: 250_000_000, answerMs: 12, answerSlowestMs: 40,
  workersBusy: 2, oldestWaitingSeconds: null, jobsToday: 14, jobsFailedToday: 0, requestsToday: 300, requestErrorsToday: 0, runsAs: "service",
  startedUtc: new Date(Date.now() - 3_600_000).toISOString(), dataBytes: 1_000_000, databaseCount: 3, lastBackupUtc: null, lastBackupBytes: null,
  restartsThisWeek: 0, browsersLive: 1, ...extra
}) as never;

describe("System: This instance fact tiles", () => {
  it("shows the twelve tiles in order, each with its value and a plain second line", () => {
    const facts = instanceFacts(instance(), undefined, Date.now());
    expect(facts.map((fact) => fact.label)).toEqual(["Version", "Usage", "Answers in", "Workers", "Jobs today", "Requests", "Address", "Runs as", "Data", "Last backup", "Restarts", "Browsers"]);
    const byLabel = Object.fromEntries(facts.map((fact) => [fact.label, fact]));
    expect(byLabel.Version).toMatchObject({ value: "1.4.0", sub: "latest version", tone: "good" });
    expect(byLabel.Usage).toMatchObject({ value: "3% CPU" });
    expect(byLabel["Answers in"]).toMatchObject({ value: "12 ms", sub: "slowest 40 ms" });
    expect(byLabel.Workers).toMatchObject({ value: "2 busy", sub: "none waiting" });
    expect(byLabel.Address).toMatchObject({ value: "192.168.1.10", sub: "port 5000" });
    expect(byLabel["Runs as"]).toMatchObject({ value: "Service" });
    expect(byLabel["Last backup"]).toMatchObject({ value: "None yet" });
    expect(byLabel.Browsers).toMatchObject({ value: "1 live" });
  });

  it("marks jobs and requests bad when any failed, and restarts bad when there were any this week", () => {
    const facts = instanceFacts(instance({ jobsFailedToday: 2, requestErrorsToday: 1, restartsThisWeek: 3 }), undefined, Date.now());
    const byLabel = Object.fromEntries(facts.map((fact) => [fact.label, fact]));
    expect(byLabel["Jobs today"]).toMatchObject({ sub: "2 failed", tone: "bad" });
    expect(byLabel.Requests).toMatchObject({ sub: "1 error", tone: "bad" });
    expect(byLabel.Restarts).toMatchObject({ value: "3", tone: "bad" });
  });
});

describe("System: resizing", () => {
  it("reports resizing from the first resize until the window has been still for the settle time", () => {
    vi.useFakeTimers();
    const { result } = renderHook(() => useResizing(150));
    expect(result.current).toBe(false);
    act(() => { window.dispatchEvent(new Event("resize")); });
    expect(result.current).toBe(true);
    act(() => { vi.advanceTimersByTime(149); });
    expect(result.current).toBe(true);
    act(() => { vi.advanceTimersByTime(1); });
    expect(result.current).toBe(false);
  });
});
