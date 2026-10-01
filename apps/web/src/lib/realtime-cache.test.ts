import { QueryClient } from "@tanstack/react-query";
import { beforeEach, describe, expect, it } from "vitest";
import type { DashboardSummary, DashboardSummaryUpdatedEvent } from "./api/types";
import { applyDashboardSummaryUpdate, DASHBOARD_SUMMARY_KEY } from "./realtime-cache";
import { isRealtimeConnected, setRealtimeConnected } from "./realtime-state";

/**
 * #1097: a pushed summary section goes straight into the cached summary, with
 * no fetch afterwards, and an older one (a replay after a reconnect) never
 * overwrites a newer one.
 */
function summary(overrides: Record<string, unknown> = {}): DashboardSummary {
  return {
    asOfUtc: "2026-10-01T12:00:00.000Z",
    buildMilliseconds: 12,
    unavailableSections: ["health"],
    library: null,
    downloads: { asOfUtc: "2026-10-01T12:00:00.000Z", active: 1 },
    pipeline: { asOfUtc: "2026-10-01T12:00:00.000Z", today: { grabbed: 1 } },
    imports: null,
    automation: null,
    indexers: null,
    processors: null,
    retention: null,
    subtitles: null,
    requests: null,
    blocklist: null,
    health: null,
    storage: null,
    system: null,
    activity: null,
    ...overrides
  } as unknown as DashboardSummary;
}

function update(sections: Record<string, unknown>, asOfUtc = "2026-10-01T12:00:05.000Z", unavailable: string[] = []): DashboardSummaryUpdatedEvent {
  return { asOfUtc, sections, unavailable } as unknown as DashboardSummaryUpdatedEvent;
}

describe("applying pushed Dashboard sections to the cache", () => {
  let queryClient: QueryClient;

  beforeEach(() => {
    queryClient = new QueryClient();
  });

  it("replaces only the pushed sections and keeps the rest", () => {
    queryClient.setQueryData(DASHBOARD_SUMMARY_KEY, summary());

    const applied = applyDashboardSummaryUpdate(
      queryClient,
      update({ downloads: { asOfUtc: "2026-10-01T12:00:05.000Z", active: 4 }, health: { asOfUtc: "2026-10-01T12:00:05.000Z", problems: 0 } })
    );

    const next = queryClient.getQueryData<DashboardSummary>(DASHBOARD_SUMMARY_KEY)!;
    expect(applied).toBe(true);
    expect((next.downloads as unknown as { active: number }).active).toBe(4);
    expect(next.pipeline).toEqual(summary().pipeline);
    expect(next.asOfUtc).toBe("2026-10-01T12:00:05.000Z");
    // A section that arrives is no longer unavailable.
    expect(next.unavailableSections).toEqual([]);
  });

  it("makes no request: it only writes what the server sent", () => {
    queryClient.setQueryData(DASHBOARD_SUMMARY_KEY, summary());
    let fetched = 0;
    queryClient.getQueryCache().subscribe((event) => {
      if (event.type === "updated" && event.action.type === "fetch") fetched += 1;
    });

    applyDashboardSummaryUpdate(queryClient, update({ downloads: { asOfUtc: "2026-10-01T12:00:05.000Z", active: 9 } }));

    expect(fetched).toBe(0);
  });

  it("does not let an older replayed section overwrite a newer one", () => {
    queryClient.setQueryData(DASHBOARD_SUMMARY_KEY, summary());

    const applied = applyDashboardSummaryUpdate(
      queryClient,
      update({ downloads: { asOfUtc: "2026-10-01T11:59:00.000Z", active: 99 } }, "2026-10-01T11:59:00.000Z")
    );

    const next = queryClient.getQueryData<DashboardSummary>(DASHBOARD_SUMMARY_KEY)!;
    expect(applied).toBe(false);
    expect((next.downloads as unknown as { active: number }).active).toBe(1);
    expect(next.asOfUtc).toBe("2026-10-01T12:00:00.000Z");
  });

  it("applying the same push twice leaves the same summary", () => {
    queryClient.setQueryData(DASHBOARD_SUMMARY_KEY, summary());
    const push = update({ downloads: { asOfUtc: "2026-10-01T12:00:05.000Z", active: 4 } });

    applyDashboardSummaryUpdate(queryClient, push);
    const once = queryClient.getQueryData(DASHBOARD_SUMMARY_KEY);
    applyDashboardSummaryUpdate(queryClient, push);

    expect(queryClient.getQueryData(DASHBOARD_SUMMARY_KEY)).toEqual(once);
  });

  it("names a section the server could not build and keeps what was cached for it", () => {
    queryClient.setQueryData(DASHBOARD_SUMMARY_KEY, summary({ unavailableSections: [] }));

    applyDashboardSummaryUpdate(
      queryClient,
      update({ downloads: { asOfUtc: "2026-10-01T12:00:05.000Z", active: 2 } }, undefined, ["storage"])
    );

    const next = queryClient.getQueryData<DashboardSummary>(DASHBOARD_SUMMARY_KEY)!;
    expect(next.unavailableSections).toEqual(["storage"]);
  });

  it("does nothing while no summary is cached yet", () => {
    expect(applyDashboardSummaryUpdate(queryClient, update({ downloads: { asOfUtc: "2026-10-01T12:00:05.000Z", active: 2 } }))).toBe(false);
    expect(queryClient.getQueryData(DASHBOARD_SUMMARY_KEY)).toBeUndefined();
  });
});

describe("the realtime connection flag", () => {
  it("starts down and follows what it is told", () => {
    setRealtimeConnected(false);
    expect(isRealtimeConnected()).toBe(false);
    setRealtimeConnected(true);
    expect(isRealtimeConnected()).toBe(true);
    setRealtimeConnected(false);
  });
});
