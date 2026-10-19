import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { IndexerItem, ReleaseProfileItem } from "../lib/api";
import { emptyPlatformSettingsSnapshot } from "../lib/api/types/settings";

/**
 * The acquisition rule drawer: saving, deleting, validation and the Proper and Repack choice.
 * Every call goes through the page's real fetch wrapper, which this file replaces with a recorder.
 */

vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
const toastMock = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn() }));
vi.mock("../components/shell/toaster", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../components/shell/toaster")>()),
  toast: toastMock
}));

const rule: ReleaseProfileItem = {
  id: "rule-1", name: "Kids rule", tagName: "Kids", tagNames: ["Kids"], preferredProtocol: "any",
  usenetDelayMinutes: 0, torrentDelayMinutes: 0, mustContain: "", mustNotContain: "", preferredTerms: [],
  createdUtc: new Date(0).toISOString(), updatedUtc: new Date(0).toISOString(),
  enabled: true, indexerIds: [], bypassIfHighestQuality: false, position: 0
};
const loaderData = { profiles: [rule], indexers: [] as IndexerItem[], settings: { ...emptyPlatformSettingsSnapshot }, ruleMode: "combine" };
const authedFetchMock = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));
const liveSpec = vi.hoisted(() => ({ current: undefined as { events?: readonly string[]; areas?: readonly string[] } | undefined }));
vi.mock("../lib/live-refresh", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/live-refresh")>()),
  useLiveRefresh: (spec: { events?: readonly string[]; areas?: readonly string[] }) => { liveSpec.current = spec; }
}));
const revalidate = vi.fn();
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate })
}));

const { SettingsReleaseRulesPage } = await import("./settings-release-rules-page");

function renderPage() {
  return render(<MemoryRouter><SettingsReleaseRulesPage /></MemoryRouter>);
}
/** Answers the tag list the picker reads, and gives every other call the reply chosen by the test. */
function answer(make: () => Response) {
  authedFetchMock.mockImplementation(async (path: string) => (path.startsWith("/api/tags") ? json([]) : make()));
}
function writes() {
  return authedFetchMock.mock.calls.filter(([path]) => !String(path).startsWith("/api/tags"));
}
function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("acquisition rule drawer", () => {
  beforeEach(() => { loaderData.settings = { ...emptyPlatformSettingsSnapshot }; });
  afterEach(() => { vi.clearAllMocks(); authedFetchMock.mockReset(); });

  it("creates a new rule with POST, then keeps the drawer on the saved rule and says Saved just now", async () => {
    answer(() => json({ ...rule, id: "rule-9", name: "Fresh rule", tagNames: [], tagName: "" }));
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "New acquisition rule" }));
    await screen.findByRole("dialog");
    fireEvent.change(screen.getByPlaceholderText("Global release timing"), { target: { value: "Fresh rule" } });
    fireEvent.click(screen.getByRole("button", { name: "Create rule" }));

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/release-profiles", expect.objectContaining({ method: "POST" })));
    const body = JSON.parse(writes()[0][1].body as string);
    expect(body.name).toBe("Fresh rule");
    expect(await screen.findByText("Saved just now")).toBeTruthy();
    expect(screen.getByRole("dialog")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Save rule" })).toBeTruthy();
    expect(revalidate).toHaveBeenCalled();
  });

  it("edits an existing rule with PUT to that rule's address and the drawer stays open on it", async () => {
    answer(() => json({ ...rule, name: "Kids rule 2" }));
    renderPage();
    fireEvent.click(screen.getByText("Kids rule"));
    await screen.findByRole("dialog");
    fireEvent.change(screen.getByPlaceholderText("Global release timing"), { target: { value: "Kids rule 2" } });
    fireEvent.click(screen.getByRole("button", { name: "Save rule" }));

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/release-profiles/rule-1", expect.objectContaining({ method: "PUT" })));
    expect(await screen.findByText("Saved just now")).toBeTruthy();
    expect(screen.getByRole("dialog")).toBeTruthy();
  });

  it("shows the server's own message in the drawer when the save is refused", async () => {
    answer(() => json({ message: "That tag is already used by another rule." }, 400));
    renderPage();
    fireEvent.click(screen.getByText("Kids rule"));
    await screen.findByRole("dialog");
    fireEvent.change(screen.getByPlaceholderText("Global release timing"), { target: { value: "Kids rule 2" } });
    fireEvent.click(screen.getByRole("button", { name: "Save rule" }));

    expect(await screen.findByText("That tag is already used by another rule.")).toBeTruthy();
    expect(screen.queryByText("Saved just now")).toBeNull();
  });

  it("deletes through the danger zone and a confirmation, with DELETE and a 'removed' toast naming the rule", async () => {
    answer(() => new Response(null, { status: 204 }));
    renderPage();
    fireEvent.click(screen.getByText("Kids rule"));
    await screen.findByRole("dialog");
    fireEvent.click(screen.getByRole("button", { name: "Delete" }));
    expect(writes()).toHaveLength(0);
    fireEvent.click(await screen.findByRole("button", { name: "Delete rule" }));

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/release-profiles/rule-1", { method: "DELETE" }));
    await waitFor(() => expect(toastMock.success).toHaveBeenCalledWith("Kids rule removed"));
  });

  it("keeps the rule and toasts the problem when the delete fails", async () => {
    answer(() => json({ message: "Cannot remove" }, 500));
    renderPage();
    fireEvent.click(screen.getByText("Kids rule"));
    await screen.findByRole("dialog");
    fireEvent.click(screen.getByRole("button", { name: "Delete" }));
    fireEvent.click(await screen.findByRole("button", { name: "Delete rule" }));

    await waitFor(() => expect(toastMock.error).toHaveBeenCalled());
    expect(toastMock.success).not.toHaveBeenCalled();
  });

  it("refuses a missing name, delays that are not whole numbers 0 to 525600 and over-long term lists without calling the server", async () => {
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "New acquisition rule" }));
    await screen.findByRole("dialog");
    fireEvent.click(screen.getByRole("button", { name: "Create rule" }));
    expect(await screen.findByText("Give this rule a name.")).toBeTruthy();

    fireEvent.change(screen.getByPlaceholderText("Global release timing"), { target: { value: "X" } });
    const numeric = screen.getAllByRole("spinbutton");
    fireEvent.change(numeric[0], { target: { value: "525601" } });
    fireEvent.change(numeric[1], { target: { value: "1.5" } });
    fireEvent.change(screen.getByPlaceholderText("Proper, Remux, /2160p|4k/i"), { target: { value: "a".repeat(2001) } });
    fireEvent.click(screen.getByRole("button", { name: "Create rule" }));

    expect(await screen.findByText("Usenet delay must be a whole number from 0 to 525600.")).toBeTruthy();
    expect(screen.getByText("Torrent delay must be a whole number from 0 to 525600.")).toBeTruthy();
    expect(screen.getByText("Must-contain terms must be 2,000 characters or fewer.")).toBeTruthy();
    expect(writes()).toHaveLength(0);
  });
});

describe("Proper and Repack choice", () => {
  afterEach(() => { vi.clearAllMocks(); authedFetchMock.mockReset(); });

  it("saves each of the three choices as a settings patch", async () => {
    authedFetchMock.mockImplementation(async (_path: string, init: { body: string }) => json({ ...emptyPlatformSettingsSnapshot, ...JSON.parse(init.body) }));
    renderPage();
    const select = screen.getByRole("combobox", { name: "What a Proper or Repack release does" }) as HTMLSelectElement;
    const values = Array.from(select.options).map((option) => option.value);
    expect(values.length).toBe(3);
    for (const value of values) {
      fireEvent.change(select, { target: { value } });
      await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/settings", expect.objectContaining({ method: "PATCH", body: JSON.stringify({ properRepackAcquisitionMode: value }) })));
    }
    await waitFor(() => expect(select.value).toBe(values[values.length - 1]));
  });

  it("goes back to the saved choice and says it could not save when the server refuses", async () => {
    answer(() => json({ title: "no" }, 500));
    renderPage();
    const select = screen.getByRole("combobox", { name: "What a Proper or Repack release does" }) as HTMLSelectElement;
    const before = select.value;
    const other = Array.from(select.options).find((option) => option.value !== before)!.value;
    fireEvent.change(select, { target: { value: other } });

    expect(await screen.findByText("Could not save. Try again.")).toBeTruthy();
    expect(select.value).toBe(before);
  });
});

describe("Acquisition rules: a change made elsewhere", () => {
  it("is followed with no reload: the screen reads again for IndexerChanged, SettingsChanged", () => {
    renderPage();
    expect(liveSpec.current?.events).toEqual(expect.arrayContaining(["IndexerChanged","SettingsChanged"]));
    
  });
});
