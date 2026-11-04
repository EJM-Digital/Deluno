import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot, type GuidePackage } from "../lib/api";

/**
 * Release Preferences, the guide-update panel and the move of rules off the old guide catalogue: the weekly check
 * switch, running the check, the sync preview and its apply, going back to a retained version, every status and
 * every way each call can fail, and the loader that reads it all. The page's calls are replaced with recorders.
 */

vi.setConfig({ testTimeout: 30_000 });
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
const toastMock = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn() }));
vi.mock("../components/shell/toaster", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../components/shell/toaster")>()),
  toast: toastMock
}));
const api = vi.hoisted(() => ({
  setEnabled: vi.fn(), run: vi.fn(), previewSync: vi.fn(), applySync: vi.fn(), activate: vi.fn(), versions: vi.fn(),
  migrate: vi.fn(), applyMigration: vi.fn(), fetchJson: vi.fn(), package: vi.fn(), updateCheck: vi.fn(), overview: vi.fn()
}));
vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("./settings-overview-page", () => ({ settingsOverviewLoader: api.overview }));
vi.mock("../lib/api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/api")>()),
  setTrashGuideUpdateCheckEnabled: api.setEnabled,
  runTrashGuideUpdateCheck: api.run,
  previewTrashGuideSync: api.previewSync,
  applyTrashGuideSync: api.applySync,
  activateTrashGuideVersion: api.activate,
  fetchTrashGuideVersions: api.versions,
  fetchGuideRuleMigration: api.migrate,
  applyGuideRuleMigration: api.applyMigration,
  fetchTrashGuidePackage: api.package,
  fetchTrashGuideUpdateCheck: api.updateCheck,
  fetchJson: api.fetchJson
}));
vi.mock("../lib/live-refresh", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/live-refresh")>()),
  useLiveRefresh: () => undefined
}));
// The props the page hands each drawer, so an open request that no click can make is still made.
const seen = vi.hoisted(() => ({ drawers: new Map<string, { onOpenChange: (open: boolean) => void; footer?: { props: Record<string, unknown> } }>() }));
vi.mock("../components/ui/drawer", async (importOriginal) => {
  const original = await importOriginal<typeof import("../components/ui/drawer")>();
  return { ...original, Drawer: (props: Parameters<typeof original.Drawer>[0]) => { seen.drawers.set(String(props.title), props as never); return original.Drawer(props); } };
});
const revalidate = vi.fn();
const loaderData: Record<string, unknown> = {};
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate })
}));

const { SettingsCustomFormatsPage, settingsCustomFormatsLoader } = await import("./settings-custom-formats-page");

const guide = {
  id: "guide", name: "Test guide", version: 3, schemaVersion: 3,
  source: { sourceName: "test", repositoryUrl: "", guideUrl: "", upstreamRevision: "1", reviewedUtc: "", adaptation: "" },
  integritySha256: "current-sha", qualityTiers: [], customFormats: [], qualityProfiles: [], bundles: []
} as unknown as GuidePackage;

const change = (over: Record<string, unknown> = {}) => ({
  kind: "customFormat", id: "1", name: "HDR10", mediaType: "movies", sourcePath: "a/hdr10.json", changeType: "changed", isInUse: false, inUseCustomFormatIds: [], ...over
});
const report = (over: Record<string, unknown> = {}) => ({
  baselineRevision: "base", remoteRevision: "remote-revision-0123456789", checkedUtc: "2026-11-01T00:00:00Z", isComplete: true, summary: "Two files changed.",
  changes: [change()], addedSources: [], ...over
});
const check = (over: Record<string, unknown> = {}) => ({
  isEnabled: true, lastCheckedUtc: null, lastSeenRevision: null, status: "update-available", error: null, report: report(), updatedUtc: "2026-11-01T00:00:00Z", ...over
});
const stored = (version: number, over: Record<string, unknown> = {}) => ({
  package: { ...guide, id: `pkg-${version}`, version, source: { ...(guide as { source: object }).source, upstreamRevision: `rev-${version}-abcdefghijklmnop` } },
  isActive: false, storedUtc: "2026-11-01T00:00:00Z", integritySha256: `sha-${version}-abcdefghijklmnop`, ...over
});
const preview = (over: Record<string, unknown> = {}) => ({
  current: stored(3), proposed: { ...guide, version: 4, source: { upstreamRevision: "proposed-revision-0123456789" } }, proposedIntegritySha256: "proposed-sha",
  proposedInventory: {}, profileDiffs: [], errors: [], warnings: [], canApply: true, ...over
});
const diff = (over: Record<string, unknown> = {}) => ({
  profileId: "p1", profileName: "Films HD", currentPlanHash: "abcdefabcdefabcdef", proposedPlanHash: "123456123456123456", currentAdvancedRuleCount: 1,
  proposedAdvancedRuleCount: 2, changes: ["Adds a rule."], warnings: [], ...over
});
const migrationItem = (over: Record<string, unknown> = {}) => ({
  ruleId: "r1", mediaType: "movies", currentName: "Old HDR", currentConditions: "[]", action: "adopt-trash-format", newName: "HDR10", newConditions: "[]",
  conditionsWereEdited: false, profileNames: [], explanation: "Matches TRaSH's HDR10.", ...over
});

function load(over: Record<string, unknown> = {}) {
  Object.assign(loaderData, {
    libraries: [], policySets: [], qualityProfiles: [], customFormats: [], settings: emptyPlatformSettingsSnapshot, guide,
    guideUpdateCheck: check(), guideVersions: [], ruleMigration: null, ...over
  });
}
const renderPage = () => render(<MemoryRouter><SettingsCustomFormatsPage /></MemoryRouter>);
const deferred = <T,>() => { let resolve!: (value: T) => void; let reject!: (reason?: unknown) => void; const promise = new Promise<T>((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; };
const spinning = (button: HTMLElement) => button.querySelector(".animate-spin") !== null;

beforeEach(() => {
  for (const fn of [toastMock.success, toastMock.error, revalidate, ...Object.values(api)]) fn.mockReset();
  seen.drawers.clear();
});
afterEach(() => { vi.restoreAllMocks(); });

describe("the loader", () => {
  it("reads the overview, the rules, the guide, the check, the versions and the move together", async () => {
    api.overview.mockResolvedValue({ libraries: [], settings: { x: 1 } });
    api.fetchJson.mockResolvedValue([{ id: "cf" }]);
    api.package.mockResolvedValue(guide);
    api.updateCheck.mockResolvedValue(check());
    api.versions.mockResolvedValue([stored(3)]);
    api.migrate.mockResolvedValue({ packageVersion: 3, items: [], previewHash: "h" });

    const data = await settingsCustomFormatsLoader();

    expect(api.fetchJson).toHaveBeenCalledWith("/api/custom-formats");
    expect(data).toMatchObject({ settings: { x: 1 }, customFormats: [{ id: "cf" }], guide, guideVersions: [expect.objectContaining({ isActive: false })], ruleMigration: { previewHash: "h" } });
  });

  it("carries on with no version history and no move when either cannot be read", async () => {
    api.overview.mockResolvedValue({});
    api.fetchJson.mockResolvedValue([]);
    api.package.mockResolvedValue(guide);
    api.updateCheck.mockResolvedValue(check());
    api.versions.mockRejectedValue(new Error("no history"));
    api.migrate.mockRejectedValue(new Error("no move"));

    expect(await settingsCustomFormatsLoader()).toMatchObject({ guideVersions: [], ruleMigration: null });
  });
});

describe("the weekly check switch and the status line", () => {
  it.each([
    ["disabled", "Guide checks are off"],
    ["never-checked", "Ready for the first guide check"],
    ["up-to-date", "No tracked guide changes"],
    ["update-available", "Guide changes need review"],
    ["failed", "Guide check could not finish"]
  ])("says %s as '%s'", (status, label) => {
    load({ guideUpdateCheck: check({ status, report: null }) });
    renderPage();
    expect(screen.getByText(label)).toBeTruthy();
  });

  it("explains an error first, then the report's summary, then that no check has run, then that checks are off", () => {
    load({ guideUpdateCheck: check({ status: "failed", error: "GitHub is slow.", report: report({ summary: "Ignored." }) }) });
    const view = renderPage();
    expect(screen.getByText("GitHub is slow.")).toBeTruthy();
    view.unmount();

    load({ guideUpdateCheck: check({ report: report({ changes: [], summary: "Nothing moved." }) }) });
    const second = renderPage();
    expect(screen.getByText("Nothing moved.")).toBeTruthy();
    second.unmount();

    load({ guideUpdateCheck: check({ report: null, status: "never-checked" }) });
    const third = renderPage();
    expect(screen.getByText("No guide check has run yet.")).toBeTruthy();
    third.unmount();

    load({ guideUpdateCheck: check({ report: null, isEnabled: false, status: "disabled" }) });
    renderPage();
    expect(screen.getByText("Enable checks to allow an outbound TRaSH Guides comparison.")).toBeTruthy();
  });

  it("turns the weekly check on and off, and says so", async () => {
    load({ guideUpdateCheck: check({ isEnabled: false, report: null, status: "disabled" }) });
    api.setEnabled.mockResolvedValueOnce(check({ report: null, status: "never-checked" })).mockResolvedValueOnce(check({ isEnabled: false, report: null, status: "disabled" }));
    renderPage();
    const toggle = screen.getByRole("switch", { name: /Check upstream guide changes weekly/ });

    fireEvent.click(toggle);
    await waitFor(() => expect(toastMock.success).toHaveBeenCalledWith("Weekly guide-change checks enabled"));
    expect(api.setEnabled).toHaveBeenLastCalledWith(true);
    await waitFor(() => expect(toggle.getAttribute("aria-checked")).toBe("true"));

    fireEvent.click(toggle);
    await waitFor(() => expect(toastMock.success).toHaveBeenCalledWith("Guide-change checks disabled"));
    expect(api.setEnabled).toHaveBeenLastCalledWith(false);
  });

  it("gives the reason when the switch cannot be changed, or a plain sentence when there is none, and frees the switch again", async () => {
    load({ guideUpdateCheck: check({ isEnabled: false, report: null, status: "disabled" }) });
    api.setEnabled.mockRejectedValueOnce(new Error("Server said no.")).mockRejectedValueOnce("nope");
    renderPage();
    const toggle = screen.getByRole("switch", { name: /Check upstream guide changes weekly/ });

    fireEvent.click(toggle);
    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith("Server said no."));
    await waitFor(() => expect((toggle as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(toggle);
    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith("Could not update guide-check preference."));
  });
});

describe("running the check", () => {
  it("is off until the weekly check is on", () => {
    load({ guideUpdateCheck: check({ isEnabled: false, report: null, status: "disabled" }) });
    renderPage();
    expect((screen.getByRole("button", { name: "Check TRaSH now" }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("opens the changes it finds, shows a spinner while it works, and says the report is ready", async () => {
    load({ guideUpdateCheck: check({ report: null, status: "never-checked" }) });
    const pending = deferred<unknown>();
    api.run.mockReturnValue(pending.promise);
    renderPage();
    const button = screen.getByRole("button", { name: "Check TRaSH now" });
    expect(spinning(button)).toBe(false);

    fireEvent.click(button);
    await waitFor(() => expect(spinning(button)).toBe(true));
    await act(async () => { pending.resolve(check()); });

    expect(toastMock.success).toHaveBeenCalledWith("Guide change report ready");
    expect(screen.getByText("Review detected guide changes").closest("button")!.getAttribute("aria-expanded")).toBe("true");
    expect(spinning(button)).toBe(false);
  });

  it("opens the panel for a new upstream file alone, and keeps it shut when nothing changed", async () => {
    load({ guideUpdateCheck: check({ report: null, status: "never-checked" }) });
    api.run
      .mockResolvedValueOnce(check({ report: report({ changes: [], addedSources: [{ kind: "customFormat", mediaType: "tv", sourcePath: "tv/new.json" }] }) }))
      .mockResolvedValueOnce(check({ status: "up-to-date", report: report({ changes: [], addedSources: [] }) }))
      .mockResolvedValueOnce(check({ status: "up-to-date", report: null }));
    renderPage();

    fireEvent.click(screen.getByRole("button", { name: "Check TRaSH now" }));
    await waitFor(() => expect(screen.getByText("Review detected guide changes").closest("button")!.getAttribute("aria-expanded")).toBe("true"));

    fireEvent.click(screen.getByRole("button", { name: "Check TRaSH now" }));
    await waitFor(() => expect(toastMock.success).toHaveBeenCalledWith("Guide sources are up to date"));
    expect(screen.queryByText("Review detected guide changes")).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: "Check TRaSH now" }));
    await waitFor(() => expect(toastMock.success).toHaveBeenCalledTimes(2));
    expect(screen.queryByText("Review detected guide changes")).toBeNull();
  });

  it("gives the reason when the check fails, or a plain sentence when there is none", async () => {
    load({ guideUpdateCheck: check({ report: null, status: "never-checked" }) });
    api.run.mockRejectedValueOnce(new Error("Rate limited.")).mockRejectedValueOnce("nope");
    renderPage();

    fireEvent.click(screen.getByRole("button", { name: "Check TRaSH now" }));
    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith("Rate limited."));
    fireEvent.click(screen.getByRole("button", { name: "Check TRaSH now" }));
    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith("Could not check TRaSH Guides."));
  });
});

describe("the detected changes", () => {
  it("lists each change with whether it was removed or changed and whether a saved rule uses it, and each new upstream file", () => {
    load({
      guideUpdateCheck: check({
        report: report({
          changes: [
            change({ id: "1", name: "HDR10", isInUse: true, inUseCustomFormatIds: ["a", "b"] }),
            change({ id: "2", name: "Gone", mediaType: "tv", changeType: "removed" }),
            change({ id: "3", name: "Quiet", kind: "profile" })
          ],
          addedSources: [{ kind: "customFormat", mediaType: "tv", sourcePath: "tv/new.json" }, { kind: "profile", mediaType: "movies", sourcePath: "movies/new.json" }]
        })
      })
    });
    renderPage();
    fireEvent.click(screen.getByText("Review detected guide changes"));

    expect(screen.getByText("3 tracked change(s) · 2 new source file(s)")).toBeTruthy();
    expect(screen.getByText(/1 changed source item\(s\) affect saved custom formats/)).toBeTruthy();
    expect(screen.getByText(/used by 2 saved custom formats/)).toBeTruthy();
    expect(screen.getAllByText("Changed upstream")).toHaveLength(2);
    expect(screen.getByText("Removed upstream")).toBeTruthy();
    expect(screen.getByText(/TV · customFormat · not used by a saved custom format/)).toBeTruthy();
    expect(screen.getByText(/Movies · profile · not used by a saved custom format/)).toBeTruthy();
    expect(screen.getByText("tv/new.json")).toBeTruthy();
    expect(screen.getByText(/New upstream TV customFormat/)).toBeTruthy();
    expect(screen.getByText(/New upstream movie profile/)).toBeTruthy();
  });

  it("shows no panel when the report has nothing in it, and none without a report", () => {
    load({ guideUpdateCheck: check({ report: report({ changes: [], addedSources: [] }) }) });
    const view = renderPage();
    expect(screen.queryByText("Review detected guide changes")).toBeNull();
    view.unmount();

    load({ guideUpdateCheck: check({ report: null }) });
    renderPage();
    expect(screen.queryByText("Review detected guide changes")).toBeNull();
  });
});

describe("the sync preview and its apply", () => {
  const openPanel = () => fireEvent.click(screen.getByText("Review detected guide changes"));

  it("asks for a preview of the exact revision it found, and shows a spinner while it works", async () => {
    load();
    const pending = deferred<unknown>();
    api.previewSync.mockReturnValue(pending.promise);
    renderPage();
    openPanel();
    const button = screen.getByRole("button", { name: "Preview sync" });

    fireEvent.click(button);
    await waitFor(() => expect(spinning(button)).toBe(true));
    await act(async () => { pending.resolve(preview()); });

    expect(api.previewSync).toHaveBeenCalledWith({ expectedCurrentIntegritySha256: "current-sha", expectedUpstreamRevision: "remote-revision-0123456789" });
    expect(toastMock.success).toHaveBeenCalledWith("Guide sync preview is ready");
    expect(screen.getByText("Ready to sync guide package v4")).toBeTruthy();
    expect(screen.getByText(/Pins the source inventory to proposed-rev\./)).toBeTruthy();
    expect(screen.getByText("No guide profile compiles differently under this snapshot.")).toBeTruthy();
    expect(spinning(button)).toBe(false);
  });

  it("does nothing when the report carries no revision to preview", () => {
    load({ guideUpdateCheck: check({ report: report({ remoteRevision: "" }) }) });
    renderPage();
    openPanel();
    fireEvent.click(screen.getByRole("button", { name: "Preview sync" }));
    expect(api.previewSync).not.toHaveBeenCalled();
  });

  it("shows what is wrong, with the first problem as the toast, when the preview cannot be applied", async () => {
    load();
    api.previewSync
      .mockResolvedValueOnce(preview({ canApply: false, errors: ["The files do not match.", "Second problem."], warnings: ["Careful."] }))
      .mockResolvedValueOnce(preview({ canApply: false }));
    renderPage();
    openPanel();

    fireEvent.click(screen.getByRole("button", { name: "Preview sync" }));
    await screen.findByText("Sync preview needs attention");
    expect(toastMock.error).toHaveBeenCalledWith("The files do not match.");
    expect(screen.getByText("Second problem.")).toBeTruthy();
    expect(screen.getByText("Careful.")).toBeTruthy();
    expect(screen.getByText("Nothing has been saved. Resolve the reported problem, then run a fresh check.")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Sync reviewed snapshot" })).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: "Preview sync" }));
    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith("Could not stage a guide sync preview."));
  });

  it("shows each guide profile that would compile differently, with its reasons, warnings and plan hashes", async () => {
    load();
    api.previewSync.mockResolvedValue(preview({
      profileDiffs: [
        diff(),
        diff({ profileId: "p2", profileName: "Films UHD", currentPlanHash: null, proposedPlanHash: null, warnings: ["Review this one."], changes: ["Drops a rule."] }),
        diff({ profileId: "p3", profileName: "Untouched", changes: [] })
      ]
    }));
    renderPage();
    openPanel();
    fireEvent.click(screen.getByRole("button", { name: "Preview sync" }));

    const plan = await screen.findByLabelText("Guide profile plan diff");
    expect(within(plan).getByText("Films HD")).toBeTruthy();
    expect(within(plan).getByText("Adds a rule.")).toBeTruthy();
    expect(within(plan).getAllByText("1 → 2 rules needing review")).toHaveLength(2);
    expect(within(plan).getByText("abcdefabcdef → 123456123456")).toBeTruthy();
    expect(within(plan).getByText("Films UHD")).toBeTruthy();
    expect(within(plan).getByText("Review this one.")).toBeTruthy();
    expect(within(plan).getByText("none → none")).toBeTruthy();
    expect(within(plan).queryByText("Untouched")).toBeNull();
  });

  it("says why when the preview itself fails, or a plain sentence when there is no reason", async () => {
    load();
    api.previewSync.mockRejectedValueOnce(new Error("Upstream moved.")).mockRejectedValueOnce("nope");
    renderPage();
    openPanel();

    fireEvent.click(screen.getByRole("button", { name: "Preview sync" }));
    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith("Upstream moved."));
    fireEvent.click(screen.getByRole("button", { name: "Preview sync" }));
    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith("Could not stage a TRaSH Guides sync."));
  });

  it("applies the reviewed snapshot against the hashes it showed, then reads the guide and the check again", async () => {
    load();
    api.previewSync.mockResolvedValue(preview());
    const pending = deferred<unknown>();
    api.applySync.mockReturnValue(pending.promise);
    api.run.mockResolvedValue(check({ status: "up-to-date", report: null }));
    renderPage();
    openPanel();
    fireEvent.click(screen.getByRole("button", { name: "Preview sync" }));
    const apply = await screen.findByRole("button", { name: "Sync reviewed snapshot" });

    fireEvent.click(apply);
    await waitFor(() => expect(spinning(apply)).toBe(true));
    await act(async () => { pending.resolve({ package: { version: 4 } }); });

    expect(api.applySync).toHaveBeenCalledWith({ expectedCurrentIntegritySha256: "current-sha", expectedUpstreamRevision: "remote-revision-0123456789", expectedProposedIntegritySha256: "proposed-sha" });
    expect(revalidate).toHaveBeenCalled();
    expect(toastMock.success).toHaveBeenCalledWith("TRaSH guide snapshot synced as version 4");
    expect(screen.getByText("No tracked guide changes")).toBeTruthy();
  });

  it("keeps the earlier report when the fresh check cannot run after a sync", async () => {
    load();
    api.previewSync.mockResolvedValue(preview());
    api.applySync.mockResolvedValue({ package: { version: 4 } });
    api.run.mockRejectedValue(new Error("offline"));
    renderPage();
    openPanel();
    fireEvent.click(screen.getByRole("button", { name: "Preview sync" }));
    fireEvent.click(await screen.findByRole("button", { name: "Sync reviewed snapshot" }));

    await waitFor(() => expect(toastMock.success).toHaveBeenCalledWith("TRaSH guide snapshot synced as version 4"));
    expect(toastMock.error).not.toHaveBeenCalled();
    expect(screen.getByText("Guide changes need review")).toBeTruthy();
  });

  it("says why when the apply fails, or a plain sentence when there is no reason", async () => {
    load();
    api.previewSync.mockResolvedValue(preview());
    api.applySync.mockRejectedValueOnce(new Error("Hash changed.")).mockRejectedValueOnce("nope");
    renderPage();
    openPanel();
    fireEvent.click(screen.getByRole("button", { name: "Preview sync" }));
    fireEvent.click(await screen.findByRole("button", { name: "Sync reviewed snapshot" }));
    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith("Hash changed."));

    fireEvent.click(screen.getByRole("button", { name: "Sync reviewed snapshot" }));
    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith("Could not sync the reviewed guide snapshot."));
  });

  it("does not apply when the report loses its revision after the preview", async () => {
    load({ guideUpdateCheck: check({ isEnabled: false }) });
    api.previewSync.mockResolvedValue(preview());
    api.setEnabled.mockResolvedValue(check({ report: report({ remoteRevision: "" }) }));
    renderPage();
    openPanel();
    fireEvent.click(screen.getByRole("button", { name: "Preview sync" }));
    const apply = await screen.findByRole("button", { name: "Sync reviewed snapshot" });

    fireEvent.click(screen.getByRole("switch", { name: /Check upstream guide changes weekly/ }));
    await waitFor(() => expect(api.setEnabled).toHaveBeenCalled());
    await waitFor(() => expect((apply as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(apply);

    expect(api.applySync).not.toHaveBeenCalled();
  });
});

describe("the guide version history", () => {
  const history = () => [stored(3, { isActive: true }), stored(2)];

  it("is not shown with one version, and with several lists each with the active one marked Current", () => {
    load({ guideVersions: [stored(3, { isActive: true })] });
    const view = renderPage();
    expect(screen.queryByText("Guide version history")).toBeNull();
    view.unmount();

    load({ guideVersions: history() });
    renderPage();
    fireEvent.click(screen.getByText("Guide version history"));
    expect(screen.getByText("2 retained versions · go back to any of them")).toBeTruthy();
    expect(screen.getByText("Version 3 · in use")).toBeTruthy();
    expect(screen.getByText("Version 2")).toBeTruthy();
    expect(screen.getByText("Current")).toBeTruthy();
    expect(screen.getByText("rev-2-abcdef · sha-2-abcdef")).toBeTruthy();
    expect(screen.getAllByRole("button", { name: "Go back to this version" })).toHaveLength(1);
  });

  it("goes back to a retained version, reads the history and the guide again, and says so", async () => {
    load({ guideVersions: history() });
    api.activate.mockResolvedValue(undefined);
    api.versions.mockResolvedValue([stored(3), stored(2, { isActive: true })]);
    renderPage();
    fireEvent.click(screen.getByText("Guide version history"));
    const back = screen.getByRole("button", { name: "Go back to this version" });

    fireEvent.click(back);

    await waitFor(() => expect(toastMock.success).toHaveBeenCalledWith("Guide package version 2 is in use again"));
    expect(api.activate).toHaveBeenCalledWith(2, "pkg-2");
    expect(revalidate).toHaveBeenCalled();
    expect(await screen.findByText("Version 2 · in use")).toBeTruthy();
  });

  it("shows a spinner while it goes back", async () => {
    load({ guideVersions: history() });
    const pending = deferred<unknown>();
    api.activate.mockReturnValue(pending.promise);
    api.versions.mockResolvedValue(history());
    renderPage();
    fireEvent.click(screen.getByText("Guide version history"));
    const back = screen.getByRole("button", { name: "Go back to this version" });

    fireEvent.click(back);
    await waitFor(() => expect(spinning(back)).toBe(true));
    await act(async () => { pending.resolve(undefined); });
    await waitFor(() => expect(spinning(back)).toBe(false));
  });

  it("says why it could not go back, or a plain sentence when there is no reason", async () => {
    load({ guideVersions: history() });
    api.activate.mockRejectedValueOnce(new Error("Version is gone.")).mockRejectedValueOnce("nope");
    renderPage();
    fireEvent.click(screen.getByText("Guide version history"));

    fireEvent.click(screen.getByRole("button", { name: "Go back to this version" }));
    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith("Version is gone."));
    fireEvent.click(screen.getByRole("button", { name: "Go back to this version" }));
    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith("Could not go back to that guide version."));
  });
});

describe("rules from the old guide catalogue", () => {
  const migration = (items: unknown[]) => ({ packageVersion: 3, items, previewHash: "preview-hash" });
  const openMove = async () => {
    fireEvent.click(screen.getByRole("button", { name: "Review the move" }));
    return await screen.findByRole("dialog");
  };

  it("is not offered when there is nothing to move", () => {
    load({ ruleMigration: migration([]) });
    renderPage();
    expect(screen.queryByText("Rules from the old guide catalogue")).toBeNull();
  });

  it("says one rule, or how many rules, were made from the old catalogue", () => {
    load({ ruleMigration: migration([migrationItem()]) });
    const view = renderPage();
    expect(screen.getByText("1 to review")).toBeTruthy();
    expect(screen.getByText(/One rule was made from Deluno's earlier guide catalogue.*Moving it onto/)).toBeTruthy();
    view.unmount();

    load({ ruleMigration: migration([migrationItem(), migrationItem({ ruleId: "r2" })]) });
    renderPage();
    expect(screen.getByText("2 to review")).toBeTruthy();
    expect(screen.getByText(/2 rules were made from Deluno's earlier guide catalogue.*Moving them onto/)).toBeTruthy();
  });

  it("lists each rule with what it becomes, who uses it and whether its conditions were edited, and chooses the unedited ones", async () => {
    load({
      ruleMigration: migration([
        migrationItem({ profileNames: ["Films HD", "Films UHD"] }),
        migrationItem({ ruleId: "r2", mediaType: "tv", currentName: "Old DV", newName: "Dolby Vision", conditionsWereEdited: true })
      ])
    });
    renderPage();
    const dialog = await openMove();

    expect(within(dialog).getByText("1 of 2 chosen")).toBeTruthy();
    expect(within(dialog).getByText("Used by Films HD, Films UHD")).toBeTruthy();
    expect(within(dialog).getByText("Your conditions differ from what Deluno shipped")).toBeTruthy();
    expect(within(dialog).getByRole("checkbox", { name: "Move Old HDR (Movies)" })).toHaveProperty("checked", true);
    expect(within(dialog).getByRole("checkbox", { name: /Move Old DV/ })).toHaveProperty("checked", false);
    expect(within(dialog).getByRole("button", { name: "Move 1 rule" })).toBeTruthy();
  });

  it("moves the chosen rules with the preview's hash and says each move is recorded", async () => {
    load({ ruleMigration: migration([migrationItem(), migrationItem({ ruleId: "r2", currentName: "Old DV", conditionsWereEdited: true })]) });
    api.applyMigration.mockResolvedValue({ outcomes: [{ ruleId: "r1", name: "Old HDR", moved: true, message: "" }], moved: 1, skipped: 0 });
    renderPage();
    const dialog = await openMove();
    fireEvent.click(within(dialog).getByRole("checkbox", { name: "Move Old DV (Movies)" }));
    expect(within(dialog).getByText("2 of 2 chosen")).toBeTruthy();
    fireEvent.click(within(dialog).getByRole("checkbox", { name: "Move Old DV (Movies)" }));

    fireEvent.click(within(dialog).getByRole("button", { name: "Move 1 rule" }));

    expect(await within(dialog).findByText("Moved 1 rule; each move is recorded in Activity.")).toBeTruthy();
    expect(api.applyMigration).toHaveBeenCalledWith("preview-hash", ["r1"]);
    expect(revalidate).toHaveBeenCalled();
  });

  it("names each rule that was not moved, and why", async () => {
    load({ ruleMigration: migration([migrationItem(), migrationItem({ ruleId: "r2", currentName: "Old DV" })]) });
    api.applyMigration.mockResolvedValue({
      outcomes: [{ ruleId: "r1", name: "Old HDR", moved: true, message: "" }, { ruleId: "r2", name: "Old DV", moved: false, message: "A rule with that name exists." }],
      moved: 1, skipped: 1
    });
    renderPage();
    const dialog = await openMove();

    fireEvent.click(within(dialog).getByRole("button", { name: "Move 2 rules" }));

    expect(await within(dialog).findByText(/Not moved: Old DV \(A rule with that name exists\.\)/)).toBeTruthy();
  });

  it("says why the move failed, or a plain sentence when there is no reason", async () => {
    load({ ruleMigration: migration([migrationItem()]) });
    api.applyMigration.mockRejectedValueOnce(new Error("The preview is stale.")).mockRejectedValueOnce("nope");
    renderPage();
    const dialog = await openMove();

    fireEvent.click(within(dialog).getByRole("button", { name: "Move 1 rule" }));
    expect(await within(dialog).findByText("The preview is stale.")).toBeTruthy();
    fireEvent.click(within(dialog).getByRole("button", { name: "Move 1 rule" }));
    expect(await within(dialog).findByText("The rules could not be moved.")).toBeTruthy();
  });

  it("cannot move with nothing chosen, even when asked directly, nor once the move has gone from the page", async () => {
    load({ ruleMigration: migration([migrationItem()]) });
    const view = renderPage();
    const dialog = await openMove();
    fireEvent.click(within(dialog).getByRole("checkbox", { name: "Move Old HDR (Movies)" }));
    expect((within(dialog).getByRole("button", { name: "Move 0 rules" }) as HTMLButtonElement).disabled).toBe(true);

    await act(async () => { (seen.drawers.get("Move rules onto TRaSH's formats")!.footer!.props.onSave as () => void)(); });
    expect(api.applyMigration).not.toHaveBeenCalled();

    load({ ruleMigration: null });
    view.rerender(<MemoryRouter><SettingsCustomFormatsPage /></MemoryRouter>);
    expect(within(dialog).getByText("Nothing left to move")).toBeTruthy();
    await act(async () => { (seen.drawers.get("Move rules onto TRaSH's formats")!.footer!.props.onSave as () => void)(); });
    expect(api.applyMigration).not.toHaveBeenCalled();
  });

  it("closes from its cancel button, and ignores a request to open that is already open", async () => {
    load({ ruleMigration: migration([migrationItem()]) });
    renderPage();
    await openMove();
    act(() => seen.drawers.get("Move rules onto TRaSH's formats")!.onOpenChange(true));
    expect(screen.queryByRole("dialog")).toBeTruthy();
    act(() => seen.drawers.get("Move rules onto TRaSH's formats")!.onOpenChange(false));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    await openMove();

    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });
});
