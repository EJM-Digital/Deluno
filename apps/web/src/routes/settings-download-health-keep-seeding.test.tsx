import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { DownloadHealthRule } from "../lib/api";
import { emptyPlatformSettingsSnapshot } from "../lib/api/types/settings";

/**
 * #1240: a strike rule that removes torrents can instead move a private (or tagged) one to the keep-seeding
 * category. The choice is only offered where it can work, defaults to Remove, and is saved with the rule.
 */

const authedFetchMock = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));

function rule(overrides: Partial<DownloadHealthRule>): DownloadHealthRule {
  return {
    id: "r1", kind: "no-progress", enabled: true, strikeLimit: 3, timerMinutes: 30, appliesTo: "both",
    minCompletionPercent: 0, maxCompletionPercent: 100, resetStrikesOnProgress: true, actionPreset: "replace",
    createdUtc: "2026-10-01T00:00:00Z", updatedUtc: "2026-10-01T00:00:00Z", threshold: 0, privacyScope: "both",
    tooSlowMaxTotalMinutes: null, ignoreAboveSizeBytes: null, skipWhileAlternateSpeedLimitOn: false,
    resetStrikesMinimumProgressBytes: null, privateAction: "remove", blocklistWhenMoved: true,
    ...overrides
  };
}

let loaderData: { rules: DownloadHealthRule[]; ignoreEntries: never[]; settings: typeof emptyPlatformSettingsSnapshot; connectivityCheckSkips: never[] };

vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate: vi.fn(), state: "idle" })
}));

const { SettingsDownloadHealthPage } = await import("./settings-download-health-page");

function renderPage(rules: DownloadHealthRule[]) {
  loaderData = { rules, ignoreEntries: [], settings: emptyPlatformSettingsSnapshot, connectivityCheckSkips: [] };
  return render(
    <MemoryRouter>
      <SettingsDownloadHealthPage />
    </MemoryRouter>
  );
}

function savedBody() {
  const call = authedFetchMock.mock.calls.find(([, init]) => (init as RequestInit | undefined)?.method === "PUT");
  return JSON.parse((call?.[1] as RequestInit).body as string);
}

describe("a rule can keep a private torrent seeding instead of removing it (#1240)", () => {
  afterEach(() => {
    authedFetchMock.mockReset();
  });

  it("offers nothing new on a rule that does not remove torrents, and a new rule starts by removing", async () => {
    authedFetchMock.mockImplementation(() => Promise.resolve({ ok: true, json: () => Promise.resolve([]) } as Response));
    renderPage([]);

    fireEvent.click(screen.getAllByRole("button", { name: "New rule" })[0]);
    const drawer = within(await screen.findByRole("dialog"));

    // A new rule starts at "Just tell me": there is no removal to replace.
    expect(drawer.queryByLabelText("If it is from a private tracker")).not.toBeInTheDocument();
    fireEvent.change(drawer.getByLabelText("When it strikes"), { target: { value: "replace" } });
    const choice = drawer.getByLabelText("If it is from a private tracker") as HTMLSelectElement;
    expect(choice.value).toBe("remove");
    expect(drawer.queryByText("Also block this release")).not.toBeInTheDocument();
  });

  it("saves a move with its block switch, and says what happens to a private torrent with no category", async () => {
    authedFetchMock.mockImplementation(() => Promise.resolve({ ok: true, json: () => Promise.resolve([]) } as Response));
    renderPage([]);

    fireEvent.click(screen.getAllByRole("button", { name: "New rule" })[0]);
    const drawer = within(await screen.findByRole("dialog"));
    fireEvent.change(drawer.getByLabelText("When it strikes"), { target: { value: "replace" } });
    fireEvent.change(drawer.getByLabelText("If it is from a private tracker"), { target: { value: "move-and-replace" } });

    expect(drawer.getByText("Also block this release")).toBeInTheDocument();
    expect(drawer.getByText(/left alone, and Deluno says so on System, Health/i)).toBeInTheDocument();
    expect(drawer.getByRole("link", { name: "Automation & Recovery, Keep Seeding" })).toHaveAttribute("href", "/search-cycles/keep-seeding");

    fireEvent.click(drawer.getByRole("button", { name: "Create rule" }));

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalled());
    expect(savedBody()).toMatchObject({ actionPreset: "replace", privateAction: "move-and-replace", blocklistWhenMoved: true });
  });

  it("drops the move the moment the action no longer removes torrents", async () => {
    authedFetchMock.mockImplementation(() => Promise.resolve({ ok: true, json: () => Promise.resolve([]) } as Response));
    renderPage([rule({ privateAction: "move-and-replace" })]);

    fireEvent.click(screen.getByText("No progress"));
    const drawer = within(await screen.findByRole("dialog"));
    expect((drawer.getByLabelText("If it is from a private tracker") as HTMLSelectElement).value).toBe("move-and-replace");

    fireEvent.change(drawer.getByLabelText("When it strikes"), { target: { value: "retry-elsewhere" } });
    expect(drawer.queryByLabelText("If it is from a private tracker")).not.toBeInTheDocument();
    fireEvent.change(drawer.getByLabelText("When it strikes"), { target: { value: "replace" } });
    expect((drawer.getByLabelText("If it is from a private tracker") as HTMLSelectElement).value).toBe("remove");

    fireEvent.click(drawer.getByRole("button", { name: "Save rule" }));
    await waitFor(() => expect(authedFetchMock).toHaveBeenCalled());
    expect(savedBody().privateAction).toBe("remove");
  });

  it("is not offered for Usenet only, or for a dangerous file", async () => {
    renderPage([rule({ privateAction: "remove" })]);

    fireEvent.click(screen.getByText("No progress"));
    const drawer = within(await screen.findByRole("dialog"));
    expect(drawer.getByLabelText("If it is from a private tracker")).toBeInTheDocument();

    fireEvent.change(drawer.getByLabelText("Applies to"), { target: { value: "usenet" } });
    expect(drawer.queryByLabelText("If it is from a private tracker")).not.toBeInTheDocument();
    fireEvent.change(drawer.getByLabelText("Applies to"), { target: { value: "torrents" } });
    expect(drawer.getByLabelText("If it is from a private tracker")).toBeInTheDocument();

    fireEvent.change(drawer.getByLabelText("Kind"), { target: { value: "dangerous-file" } });
    expect(drawer.queryByLabelText("If it is from a private tracker")).not.toBeInTheDocument();
  });

  it("shows on the list which rules keep private torrents seeding", () => {
    renderPage([
      rule({ id: "r1", kind: "no-progress", privateAction: "move-and-replace" }),
      rule({ id: "r2", kind: "too-slow", threshold: 100, privateAction: "remove" })
    ]);

    expect(screen.getAllByText("Private torrents keep seeding instead")).toHaveLength(1);
  });

  it("reads a rule saved before the setting existed as removing", async () => {
    const older = rule({}) as Partial<DownloadHealthRule>;
    delete older.privateAction;
    delete older.blocklistWhenMoved;
    authedFetchMock.mockImplementation(() => Promise.resolve({ ok: true, json: () => Promise.resolve([]) } as Response));
    renderPage([older as DownloadHealthRule]);

    fireEvent.click(screen.getByText("No progress"));
    const drawer = within(await screen.findByRole("dialog"));

    expect((drawer.getByLabelText("If it is from a private tracker") as HTMLSelectElement).value).toBe("remove");
  });
});
