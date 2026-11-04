import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// #1329: the Tags page's loader, empty state, sorting, colours, saving an edit, discarding, and the failures of delete.

const authedFetch = vi.hoisted(() => vi.fn());
const fetchJson = vi.hoisted(() => vi.fn(async (_url: string): Promise<unknown> => []));
const overview = vi.hoisted(() => vi.fn(async () => ({ libraries: [{ id: "l1" }], qualityProfiles: [], settings: {} })));
vi.mock("../lib/use-auth", () => ({ authedFetch }));
vi.mock("../lib/api", async (importOriginal) => ({ ...(await importOriginal<object>()), fetchJson }));
vi.mock("./settings-overview-page", () => ({ settingsOverviewLoader: overview }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));

const tag = (id: string, name: string, color = "slate", description = "") => ({ id, name, color, description, createdUtc: "2026-09-29T00:00:00Z", updatedUtc: "2026-09-29T00:00:00Z" });
const state = vi.hoisted(() => ({ data: { libraries: [], qualityProfiles: [], settings: {}, tags: [] as unknown[], usage: [] as unknown[] } }));
const revalidate = vi.hoisted(() => vi.fn());
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => state.data,
  useRevalidator: () => ({ revalidate, state: "idle" })
}));
const toast = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn() }));
vi.mock("../components/shell/toaster", () => ({ toast }));

const { SettingsTagsPage, settingsTagsLoader } = await import("./settings-tags-page");

const reply = (ok: boolean, status = ok ? 200 : 500, body: unknown = {}) => ({ ok, status, url: "/api/tags", json: async () => body, text: async () => (ok ? "" : JSON.stringify(body)) });
const show = () => render(<MemoryRouter><SettingsTagsPage /></MemoryRouter>);
const unused = (id: string, name: string) => ({ id, name, movieCount: 0, seriesCount: 0, totalCount: 0, isUnused: true, users: [] });
const used = (id: string, name: string) => ({ id, name, movieCount: 1, seriesCount: 0, totalCount: 1, isUnused: false, users: [] });

beforeEach(() => {
  authedFetch.mockReset();
  authedFetch.mockResolvedValue(reply(true, 204));
  toast.success.mockReset();
  toast.error.mockReset();
  revalidate.mockReset();
  state.data = { libraries: [], qualityProfiles: [], settings: {}, tags: [tag("t1", "Kids", "emerald", "For the lounge"), tag("t2", "Adults", "teal"), tag("t3", "Anime", "blue"), tag("t4", "Docs", "violet"), tag("t5", "Eng", "amber"), tag("t6", "Fun", "rose"), tag("t7", "Plain")], usage: [used("t1", "Kids"), unused("t2", "Adults")] };
});
afterEach(() => { cleanup(); window.localStorage.clear(); });

describe("the Tags page's loader", () => {
  it("reads the overview, the tags and their usage, and still loads when usage cannot be read", async () => {
    fetchJson.mockImplementation(async (url: string) => (url === "/api/tags" ? [tag("t1", "Kids")] : [used("t1", "Kids")]));
    expect(await settingsTagsLoader()).toMatchObject({ libraries: [{ id: "l1" }], tags: [tag("t1", "Kids")], usage: [used("t1", "Kids")] });
    fetchJson.mockImplementation(async (url: string) => { if (url === "/api/tags/usage") throw new Error("down"); return [tag("t1", "Kids")]; });
    expect((await settingsTagsLoader()).usage).toEqual([]);
  });
});

describe("the Tags list", () => {
  it("with no tags says so and offers New tag, which opens a blank drawer", async () => {
    state.data = { ...state.data, tags: [], usage: [] };
    show();
    expect(screen.getByText("0 tags")).toBeTruthy();
    fireEvent.click(within(screen.getByText("No tags yet").parentElement!.parentElement!).getByRole("button", { name: "New tag" }));
    expect(await screen.findByRole("dialog")).toBeTruthy();
    expect(screen.queryByRole("button", { name: /^Delete unused tags/ })).toBeNull();
  });

  it("sorts by Name, Colour, Used by and Description, each reversed by a second press, and shows a dash for no description", () => {
    show();
    const names = () => screen.getAllByRole("row").map((row) => /Kids|Adults|Anime|Docs|Eng|Fun|Plain/.exec(row.textContent ?? "")?.[0]).filter(Boolean);
    const press = (label: string) => fireEvent.click(within(screen.getByRole("columnheader", { name: new RegExp(`^${label}`) })).getByRole("button"));
    press("Name");
    expect(names()).toEqual(["Adults", "Anime", "Docs", "Eng", "Fun", "Kids", "Plain"]);
    press("Name");
    expect(names()).toEqual(["Plain", "Kids", "Fun", "Eng", "Docs", "Anime", "Adults"]);
    press("Colour");
    expect(names()[0]).toBe("Eng");
    press("Colour");
    expect(names()[0]).toBe("Docs");
    press("Used by");
    expect(names()).toHaveLength(7);
    press("Description");
    expect(names()[6]).toBe("Kids");
    press("Description");
    expect(names()[0]).toBe("Kids");
    expect(screen.getAllByText("—").length).toBeGreaterThan(0);
  });

  it("each colour has its own dot", () => {
    const { container } = show();
    const dots = Array.from(container.querySelectorAll("span.rounded-full.h-2\\.5")).map((dot) => dot.className.split(" ").find((name) => name.startsWith("bg-")));
    expect(dots.sort()).toEqual(["bg-amber-500", "bg-emerald-500", "bg-rose-500", "bg-sky-500", "bg-slate-400", "bg-teal-500", "bg-violet-500"]);
  });
});

describe("editing a tag", () => {
  it("changes its note and colour, saves it in place, and says it was saved", async () => {
    show();
    fireEvent.click(screen.getByText("Adults"));
    const drawer = within(await screen.findByRole("dialog"));
    fireEvent.change(drawer.getByPlaceholderText("For example: titles sent to the lounge TV."), { target: { value: "  grown-ups  " } });
    fireEvent.change(drawer.getByRole("combobox"), { target: { value: "rose" } });
    fireEvent.click(drawer.getByRole("button", { name: "Save tag" }));
    expect(await drawer.findByText("Saved just now")).toBeTruthy();
    const call = authedFetch.mock.calls.find(([url]) => url === "/api/tags/t2")!;
    expect((call[1] as RequestInit).method).toBe("PUT");
    expect(JSON.parse(String((call[1] as RequestInit).body))).toEqual({ name: "Adults", color: "rose", description: "grown-ups" });
    expect(revalidate).toHaveBeenCalled();
  });

  it("a save that is refused without a message, and one that throws something that is not a message, still say it could not save", async () => {
    show();
    fireEvent.click(screen.getByText("Adults"));
    const drawer = within(await screen.findByRole("dialog"));
    fireEvent.change(drawer.getByPlaceholderText("For example: titles sent to the lounge TV."), { target: { value: "x" } });
    authedFetch.mockResolvedValue(reply(false, 500, {}));
    fireEvent.click(drawer.getByRole("button", { name: "Save tag" }));
    expect(await drawer.findByText("Tag could not be saved.")).toBeTruthy();
    authedFetch.mockRejectedValue("offline");
    fireEvent.click(drawer.getByRole("button", { name: "Save tag" }));
    expect(await drawer.findByText("Could not save")).toBeTruthy();
  });

  it("a second press on Save while the first is working sends nothing more", async () => {
    show();
    fireEvent.click(screen.getByText("Adults"));
    const drawer = within(await screen.findByRole("dialog"));
    fireEvent.change(drawer.getByPlaceholderText("For example: titles sent to the lounge TV."), { target: { value: "x" } });
    let release!: () => void;
    authedFetch.mockImplementation(() => new Promise((resolve) => { release = () => resolve(reply(true, 200)); }));
    const form = drawer.getByRole("button", { name: "Save tag" }).closest("form")!;
    fireEvent.submit(form);
    await waitFor(() => expect(authedFetch).toHaveBeenCalledTimes(1));
    fireEvent.submit(form);
    expect(authedFetch).toHaveBeenCalledTimes(1);
    release();
    expect(await drawer.findByText("Saved just now")).toBeTruthy();
  });
});

describe("closing the tag drawer", () => {
  it("closes at once when nothing was changed, and asks first when something was, until Discard", async () => {
    show();
    fireEvent.click(screen.getByText("Adults"));
    let drawer = within(await screen.findByRole("dialog"));
    fireEvent.click(drawer.getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());

    fireEvent.click(screen.getByText("Adults"));
    drawer = within(await screen.findByRole("dialog"));
    fireEvent.change(drawer.getByPlaceholderText("For example: titles sent to the lounge TV."), { target: { value: "x" } });
    fireEvent.keyDown(drawer.getByRole("button", { name: "Cancel" }), { key: "Escape" });
    expect(await screen.findByText("Discard unsaved changes?")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Discard" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });
});

describe("a tag the server sent without a note", () => {
  it("opens with an empty note, sorts as having none, and a new edit after a save clears the saved mark", async () => {
    state.data = { ...state.data, tags: [{ ...tag("t1", "Kids"), description: null }, tag("t2", "Adults", "teal", "grown-ups")], usage: [] };
    show();
    fireEvent.click(within(screen.getByRole("columnheader", { name: /^Description/ })).getByRole("button"));
    expect(screen.getAllByRole("row").map((row) => /Kids|Adults/.exec(row.textContent ?? "")?.[0]).filter(Boolean)).toEqual(["Kids", "Adults"]);
    fireEvent.click(screen.getByText("Kids"));
    const drawer = within(await screen.findByRole("dialog"));
    const note = drawer.getByPlaceholderText("For example: titles sent to the lounge TV.") as HTMLTextAreaElement;
    expect(note.value).toBe("");
    fireEvent.change(note, { target: { value: "x" } });
    fireEvent.click(drawer.getByRole("button", { name: "Save tag" }));
    expect(await drawer.findByText("Saved just now")).toBeTruthy();
    fireEvent.change(note, { target: { value: "xy" } });
    await waitFor(() => expect(drawer.queryByText("Saved just now")).toBeNull());
  });

  it("Escape on the discard question keeps the drawer and asks nothing more", async () => {
    show();
    fireEvent.click(screen.getByText("Adults"));
    const drawer = within(await screen.findByRole("dialog"));
    fireEvent.change(drawer.getByPlaceholderText("For example: titles sent to the lounge TV."), { target: { value: "x" } });
    fireEvent.click(drawer.getByRole("button", { name: "Cancel" }));
    const question = await screen.findByText("Discard unsaved changes?");
    fireEvent.keyDown(question, { key: "Escape" });
    await waitFor(() => expect(screen.queryByText("Discard unsaved changes?")).toBeNull());
    expect(screen.getByRole("dialog")).toBeTruthy();
  });
});

describe("deleting tags", () => {
  it("a tag that cannot be deleted says why, and one that can is deleted and named", async () => {
    show();
    fireEvent.click(screen.getByText("Adults"));
    const drawer = within(await screen.findByRole("dialog"));
    authedFetch.mockResolvedValue(reply(false, 409, { title: "A rule still uses it." }));
    fireEvent.click(drawer.getByRole("button", { name: "Delete" }));
    fireEvent.click(await screen.findByRole("button", { name: "Delete tag" }));
    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("A rule still uses it."));
    authedFetch.mockRejectedValue("offline");
    fireEvent.click(await screen.findByRole("button", { name: "Delete tag" }));
    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("Tag could not be removed."));
    authedFetch.mockResolvedValue(reply(true, 204));
    fireEvent.click(await screen.findByRole("button", { name: "Delete tag" }));
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("Adults removed"));
  });

  it("deleting the unused tags that cannot be deleted says why", async () => {
    show();
    fireEvent.click(screen.getByRole("button", { name: /^Delete unused tags/ }));
    authedFetch.mockResolvedValue(reply(false, 500, { title: "The tags are busy." }));
    fireEvent.click(await screen.findByRole("button", { name: "Delete unused tags" }));
    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("The tags are busy."));
    authedFetch.mockRejectedValue("offline");
    fireEvent.click(await screen.findByRole("button", { name: "Delete unused tags" }));
    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("Unused tags could not be deleted."));
  });
});
