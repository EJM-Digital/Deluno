import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import type { IndexerScoreboardRow, IndexerScoreboardSnapshot } from "../../lib/api";
import { IndexerScoreboard } from "./indexer-scoreboard";

const row = (over: Partial<IndexerScoreboardRow>): IndexerScoreboardRow => ({
  id: "a", name: "Tracker One", isEnabled: true, healthStatus: "healthy", totalQueries: 7, searchQueries: 7, rssQueries: 0, authQueries: 0, failedQueries: 2,
  failureRate: 0.29, averageResponseMilliseconds: 900, candidatesReturned: 0, totalGrabs: 1, successfulGrabs: 1, queryToGrabConversion: 0.1, recommendation: "Fine.", ...over
});

const snapshot = (indexers: IndexerScoreboardRow[]): IndexerScoreboardSnapshot => ({
  windowDays: 30, fromUtc: "2026-09-01T00:00:00Z", toUtc: "2026-10-01T00:00:00Z", activeIndexers: 1, totalIndexers: indexers.length, totalQueries: 14, totalGrabs: 2,
  successfulGrabs: 2, conversionRate: 0.1, insight: "One.", indexers
});

describe("the Scoreboard names every indexer (#1168 C6)", () => {
  it("says 'A removed indexer' for a removed row whose history kept no name, in its row and in the charts", () => {
    render(<IndexerScoreboard snapshot={snapshot([row({}), row({ id: "b", name: "", isEnabled: false, healthStatus: "removed" })])} />);
    // The row and each of the four bar charts that has a bar for it.
    expect(screen.getAllByText("A removed indexer").length).toBeGreaterThanOrEqual(2);
  });

  it("keeps the last known name when there is one", () => {
    render(<IndexerScoreboard snapshot={snapshot([row({ id: "c", name: "1337x (a009)", isEnabled: false, healthStatus: "removed" })])} />);
    expect(screen.getAllByText("1337x (a009)").length).toBeGreaterThanOrEqual(1);
    expect(screen.queryByText("A removed indexer")).toBeNull();
  });
});
