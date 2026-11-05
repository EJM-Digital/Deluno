import { describe, expect, it } from "vitest";
import { sidebarBadge } from "./app-layout";

/** #1499: a number beside a sidebar item says what it counts, and a number that needs the person is never drawn as a plain count. */

const attention = (over: Record<string, unknown> = {}) => ({
  downloadsNeedingAction: 0, heldReleases: 0, inProgress: 0, indexerAlerts: 0, movieWanted: 0, tvWanted: 0, ...over
}) as never;

describe("the number beside In progress", () => {
  it("on the rig (1 active, 16 needing action) shows 1 in progress and, apart, 16 that need you", () => {
    const badge = sidebarBadge(attention({ inProgress: 1, downloadsNeedingAction: 15, heldReleases: 1 }), "transfers");

    expect(badge).toEqual([
      expect.objectContaining({ kind: "count", count: 1, label: "1 in progress" }),
      expect.objectContaining({ kind: "needs", count: 16, label: "16 need your action", title: "16 things need your action. The same number as Needs action on this page." })
    ]);
  });

  it("on the rig's later state (one failed import, nothing moving) shows no plain count and one that needs you", () => {
    expect(sidebarBadge(attention({ inProgress: 0, downloadsNeedingAction: 1 }), "transfers")).toEqual([expect.objectContaining({ kind: "needs", count: 1, label: "1 needs your action" })]);
  });

  it("shows only the warning when nothing is in progress", () => {
    expect(sidebarBadge(attention({ downloadsNeedingAction: 1 }), "transfers")).toEqual([expect.objectContaining({ kind: "needs", label: "1 needs your action" })]);
  });

  it("shows nothing when there is nothing to say", () => {
    expect(sidebarBadge(attention(), "transfers")).toEqual([]);
    expect(sidebarBadge(attention(), "none")).toEqual([]);
  });
});

describe("the other sidebar numbers", () => {
  it("count what is wanted for Films and TV, in the plain style", () => {
    expect(sidebarBadge(attention({ movieWanted: 12 }), "movies")).toEqual([expect.objectContaining({ kind: "count", count: 12, label: "12 wanted" })]);
    expect(sidebarBadge(attention({ tvWanted: 4 }), "tv")).toEqual([expect.objectContaining({ kind: "count", count: 4, label: "4 wanted" })]);
  });

  it("warn for indexers that are not answering", () => {
    expect(sidebarBadge(attention({ indexerAlerts: 2 }), "indexers")).toEqual([
      expect.objectContaining({ kind: "needs", count: 2, label: "2 need your action", title: "2 indexers are not answering and need your action." })
    ]);
    expect(sidebarBadge(attention({ indexerAlerts: 1 }), "indexers")[0].title).toBe("1 indexer is not answering and needs your action.");
  });
});
