import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { MovieCollectionItem, MovieCollectionMemberItem } from "../lib/api";

/**
 * What a person does on the Collections screen (coverage register, area library): follow a collection, switch Monitor and Add
 * missing, Refresh, select several and act on them. The replies are the shapes the server really sends: a 400 problem document with
 * the sentence under errors, a 404 with a message, a 200 with the collection.
 */
const mocks = vi.hoisted(() => ({
  authedFetch: vi.fn(),
  toast: { success: vi.fn(), error: vi.fn(), info: vi.fn(), warning: vi.fn() }
}));
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => mocks.authedFetch(...args) }));
vi.mock("../components/app/tag-picker", () => ({ TagPicker: ({ label }: { label: string }) => <span>{label}</span> }));
vi.mock("../components/shell/toaster", () => ({ toast: mocks.toast }));

const { CollectionsPage } = await import("./collections-page");

const collection = (id: string, name: string, over: Partial<MovieCollectionItem> = {}): MovieCollectionItem => ({
  id, provider: "tmdb", providerId: id === "c1" ? "2344" : "726871", name, overview: null, posterUrl: null, backdropUrl: null, libraryId: "l1", libraryName: "Films",
  rootPath: "/films", monitored: true, monitorMovies: true, qualityProfileId: null, qualityProfileName: null, minimumAvailability: "released", searchOnAdd: false,
  memberCount: 4, heldCount: 1, missingCount: 3, lastSyncedUtc: null, nextSyncUtc: null, lastSyncError: null, createdUtc: "2026-10-03T00:00:00Z",
  updatedUtc: "2026-10-03T00:00:00Z", tags: "", ...over
}) as MovieCollectionItem;
const members: Record<string, MovieCollectionMemberItem[]> = { c1: [], c2: [] };

const problem = (errors: Record<string, string[]>) => JSON.stringify({ type: "https://tools.ietf.org/html/rfc9110#section-15.5.1", title: "One or more validation errors occurred.", status: 400, errors });
const reply = (ok: boolean, text = "") => Promise.resolve({ ok, status: ok ? 200 : 400, text: async () => text, json: async () => (text ? JSON.parse(text) : {}) } as Response);

let items: MovieCollectionItem[];
let libraries: Array<{ id: string; name: string; mediaType: string; rootPath: string }>;

function renderPage() {
  const router = createMemoryRouter(
    [{ path: "/", element: <CollectionsPage />, loader: () => ({ collections: items, libraries, qualityProfiles: [], membersByCollection: members }) }],
    { initialEntries: ["/"] }
  );
  return render(<RouterProvider router={router} />);
}
const calls = () => mocks.authedFetch.mock.calls.map(([url, init]) => ({ url: String(url), method: (init as RequestInit | undefined)?.method ?? "GET", body: (init as RequestInit | undefined)?.body ? JSON.parse(String((init as RequestInit).body)) : undefined }));

beforeEach(() => {
  mocks.authedFetch.mockReset();
  mocks.authedFetch.mockImplementation(() => reply(true));
  for (const fn of Object.values(mocks.toast)) fn.mockClear();
  items = [collection("c1", "The Matrix Collection"), collection("c2", "Dune Collection", { monitored: false, lastSyncError: "The metadata provider did not return this collection; Deluno will try again later." })];
  libraries = [{ id: "l1", name: "Films", mediaType: "movies", rootPath: "/films" }, { id: "l2", name: "Shows", mediaType: "tv", rootPath: "/shows" }];
});

async function openForm() {
  renderPage();
  fireEvent.click(await screen.findByRole("button", { name: "Follow collection" }, { timeout: 30_000 }));
  return await screen.findByRole("heading", { name: "Follow a TMDb collection" });
}
const form = () => screen.getByRole("heading", { name: "Follow a TMDb collection" }).closest("form") as HTMLFormElement;

describe("the Follow a collection form", () => {
  it("takes the id, a movie library of films only, tags and the three switches, and posts them with the id trimmed; success says Collection added and closes the form", async () => {
    await openForm();
    const box = within(form());
    expect(box.getByRole("button", { name: "Add collection" })).toBeEnabled();
    expect(box.getByText("Tags for every movie the collection adds")).toBeInTheDocument();
    for (const name of ["Monitor collection", "Monitor movies", "Search on add"]) expect(box.getByRole("switch", { name: new RegExp(name) })).toBeChecked();

    fireEvent.change(box.getByPlaceholderText("645"), { target: { value: "  2344 " } });
    fireEvent.click(box.getByRole("switch", { name: /Search on add/ }));
    fireEvent.submit(form());

    await waitFor(() => expect(calls().filter((call) => call.method === "POST")).toHaveLength(1));
    expect(calls()[0]).toMatchObject({ url: "/api/movie-collections", method: "POST", body: { providerId: "2344", libraryId: "l1", monitored: true, monitorMovies: true, searchOnAdd: false, tags: "" } });
    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith("Collection added"));
    await waitFor(() => expect(screen.queryByRole("heading", { name: "Follow a TMDb collection" })).toBeNull());
  });

  it("does nothing without an id, and the Follow collection button closes the form again", async () => {
    await openForm();

    fireEvent.submit(form());
    expect(calls()).toEqual([]);
    fireEvent.click(screen.getByRole("button", { name: "Follow collection" }));
    await waitFor(() => expect(screen.queryByRole("heading", { name: "Follow a TMDb collection" })).toBeNull());
  });

  it("Cancel closes the form without sending anything", async () => {
    await openForm();
    fireEvent.change(within(form()).getByPlaceholderText("645"), { target: { value: "2344" } });

    fireEvent.click(within(form()).getByRole("button", { name: "Cancel" }));

    await waitFor(() => expect(screen.queryByRole("heading", { name: "Follow a TMDb collection" })).toBeNull());
    expect(calls()).toEqual([]);
  });

  it("with no movie library to follow into, Add collection is disabled", async () => {
    libraries = [{ id: "l2", name: "Shows", mediaType: "tv", rootPath: "/shows" }];
    await openForm();

    expect(within(form()).getByRole("button", { name: "Add collection" })).toBeDisabled();
  });

  it("is disabled while the add is in flight, so it cannot be sent twice", async () => {
    let finish!: () => void;
    mocks.authedFetch.mockImplementation(() => new Promise((resolve) => { finish = () => resolve({ ok: true, text: async () => "" }); }));
    await openForm();
    fireEvent.change(within(form()).getByPlaceholderText("645"), { target: { value: "2344" } });

    fireEvent.submit(form());
    await waitFor(() => expect(within(form()).getByRole("button", { name: "Add collection" })).toBeDisabled());
    fireEvent.submit(form());
    expect(calls().filter((call) => call.method === "POST")).toHaveLength(1);
    finish();
  });

  // DEFECT (wording): the screen toasts the raw body of a refusal. A 400 from the server is a problem document, so the person
  // reads JSON instead of "Enter a numeric TMDb collection id.", "Choose a movie library for the collection." and, for a 404,
  // {"message":"TMDb did not return that collection."}. The sentences exist on the server (MovieCollectionsOnTheRecordedGatewayTests).
  it("a refused follow toasts the server's sentence, not the raw reply", async () => {
    mocks.authedFetch.mockImplementation(() => reply(false, problem({ providerId: ["Enter a numeric TMDb collection id."] })));
    await openForm();
    fireEvent.change(within(form()).getByPlaceholderText("645"), { target: { value: "matrix" } });

    fireEvent.submit(form());

    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalled());
    expect(mocks.toast.error).toHaveBeenCalledWith("Enter a numeric TMDb collection id.");
  });

  it("a 404 toasts the server's sentence, not the {\"message\"} document", async () => {
    mocks.authedFetch.mockImplementation(() => reply(false, JSON.stringify({ message: "TMDb did not return that collection." })));
    await openForm();
    fireEvent.change(within(form()).getByPlaceholderText("645"), { target: { value: "999999999" } });
    fireEvent.submit(form());
    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalledWith("TMDb did not return that collection."));
  });

  it("a refusal keeps the form open with what was typed, and the toast carries the reply it was given", async () => {
    mocks.authedFetch.mockImplementation(() => reply(false, "TMDb did not return that collection."));
    await openForm();
    fireEvent.change(within(form()).getByPlaceholderText("645"), { target: { value: "999999999" } });

    fireEvent.submit(form());

    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalledWith("TMDb did not return that collection."));
    expect((within(form()).getByPlaceholderText("645") as HTMLInputElement).value).toBe("999999999");
  });
});

describe("a followed collection", () => {
  it("Monitor and Add missing send PUT /api/movie-collections/{id} with the one switch changed, and every control on that card is disabled while it saves", async () => {
    let finish!: () => void;
    mocks.authedFetch.mockImplementation(() => new Promise((resolve) => { finish = () => resolve({ ok: true, text: async () => "" }); }));
    renderPage();
    const card = (await screen.findByRole("heading", { name: "The Matrix Collection" }, { timeout: 30_000 })).closest("div.flex-col") as HTMLElement;

    fireEvent.click(within(card).getByRole("switch", { name: "Monitor" }));

    await waitFor(() => expect(calls()).toEqual([{ url: "/api/movie-collections/c1", method: "PUT", body: { monitored: false } }]));
    expect(within(card).getByRole("switch", { name: "Monitor" })).toBeDisabled();
    expect(within(card).getByRole("switch", { name: "Add missing" })).toBeDisabled();
    expect(within(card).getByRole("button", { name: /Refresh/ })).toBeDisabled();
    const other = screen.getByRole("heading", { name: "Dune Collection" }).closest("div.flex-col") as HTMLElement;
    expect(within(other).getByRole("switch", { name: "Monitor" })).toBeEnabled();
    finish();
    await waitFor(() => expect(within(card).getByRole("switch", { name: "Add missing" })).toBeEnabled());
    fireEvent.click(within(card).getByRole("switch", { name: "Add missing" }));
    await waitFor(() => expect(calls().at(-1)).toEqual({ url: "/api/movie-collections/c1", method: "PUT", body: { monitorMovies: false } }));
  });

  it("a refused switch says so", async () => {
    mocks.authedFetch.mockImplementation(() => reply(false));
    renderPage();
    const card = (await screen.findByRole("heading", { name: "The Matrix Collection" }, { timeout: 30_000 })).closest("div.flex-col") as HTMLElement;

    fireEvent.click(within(card).getByRole("switch", { name: "Add missing" }));

    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalledWith("Collection settings could not be saved."));
  });

  it("Refresh posts to /refresh, shows the spinner and disables the card while it runs, and says the collection was refreshed", async () => {
    let finish!: () => void;
    mocks.authedFetch.mockImplementation(() => new Promise((resolve) => { finish = () => resolve({ ok: true, text: async () => "" }); }));
    renderPage();
    const card = (await screen.findByRole("heading", { name: "The Matrix Collection" }, { timeout: 30_000 })).closest("div.flex-col") as HTMLElement;

    fireEvent.click(within(card).getByRole("button", { name: /Refresh/ }));

    await waitFor(() => expect(calls()).toEqual([{ url: "/api/movie-collections/c1/refresh", method: "POST", body: undefined }]));
    expect(within(card).getByRole("button", { name: /Refresh/ })).toBeDisabled();
    expect(card.querySelector("svg.animate-spin")).not.toBeNull();
    finish();
    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith("The Matrix Collection refreshed"));
  });

  it("a collection whose last refresh failed shows Last refresh: and the reason, and one that did not shows nothing", async () => {
    renderPage();

    expect(await screen.findByText("Last refresh: The metadata provider did not return this collection; Deluno will try again later.", {}, { timeout: 30_000 })).toBeInTheDocument();
    expect(screen.getAllByText(/^Last refresh:/)).toHaveLength(1);
  });
});

describe("selecting collections", () => {
  it("a tick opens a bar saying N selected with Monitor and Stop monitoring, and each sends the change for every selected collection", async () => {
    renderPage();
    await screen.findByRole("heading", { name: "The Matrix Collection" }, { timeout: 30_000 });
    expect(screen.queryByText(/selected$/)).toBeNull();

    fireEvent.click(screen.getByRole("checkbox", { name: "Select The Matrix Collection" }));
    fireEvent.click(screen.getByRole("checkbox", { name: "Select Dune Collection" }));
    expect(screen.getByText("2 selected")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Stop monitoring" }));

    await waitFor(() => expect(calls().filter((call) => call.method === "PUT")).toHaveLength(2));
    expect(calls().map((call) => [call.url, call.body])).toEqual([["/api/movie-collections/c1", { monitored: false }], ["/api/movie-collections/c2", { monitored: false }]]);
    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith("2 collections updated"));
    await waitFor(() => expect(screen.queryByText("2 selected")).toBeNull());
    fireEvent.click(screen.getByRole("checkbox", { name: "Select The Matrix Collection" }));
    fireEvent.click(screen.getByRole("checkbox", { name: "Select Dune Collection" }));
    fireEvent.click(screen.getByRole("button", { name: "Monitor" }));
    await waitFor(() => expect(calls().filter((call) => call.method === "PUT")).toHaveLength(4));
    expect(calls().at(-1)!.body).toEqual({ monitored: true });
  });

  it("unticking the last collection takes the bar away", async () => {
    renderPage();
    await screen.findByRole("heading", { name: "The Matrix Collection" }, { timeout: 30_000 });
    const tick = screen.getByRole("checkbox", { name: "Select The Matrix Collection" });

    fireEvent.click(tick);
    expect(screen.getByText("1 selected")).toBeInTheDocument();
    fireEvent.click(tick);

    expect(screen.queryByText("1 selected")).toBeNull();
  });
});
