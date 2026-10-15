import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { DownloadHealthRule } from "../lib/api";
import { emptyPlatformSettingsSnapshot } from "../lib/api/types/settings";

/**
 * Automation and Recovery, Download health: the strike rules and the cards beside them. Each control sends what the server's
 * routes are tested against (the download-health route tests on the real host), shows the numbers back in the units they were
 * typed in, and refuses what the server would refuse before anything is sent.
 */

const authedFetchMock = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));

const toastSuccess = vi.fn();
const toastError = vi.fn();
vi.mock("../components/shell/toaster", async (importOriginal) => {
  const original = await importOriginal<typeof import("../components/shell/toaster")>();
  return {
    ...original,
    toast: Object.assign(() => undefined, { success: (...args: unknown[]) => toastSuccess(...args), error: (...args: unknown[]) => toastError(...args), message: vi.fn(), info: vi.fn(), warning: vi.fn(), dismiss: vi.fn() })
  };
});

const revalidateMock = vi.fn();
let loaderData: { rules: DownloadHealthRule[]; ignoreEntries: unknown[]; settings: typeof emptyPlatformSettingsSnapshot; connectivityCheckSkips: unknown[] };

vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate: revalidateMock, state: "idle" })
}));

const { SettingsDownloadHealthPage } = await import("./settings-download-health-page");

vi.setConfig({ testTimeout: 20_000 });

function rule(overrides: Partial<DownloadHealthRule> = {}): DownloadHealthRule {
  return {
    id: "r1", kind: "no-progress", enabled: true, strikeLimit: 3, timerMinutes: 30, appliesTo: "both",
    minCompletionPercent: 0, maxCompletionPercent: 100, resetStrikesOnProgress: true, actionPreset: "replace",
    createdUtc: "2026-10-01T00:00:00Z", updatedUtc: "2026-10-01T00:00:00Z", threshold: 0, privacyScope: "both",
    tooSlowMaxTotalMinutes: null, ignoreAboveSizeBytes: null, skipWhileAlternateSpeedLimitOn: false,
    resetStrikesMinimumProgressBytes: null, privateAction: "remove", blocklistWhenMoved: true,
    ...overrides
  };
}

function reply(body: unknown = {}, ok = true, status = 200) {
  return Promise.resolve({ ok, status, json: () => Promise.resolve(body), clone() { return this; }, text: () => Promise.resolve(JSON.stringify(body)) } as unknown as Response);
}

function renderPage(rules: DownloadHealthRule[] = [], settings = emptyPlatformSettingsSnapshot, ignoreEntries: unknown[] = []) {
  loaderData = { rules, ignoreEntries, settings, connectivityCheckSkips: [] };
  return render(
    <MemoryRouter>
      <SettingsDownloadHealthPage />
    </MemoryRouter>
  );
}

const calls = (method: string, path: string) =>
  authedFetchMock.mock.calls.filter(([url, init]) => String(url) === path && ((init as RequestInit | undefined)?.method ?? "GET") === method);
const bodyOf = (call: unknown[]) => JSON.parse(String((call[1] as RequestInit).body));

beforeEach(() => {
  authedFetchMock.mockReset();
  toastSuccess.mockReset();
  toastError.mockReset();
  revalidateMock.mockReset();
  authedFetchMock.mockImplementation(() => reply({}));
});

afterEach(() => {
  vi.restoreAllMocks();
});

/** The Save button of the card that holds this field: the nearest ancestor with exactly one. */
function saveFor(field: HTMLElement) {
  let node: HTMLElement | null = field;
  while (node) {
    const buttons = within(node).queryAllByRole("button", { name: "Save" });
    if (buttons.length === 1) return buttons[0];
    node = node.parentElement;
  }
  throw new Error("No Save button for that field");
}

async function openNewRule() {
  fireEvent.click(screen.getAllByRole("button", { name: "New rule" })[0]);
  return within(await screen.findByRole("dialog"));
}

describe("the rule list and its drawer", () => {
  it("clicking a rule opens it for editing: the title is the kind, Save rule replaces Create rule and a Delete section appears", async () => {
    renderPage([rule({ kind: "too-slow", threshold: 500 })]);

    fireEvent.click(screen.getByText("Too slow"));

    const drawer = within(await screen.findByRole("dialog"));
    expect(drawer.getAllByText("Too slow").length).toBeGreaterThan(0);
    expect(drawer.getByRole("button", { name: "Save rule" })).toBeVisible();
    expect(drawer.queryByRole("button", { name: "Create rule" })).not.toBeInTheDocument();
    expect(drawer.getByText("Delete this rule")).toBeVisible();
  });

  it("a new rule starts with Create rule, no Delete section and a Just tell me action", async () => {
    renderPage();

    const drawer = await openNewRule();

    expect(drawer.getByText("New download health rule")).toBeVisible();
    expect(drawer.getByRole("button", { name: "Create rule" })).toBeVisible();
    expect(drawer.queryByText("Delete this rule")).not.toBeInTheDocument();
    expect(drawer.getByLabelText("When it strikes")).toHaveValue("just-tell-me");
  });

  it("Create rule puts the whole rule at /rules/new and reads the page again", async () => {
    renderPage();
    const drawer = await openNewRule();
    fireEvent.change(drawer.getByLabelText("Strikes before it acts"), { target: { value: "5" } });
    fireEvent.change(drawer.getByLabelText("When it strikes"), { target: { value: "replace" } });

    fireEvent.click(drawer.getByRole("button", { name: "Create rule" }));

    await waitFor(() => expect(calls("PUT", "/api/download-health/rules/new")).toHaveLength(1));
    expect(bodyOf(calls("PUT", "/api/download-health/rules/new")[0])).toMatchObject({
      kind: "no-progress", enabled: true, strikeLimit: 5, appliesTo: "both", minCompletionPercent: 0, maxCompletionPercent: 100,
      actionPreset: "replace", privacyScope: "both", resetStrikesOnProgress: true
    });
    await waitFor(() => expect(revalidateMock).toHaveBeenCalled());
  });

  it("Save rule puts the changed rule at its own id", async () => {
    renderPage([rule()]);
    fireEvent.click(screen.getByText("No progress"));
    const drawer = within(await screen.findByRole("dialog"));
    fireEvent.change(drawer.getByLabelText("Strikes before it acts"), { target: { value: "7" } });

    fireEvent.click(drawer.getByRole("button", { name: "Save rule" }));

    await waitFor(() => expect(calls("PUT", "/api/download-health/rules/r1")).toHaveLength(1));
    expect(bodyOf(calls("PUT", "/api/download-health/rules/r1")[0])).toMatchObject({ kind: "no-progress", strikeLimit: 7 });
  });

  it("shows the server's own reason in the footer when a rule is refused, for example an overlap", async () => {
    authedFetchMock.mockImplementation((_path: string, init?: RequestInit) => init?.method === "PUT"
      ? reply({ errors: { rule: ["It overlaps the \"No progress\" rule for torrents between 0% and 100%."] } }, false, 400)
      : reply([]));
    renderPage();
    const drawer = await openNewRule();

    fireEvent.click(drawer.getByRole("button", { name: "Create rule" }));

    expect(await drawer.findByText("It overlaps the \"No progress\" rule for torrents between 0% and 100%.")).toBeVisible();
    expect(revalidateMock).not.toHaveBeenCalled();
  });

  it("Delete asks first by kind and then removes the rule and says so", async () => {
    renderPage([rule()]);
    fireEvent.click(screen.getByText("No progress"));
    const drawer = within(await screen.findByRole("dialog"));

    fireEvent.click(drawer.getByRole("button", { name: "Delete" }));

    expect(await screen.findByText("Delete the \"No progress\" rule?")).toBeVisible();
    expect(calls("DELETE", "/api/download-health/rules/r1")).toHaveLength(0);
    fireEvent.click(screen.getByRole("button", { name: "Delete rule" }));
    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("No progress rule removed"));
    expect(calls("DELETE", "/api/download-health/rules/r1")).toHaveLength(1);
  });

  it("the Rule enabled switch puts the rule off and keeps it", async () => {
    renderPage([rule()]);
    fireEvent.click(screen.getByText("No progress"));
    const drawer = within(await screen.findByRole("dialog"));
    const label = drawer.getByText("Rule enabled").closest("label");
    const toggle = document.getElementById(label!.htmlFor)!;

    fireEvent.click(toggle);
    fireEvent.click(drawer.getByRole("button", { name: "Save rule" }));

    await waitFor(() => expect(calls("PUT", "/api/download-health/rules/r1")).toHaveLength(1));
    expect(bodyOf(calls("PUT", "/api/download-health/rules/r1")[0])).toMatchObject({ enabled: false, kind: "no-progress", strikeLimit: 3 });
  });

  it("offers Torrents and Usenet, Torrents only and Usenet only, and sends the one chosen", async () => {
    renderPage();
    const drawer = await openNewRule();
    const select = drawer.getByLabelText("Applies to") as HTMLSelectElement;

    expect(Array.from(select.options).map((option) => option.textContent)).toEqual(["Torrents and Usenet", "Torrents only", "Usenet only"]);
    fireEvent.change(select, { target: { value: "usenet" } });
    fireEvent.click(drawer.getByRole("button", { name: "Create rule" }));

    await waitFor(() => expect(calls("PUT", "/api/download-health/rules/new")).toHaveLength(1));
    expect(bodyOf(calls("PUT", "/api/download-health/rules/new")[0])).toMatchObject({ appliesTo: "usenet" });
  });
});

describe("what a rule is checked against before it is sent", () => {
  it("refuses strikes outside 1 to 100 and a completion range that runs backwards, and sends nothing", async () => {
    renderPage();
    const drawer = await openNewRule();
    fireEvent.change(drawer.getByLabelText("Strikes before it acts"), { target: { value: "0" } });
    fireEvent.change(drawer.getByLabelText("From this % complete"), { target: { value: "80" } });
    fireEvent.change(drawer.getByLabelText("Up to this % complete"), { target: { value: "20" } });

    fireEvent.click(drawer.getByRole("button", { name: "Create rule" }));

    expect(await drawer.findByText("Strikes must be between 1 and 100.")).toBeVisible();
    expect(drawer.getByText("The completion range must be between 0% and 100%, with the low end at or below the high end.")).toBeVisible();
    expect(calls("PUT", "/api/download-health/rules/new")).toHaveLength(0);
  });

  it.each([
    ["too-slow", "Slower than", "0", "Set a speed floor above zero."],
    ["low-peers", "Fewer than", "0", "Set a peer floor of at least 1."],
    ["dead-torrent", "Grace period", "2000", "The grace period must be between 0 minutes and 24 hours."]
  ])("a %s rule needs a sensible %s", async (kind, label, value, message) => {
    renderPage();
    const drawer = await openNewRule();
    fireEvent.change(drawer.getByLabelText("Kind"), { target: { value: kind } });
    fireEvent.change(drawer.getByLabelText(label), { target: { value } });

    fireEvent.click(drawer.getByRole("button", { name: "Create rule" }));

    expect(await drawer.findByText(message)).toBeVisible();
    expect(calls("PUT", "/api/download-health/rules/new")).toHaveLength(0);
  });

  it("each kind asks for its own threshold: speed, peers or a grace period, and No progress asks for none", async () => {
    renderPage();
    const drawer = await openNewRule();

    expect(drawer.queryByLabelText("Slower than")).not.toBeInTheDocument();
    fireEvent.change(drawer.getByLabelText("Kind"), { target: { value: "too-slow" } });
    expect(drawer.getByLabelText("Slower than")).toBeVisible();
    expect(drawer.getByLabelText("Speed unit")).toBeVisible();
    fireEvent.change(drawer.getByLabelText("Kind"), { target: { value: "low-peers" } });
    expect(drawer.getByLabelText("Fewer than")).toBeVisible();
    fireEvent.change(drawer.getByLabelText("Kind"), { target: { value: "dead-torrent" } });
    expect(drawer.getByLabelText("Grace period")).toBeVisible();
    fireEvent.change(drawer.getByLabelText("Kind"), { target: { value: "no-progress" } });
    expect(drawer.queryByLabelText("Grace period")).not.toBeInTheDocument();
  });

  it("a speed typed in MB/s is saved as KB/s times a thousand, and a whole MB/s shows back in MB/s", async () => {
    renderPage([rule({ id: "slow", kind: "too-slow", threshold: 2000 })]);
    fireEvent.click(screen.getByText("Too slow"));
    const drawer = within(await screen.findByRole("dialog"));

    expect(drawer.getByLabelText("Slower than")).toHaveValue(2);
    expect(drawer.getByLabelText("Speed unit")).toHaveValue("mb");
    fireEvent.change(drawer.getByLabelText("Slower than"), { target: { value: "3" } });
    fireEvent.click(drawer.getByRole("button", { name: "Save rule" }));

    await waitFor(() => expect(calls("PUT", "/api/download-health/rules/slow")).toHaveLength(1));
    expect(bodyOf(calls("PUT", "/api/download-health/rules/slow")[0])).toMatchObject({ kind: "too-slow", threshold: 3000 });
  });

  it("a speed that is not a whole number of MB/s shows in KB/s", async () => {
    renderPage([rule({ id: "slow", kind: "too-slow", threshold: 1500 })]);
    fireEvent.click(screen.getByText("Too slow"));
    const drawer = within(await screen.findByRole("dialog"));

    expect(drawer.getByLabelText("Slower than")).toHaveValue(1500);
    expect(drawer.getByLabelText("Speed unit")).toHaveValue("kb");
  });

  it("Too slow leaves the total time and the size ceiling blank, meaning none, and sends neither", async () => {
    renderPage();
    const drawer = await openNewRule();
    fireEvent.change(drawer.getByLabelText("Kind"), { target: { value: "too-slow" } });
    fireEvent.change(drawer.getByLabelText("Slower than"), { target: { value: "500" } });
    const [maximumTotalTime, ignoreAboveSize] = drawer.getAllByPlaceholderText("No ceiling");
    expect(maximumTotalTime).toHaveValue(null);
    expect(ignoreAboveSize).toHaveValue(null);

    fireEvent.click(drawer.getByRole("button", { name: "Create rule" }));

    await waitFor(() => expect(calls("PUT", "/api/download-health/rules/new")).toHaveLength(1));
    expect(bodyOf(calls("PUT", "/api/download-health/rules/new")[0])).toMatchObject({ tooSlowMaxTotalMinutes: null, ignoreAboveSizeBytes: null });
  });

  it("Too slow sends a total time in minutes and the size ceiling in bytes", async () => {
    renderPage();
    const drawer = await openNewRule();
    fireEvent.change(drawer.getByLabelText("Kind"), { target: { value: "too-slow" } });
    fireEvent.change(drawer.getByLabelText("Slower than"), { target: { value: "500" } });
    const [maximumTotalTime, ignoreAboveSize] = drawer.getAllByPlaceholderText("No ceiling");
    fireEvent.change(maximumTotalTime, { target: { value: "120" } });
    fireEvent.change(ignoreAboveSize, { target: { value: "20000" } });

    fireEvent.click(drawer.getByRole("button", { name: "Create rule" }));

    await waitFor(() => expect(calls("PUT", "/api/download-health/rules/new")).toHaveLength(1));
    expect(bodyOf(calls("PUT", "/api/download-health/rules/new")[0])).toMatchObject({ tooSlowMaxTotalMinutes: 120, ignoreAboveSizeBytes: 20_000_000_000 });
  });

  it("refuses a total time over 30 days with its own message", async () => {
    renderPage();
    const drawer = await openNewRule();
    fireEvent.change(drawer.getByLabelText("Kind"), { target: { value: "too-slow" } });
    fireEvent.change(drawer.getByLabelText("Slower than"), { target: { value: "500" } });
    fireEvent.change(drawer.getAllByPlaceholderText("No ceiling")[0], { target: { value: "50000" } });

    fireEvent.click(drawer.getByRole("button", { name: "Create rule" }));

    expect(await drawer.findByText("The maximum total time must be between 1 minute and 30 days.")).toBeVisible();
  });

  it("Reset strikes on progress and the minimum progress to reset belong to No progress alone", async () => {
    renderPage();
    const drawer = await openNewRule();

    expect(drawer.getByText("Reset strikes on progress")).toBeVisible();
    expect(drawer.getByLabelText("Minimum progress to reset")).toBeVisible();
    fireEvent.change(drawer.getByLabelText("Kind"), { target: { value: "low-peers" } });
    expect(drawer.queryByText("Reset strikes on progress")).not.toBeInTheDocument();
    expect(drawer.queryByLabelText("Minimum progress to reset")).not.toBeInTheDocument();
  });

  it("says in the person's own numbers when strike one lands and when the download is removed", async () => {
    renderPage();
    const drawer = await openNewRule();

    fireEvent.change(drawer.getByLabelText("Strikes before it acts"), { target: { value: "1" } });
    expect(drawer.getByText(/With 1 strike the download is removed at that same check\./)).toBeVisible();
    fireEvent.change(drawer.getByLabelText("Strikes before it acts"), { target: { value: "4" } });
    expect(drawer.getByText(/with 4 strikes it is removed about 15 min later/)).toBeVisible();
  });
});

describe("Ignored downloads", () => {
  it("Add needs at least a hash, a category, a tag or a tracker domain and says so when it has none", () => {
    renderPage();

    fireEvent.click(screen.getByRole("button", { name: "Add" }));

    expect(toastError).toHaveBeenCalledWith("Give at least a hash, a client category, a tag, or a tracker domain to ignore.");
    expect(calls("POST", "/api/download-health/ignore-list")).toHaveLength(0);
  });

  it("adding with a note posts the entry, clears the form and says it was added", async () => {
    renderPage();
    fireEvent.change(screen.getByPlaceholderText("Hash"), { target: { value: "abc123" } });
    fireEvent.change(screen.getByPlaceholderText("Note (optional)"), { target: { value: "Keep seeding this" } });

    fireEvent.click(screen.getByRole("button", { name: "Add" }));

    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Added to the ignore list"));
    expect(bodyOf(calls("POST", "/api/download-health/ignore-list")[0])).toEqual({ hash: "abc123", category: null, trackerDomain: null, tag: null, note: "Keep seeding this" });
    expect(screen.getByPlaceholderText("Hash")).toHaveValue("");
    expect(screen.getByPlaceholderText("Note (optional)")).toHaveValue("");
  });

  it("an entry can be removed and the page says so", async () => {
    renderPage([], emptyPlatformSettingsSnapshot, [{ id: "ig1", hash: "abc123", category: null, trackerDomain: null, tag: null, note: "Keep seeding this", createdUtc: "2026-10-01T00:00:00Z" }]);

    fireEvent.click(screen.getByRole("button", { name: "Remove" }));

    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Removed from the ignore list"));
    expect(calls("DELETE", "/api/download-health/ignore-list/ig1")).toHaveLength(1);
  });
});

describe("the cards beside the rules", () => {
  it("Dry run patches downloadHealthDryRunEnabled and puts the switch back with a message when it fails", async () => {
    authedFetchMock.mockImplementation((_path: string, init?: RequestInit) => init?.method === "PATCH" ? reply({}, false, 500) : reply([]));
    renderPage();
    const row = screen.getByText("Dry run", { selector: "label *, label" }).closest("label")!;
    const toggle = document.getElementById(row.htmlFor)!;

    fireEvent.click(toggle);

    await waitFor(() => expect(calls("PATCH", "/api/settings")).toHaveLength(1));
    expect(bodyOf(calls("PATCH", "/api/settings")[0])).toEqual({ downloadHealthDryRunEnabled: true });
    expect(await screen.findByText("Could not save. Try again.")).toBeVisible();
    expect(toggle).toHaveAttribute("aria-checked", "false");
  });

  it("the strike inactivity window keeps Save closed until changed and refuses a number outside 1 to 720 hours", async () => {
    renderPage();
    const input = screen.getByLabelText("Clear strikes after this long without a new one");
    const card = screen.getByText(/strike inactivity window/i).closest("section, div")!.parentElement!;
    const save = within(card).getAllByRole("button", { name: "Save" })[0];

    expect(save).toBeDisabled();
    fireEvent.change(input, { target: { value: "800" } });
    expect(save).toBeEnabled();
    fireEvent.click(save);

    expect(await screen.findByText("Must be between 1 and 720 hours.")).toBeVisible();
    expect(calls("PATCH", "/api/settings")).toHaveLength(0);
    fireEvent.change(input, { target: { value: "48" } });
    fireEvent.click(save);
    await waitFor(() => expect(calls("PATCH", "/api/settings")).toHaveLength(1));
    expect(bodyOf(calls("PATCH", "/api/settings")[0])).toEqual({ downloadHealthStrikeInactivityHours: 48 });
  });

  it("Timing refuses what is out of range with each field's own message and saves the three numbers together", async () => {
    renderPage();
    fireEvent.change(screen.getByLabelText("Check every"), { target: { value: "2000" } });
    fireEvent.change(screen.getByLabelText("Strikes before a stalled download is acted on"), { target: { value: "101" } });
    fireEvent.change(screen.getByLabelText("Speed chart sampling"), { target: { value: "90" } });

    fireEvent.click(screen.getByRole("button", { name: "Save timing" }));

    expect(await screen.findByText("Must be between 1 and 1440 minutes.")).toBeVisible();
    expect(screen.getByText("Must be between 1 and 100.")).toBeVisible();
    expect(screen.getByText("Must be between 1 and 60 minutes.")).toBeVisible();
    expect(calls("PATCH", "/api/settings")).toHaveLength(0);

    fireEvent.change(screen.getByLabelText("Check every"), { target: { value: "10" } });
    fireEvent.change(screen.getByLabelText("Strikes before a stalled download is acted on"), { target: { value: "4" } });
    fireEvent.change(screen.getByLabelText("Speed chart sampling"), { target: { value: "15" } });
    fireEvent.click(screen.getByRole("button", { name: "Save timing" }));

    await waitFor(() => expect(calls("PATCH", "/api/settings")).toHaveLength(1));
    expect(bodyOf(calls("PATCH", "/api/settings")[0])).toEqual({
      downloadHealthDefaultStrikeWindowMinutes: 10, downloadHealthStrikeThreshold: 4, downloadThroughputSamplerIntervalMinutes: 15
    });
  });

  it("History retention refuses a number outside its range and saves both periods together", async () => {
    renderPage();
    fireEvent.change(screen.getByLabelText("Keep Activity and strike history for"), { target: { value: "4000" } });

    fireEvent.click(saveFor(screen.getByLabelText("Keep Activity and strike history for")));

    expect(await screen.findByText("Must be between 1 and 3650 days.")).toBeVisible();
    expect(calls("PATCH", "/api/settings")).toHaveLength(0);
    fireEvent.change(screen.getByLabelText("Keep Activity and strike history for"), { target: { value: "90" } });
    fireEvent.change(screen.getByLabelText("Keep unresolved import-recovery cases for"), { target: { value: "60" } });
    fireEvent.click(saveFor(screen.getByLabelText("Keep Activity and strike history for")));

    await waitFor(() => expect(calls("PATCH", "/api/settings")).toHaveLength(1));
    expect(bodyOf(calls("PATCH", "/api/settings")[0])).toEqual({ historyRetentionDays: 90, importRecoveryRetentionDays: 60 });
  });

  it("the connectivity check switch patches the setting, and the URLs save on their own", async () => {
    renderPage();
    const row = screen.getByText("Check before every pass").closest("label")!;
    fireEvent.click(document.getElementById(row.htmlFor)!);

    await waitFor(() => expect(calls("PATCH", "/api/settings")).toHaveLength(1));
    expect(bodyOf(calls("PATCH", "/api/settings")[0])).toEqual({ downloadHealthConnectivityCheckEnabled: true });
    expect(screen.getByText("Has not skipped a pass.")).toBeVisible();
  });

  it("Purge all strikes asks first and then says how many were cleared", async () => {
    authedFetchMock.mockImplementation((path: string) => String(path).endsWith("purge-strikes") ? reply({ purgedCount: 4 }) : reply([]));
    renderPage();

    fireEvent.click(screen.getByRole("button", { name: "Purge all strikes" }));
    expect(await screen.findByText("Purge every recorded strike?")).toBeVisible();
    expect(calls("POST", "/api/download-health/purge-strikes")).toHaveLength(0);
    fireEvent.click(screen.getAllByRole("button", { name: "Purge all strikes" }).at(-1)!);

    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Cleared 4 recorded strikes"));
    expect(calls("POST", "/api/download-health/purge-strikes")).toHaveLength(1);
  });
});
