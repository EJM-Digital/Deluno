import { beforeEach, describe, expect, it, vi } from "vitest";

const fetchJson = vi.hoisted(() => vi.fn());
vi.mock("./client", () => ({ fetchJson }));

const guides = await import("./guides");
const scenarios = await import("./scenarios");
const prefs = await import("./release-preferences");

const json = (method: string, body: unknown) => ({ method, headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });

beforeEach(() => fetchJson.mockReset().mockResolvedValue("ok"));

describe("the TRaSH guide calls", () => {
  it("reads the package, versions, inventory and update-check state", async () => {
    await guides.fetchTrashGuidePackage();
    await guides.fetchTrashGuideVersions();
    await guides.fetchTrashGuideInventory();
    await guides.fetchTrashGuideUpdateCheck();
    await guides.fetchGuideRuleMigration();
    expect(fetchJson.mock.calls.map((c) => c[0])).toEqual([
      "/api/v1/guides/trash/package", "/api/v1/guides/trash/versions", "/api/v1/guides/trash/inventory",
      "/api/v1/guides/trash/update-check", "/api/v1/guides/trash/rule-migration"
    ]);
  });

  it("makes a retained version current again, naming its package only when given one", async () => {
    await guides.activateTrashGuideVersion(3);
    await guides.activateTrashGuideVersion(4, "a b");
    expect(fetchJson.mock.calls).toEqual([
      ["/api/v1/guides/trash/versions/3/activate", { method: "POST" }],
      ["/api/v1/guides/trash/versions/4/activate?packageId=a%20b", { method: "POST" }]
    ]);
  });

  it("turns the update check on or off, runs it, and previews and applies updates and syncs", async () => {
    await guides.setTrashGuideUpdateCheckEnabled(true);
    await guides.runTrashGuideUpdateCheck();
    await guides.previewTrashGuideUpdate({ x: 1 } as never);
    await guides.applyTrashGuideUpdate({ x: 2 } as never);
    await guides.previewTrashGuideSync({ y: 1 } as never);
    await guides.applyTrashGuideSync({ y: 2 } as never);
    await guides.applyGuideRuleMigration("hash1", ["r1", "r2"]);
    expect(fetchJson.mock.calls).toEqual([
      ["/api/v1/guides/trash/update-check/settings", json("PUT", { isEnabled: true })],
      ["/api/v1/guides/trash/update-check/run", { method: "POST" }],
      ["/api/v1/guides/trash/preview", json("POST", { x: 1 })],
      ["/api/v1/guides/trash/apply", json("POST", { x: 2 })],
      ["/api/v1/guides/trash/sync/preview", json("POST", { y: 1 })],
      ["/api/v1/guides/trash/sync/apply", json("POST", { y: 2 })],
      ["/api/v1/guides/trash/rule-migration/apply", json("POST", { previewHash: "hash1", ruleIds: ["r1", "r2"] })]
    ]);
  });
});

describe("the media plan scenario calls", () => {
  it("lists scenarios for every media type or one", async () => {
    await scenarios.fetchMediaPlanScenarios();
    await scenarios.fetchMediaPlanScenarios("movies & tv");
    expect(fetchJson.mock.calls.map((c) => c[0])).toEqual(["/api/media-plan-scenarios", "/api/media-plan-scenarios?mediaType=movies%20%26%20tv"]);
  });

  it("compiles a scenario, adding a name only when it has words", async () => {
    await scenarios.fetchMediaPlanScenarioCompilation("a/b", "movies");
    await scenarios.fetchMediaPlanScenarioCompilation("a/b", "movies", "   ");
    await scenarios.fetchMediaPlanScenarioCompilation("a/b", "movies", " My plan ");
    expect(fetchJson.mock.calls.map((c) => c[0])).toEqual([
      "/api/media-plan-scenarios/a%2Fb/compile?mediaType=movies",
      "/api/media-plan-scenarios/a%2Fb/compile?mediaType=movies",
      "/api/media-plan-scenarios/a%2Fb/compile?mediaType=movies&name=My+plan"
    ]);
  });
});

describe("the release preference calls", () => {
  it("lists stored plans for all media or one, and reads one plan with or without a version", async () => {
    await prefs.fetchStoredPreferencePlans();
    await prefs.fetchStoredPreferencePlans("tv");
    await prefs.fetchStoredPreferencePlan("p/1");
    await prefs.fetchStoredPreferencePlan("p/1", "v 2");
    expect(fetchJson.mock.calls.map((c) => c[0])).toEqual([
      "/api/v1/release-preferences/plans", "/api/v1/release-preferences/plans?mediaType=tv",
      "/api/v1/release-preferences/plans/p%2F1", "/api/v1/release-preferences/plans/p%2F1?version=v%202"
    ]);
  });

  it("saves a plan, previews a release, compiles a profile and judges a draft", async () => {
    await prefs.savePreferencePlan({ id: "p" } as never);
    await prefs.previewReleasePreference({ planId: "p", releaseName: "R" });
    await prefs.compileQualityProfilePreferences("q/1");
    const draft = { name: "n", mediaType: "movies", allowedQualities: [], cutoffQuality: "", customFormatIds: [], formatIntents: {}, upgradeUntilCutoff: true, upgradeUnknownItems: false, allowLowerQualityReplacements: false, releaseName: "R" };
    await prefs.judgeDraftProfile(draft);
    expect(fetchJson.mock.calls).toEqual([
      ["/api/v1/release-preferences/plans", json("POST", { id: "p" })],
      ["/api/v1/release-preferences/preview", json("POST", { planId: "p", releaseName: "R" })],
      ["/api/v1/release-preferences/plans/quality-profile/q%2F1"],
      ["/api/v1/release-preferences/judge-draft", json("POST", draft)]
    ]);
  });
});
