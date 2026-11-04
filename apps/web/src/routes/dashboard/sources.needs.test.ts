import { describe, expect, it, vi } from "vitest";

const status = vi.hoisted(() => ({
  current: { steps: [] as Array<{ id: string }>, attentionItems: [] as Array<Record<string, string>> }
}));
vi.mock("../../lib/setup-status", () => ({ buildSetupStatus: () => status.current }));

const { buildSetupState, liveSources, setupSources } = await import("./sources");

const item = (id: string) => ({ id, title: `Title ${id}`, text: `Text ${id}`, href: `/go/${id}`, action: `Action ${id}` });

describe("what the setup checklist raises beyond its own steps", () => {
  it("keeps only the items the ladder has no step for, as rows with a title, text, link and action", () => {
    status.current = { steps: [{ id: "libraries" }, { id: "indexers" }], attentionItems: [item("libraries"), item("quality"), item("indexers"), item("policy")] };
    const { needs } = buildSetupState({} as never);
    expect(needs).toEqual([
      { id: "quality", title: "Title quality", text: "Text quality", to: "/go/quality", action: "Action quality" },
      { id: "policy", title: "Title policy", text: "Text policy", to: "/go/policy", action: "Action policy" }
    ]);
  });

  it("leaves out the connection health item, whose connections have their own rows", () => {
    status.current = { steps: [], attentionItems: [item("connection-health"), item("other")] };
    expect(buildSetupState({} as never).needs.map((n) => n.id)).toEqual(["other"]);
  });

  it("hands back the status it was given and nothing when nothing is raised", () => {
    status.current = { steps: [{ id: "a" }], attentionItems: [] };
    const result = buildSetupState({} as never);
    expect(result.status).toBe(status.current);
    expect(result.needs).toEqual([]);
  });

  it("drops an item only when a step has exactly its id", () => {
    status.current = { steps: [{ id: "a" }, { id: "b" }], attentionItems: [item("a"), item("c")] };
    expect(buildSetupState({} as never).needs.map((n) => n.id)).toEqual(["c"]);
  });
});

describe("how each list is named", () => {
  it.each([
    [setupSources.policySets, ["src", "policy-sets"], "Library profiles"],
    [setupSources.qualityProfiles, ["src", "quality-profiles"], "Quality profiles"],
    [setupSources.setupProgress, ["src", "setup-progress"], "Setup progress"],
    [liveSources.upcoming, ["src", "dashboard-upcoming"], "Airing soon"],
    [liveSources.boardDispatches, ["src", "dashboard-board-dispatches"], "Sent downloads for the board"],
    [liveSources.throughput, ["src", "dashboard-throughput-6h"], "Speed over six hours"],
    [liveSources.scoreboard, ["src", "dashboard-indexers-today"], "Indexers today"],
    [liveSources.latestActivity, ["src", "dashboard-latest-activity"], "Latest activity"],
    [liveSources.liveExtras, ["src", "dashboard-live-extras"], "Today on Live"]
  ])("%#", (src, key, label) => {
    expect(src.key).toEqual(key);
    expect(src.label).toBe(label);
  });

  it("starts the board with no sent downloads", () => {
    expect(liveSources.boardDispatches.fallback).toEqual([]);
  });
});
