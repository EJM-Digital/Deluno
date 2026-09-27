import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it } from "vitest";
import type { ActivityStatsSnapshot } from "../../lib/api";
import { ActivityTicker } from "./activity-ticker";

/**
 * #827: the Dashboard's Live Activity card grows a "Last 24h" summary line
 * fed by GET /api/stats, so the same counts an external dashboard widget can
 * poll are also visible in-app. This is a render smoke test for that
 * section — dashboard-page.tsx renders it by passing `stats` straight
 * through to `ActivityTicker`.
 */
function stats(overrides: Partial<ActivityStatsSnapshot> = {}): ActivityStatsSnapshot {
  return {
    sinceUtc: "2026-09-27T00:00:00Z",
    generatedUtc: "2026-09-28T00:00:00Z",
    windowHours: 24,
    searchesRun: 12,
    downloadsStarted: 3,
    importsCompleted: 2,
    cleanupRemovals: 1,
    retentionRemovals: 0,
    ...overrides
  };
}

function renderTicker(activityStats?: ActivityStatsSnapshot) {
  return render(
    <MemoryRouter>
      <ActivityTicker seed={[]} stats={activityStats} />
    </MemoryRouter>
  );
}

describe("Dashboard stats summary section (#827)", () => {
  it("renders the last-24h counts the stats endpoint returns", () => {
    renderTicker(stats());

    const summary = screen.getByLabelText("Automation counts for the last 24 hours");
    expect(summary).toBeInTheDocument();
    expect(summary).toHaveTextContent("12 searches");
    expect(summary).toHaveTextContent("3 downloads");
    expect(summary).toHaveTextContent("2 imports");
    expect(summary).toHaveTextContent("1 cleaned up");
    expect(summary).toHaveTextContent("0 retention");
  });

  it("uses singular wording for a count of exactly one", () => {
    renderTicker(stats({ searchesRun: 1, downloadsStarted: 1, importsCompleted: 1 }));

    const summary = screen.getByLabelText("Automation counts for the last 24 hours");
    expect(summary).toHaveTextContent("1 search");
    expect(summary).toHaveTextContent("1 download");
    expect(summary).toHaveTextContent("1 import");
  });

  it("renders nothing extra when no stats snapshot is available yet", () => {
    renderTicker(undefined);

    expect(screen.queryByLabelText("Automation counts for the last 24 hours")).not.toBeInTheDocument();
  });
});
