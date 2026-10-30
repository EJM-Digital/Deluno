import { render, screen, within } from "@testing-library/react";
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
    expect(screen.getAllByText("A removed indexer")).toHaveLength(1);
    expect(screen.getAllByText("A removed indexer")[0]!.closest("[role='row'], tr, li, div")).not.toBeNull();
  });

  it("keeps the last known name when there is one", () => {
    render(<IndexerScoreboard snapshot={snapshot([row({ id: "c", name: "1337x (a009)", isEnabled: false, healthStatus: "removed" })])} />);
    expect(screen.getAllByText("1337x (a009)")).toHaveLength(1);
    expect(screen.queryByText("A removed indexer")).toBeNull();
  });
});

describe("the Scoreboard says each indexer's name once (#1249)", () => {
  const names = [1, 2, 3, 4, 5].map((n) => `A Very Long Indexer Name Number ${n}`);
  const rows = names.map((name, i) => row({ id: `r${i}`, name, totalQueries: 2368 - i * 300, totalGrabs: 24 - i * 5, successfulGrabs: 20 - i * 4, failedQueries: i, averageResponseMilliseconds: 64825 - i * 9000 }));

  it("prints every name exactly once across the bars and the table", () => {
    render(<IndexerScoreboard snapshot={snapshot(rows)} />);
    for (const name of names) expect(screen.getAllByText(name)).toHaveLength(1);
  });

  it("keeps every metric as a column of the one table", () => {
    render(<IndexerScoreboard snapshot={snapshot(rows)} />);
    for (const label of ["Queries", "Grabs", "Grab rate", "Response", "Failed"]) {
      expect(screen.getAllByText(label).length).toBeGreaterThanOrEqual(1);
    }
  });
});

describe("the Scoreboard status chip (#1329)", () => {
  const chipOf = (name: string, label: string) => {
    const cell = screen.getAllByText(name)[0]!.closest("[role='row'], tr, li")!;
    return within(cell as HTMLElement).getAllByText(label)[0]!;
  };

  it("reads Paused for a switched-off indexer and Removed for a removed one, in the quiet tone; a failure rate of 25% or more, unreachable or degraded warns; a healthy one is ok", () => {
    render(<IndexerScoreboard snapshot={snapshot([
      row({ id: "p", name: "Paused One", isEnabled: false, healthStatus: "healthy", failureRate: 0.9 }),
      row({ id: "r", name: "Removed One", isEnabled: false, healthStatus: "removed" }),
      row({ id: "f", name: "Failing One", healthStatus: "healthy", failureRate: 0.25 }),
      row({ id: "u", name: "Unreachable One", healthStatus: "unreachable", failureRate: 0 }),
      row({ id: "d", name: "Degraded One", healthStatus: "degraded", failureRate: 0 }),
      row({ id: "h", name: "Healthy One", healthStatus: "healthy", failureRate: 0.24 })
    ])} />);

    expect(chipOf("Paused One", "Paused").className).toContain("text-muted-foreground");
    expect(chipOf("Removed One", "Removed").className).toContain("text-muted-foreground");
    expect(chipOf("Failing One", "healthy").className).toContain("text-warning");
    expect(chipOf("Unreachable One", "unreachable").className).toContain("text-warning");
    expect(chipOf("Degraded One", "degraded").className).toContain("text-warning");
    expect(chipOf("Healthy One", "healthy").className).toContain("text-success");
  });
});
