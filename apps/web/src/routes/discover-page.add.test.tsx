import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { DiscoverResponse, LibraryItem, QualityProfileItem } from "../lib/api";

/**
 * Adding from Discover (coverage register, area library): the TV list is asked for as TV and its cards are marked, the add drawer
 * offers only the shows' libraries and profiles and posts the series branch, and the outcome is told in words: the sentence the
 * server gave on a refusal, and "{title} added - searching now" with the drawer closed and the card turned to In library.
 */
const mocks = vi.hoisted(() => ({
  authedFetch: vi.fn(),
  toast: Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn(), info: vi.fn(), warning: vi.fn(), loading: vi.fn(), dismiss: vi.fn() })
}));
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => mocks.authedFetch(...args) }));
vi.mock("../components/shell/toaster", () => ({ toast: mocks.toast, realtimeToast: {}, Toaster: () => null }));

const loaderData = {
  libraries: [
    { id: "lib-films", name: "Films", mediaType: "movies" },
    { id: "lib-shows", name: "Shows", mediaType: "tv" }
  ] as LibraryItem[],
  qualityProfiles: [
    { id: "profile-films", name: "Films HD", mediaType: "movies" },
    { id: "profile-shows", name: "Shows HD", mediaType: "tv" }
  ] as QualityProfileItem[]
};
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData
}));

const shows: DiscoverResponse = {
  popular: [
    { provider: "tmdb", providerId: "108978", title: "Reacher", year: 2022, overview: "A drifter.", posterUrl: null, backdropUrl: null, rating: 8.1, imdbId: null, externalUrl: null, inLibrary: false, libraryEntryId: null, excluded: false },
    { provider: "tmdb", providerId: "456", title: "The Simpsons", year: 1989, overview: "Yellow.", posterUrl: null, backdropUrl: null, rating: 7.9, imdbId: null, externalUrl: null, inLibrary: true, libraryEntryId: "show-1", excluded: false },
    { provider: "tmdb", providerId: "4614", title: "NCIS", year: 2003, overview: "Navy.", posterUrl: null, backdropUrl: null, rating: 7.4, imdbId: null, externalUrl: null, inLibrary: false, libraryEntryId: null, excluded: true }
  ],
  lists: []
};

const reply = (ok: boolean, body: unknown, status = ok ? 200 : 400) =>
  Promise.resolve({ ok, status, json: () => Promise.resolve(body), text: () => Promise.resolve(typeof body === "string" ? body : JSON.stringify(body)) } as Response);

let addReply: () => Promise<Response>;
const asked: string[] = [];

beforeEach(() => {
  window.localStorage.clear();
  window.localStorage.setItem("deluno-discover-type", "series");
  asked.length = 0;
  for (const fn of [mocks.toast.success, mocks.toast.error]) fn.mockClear();
  addReply = () => reply(true, { id: "show-9", mediaType: "series", libraryId: "lib-shows" }, 201);
  mocks.authedFetch.mockReset();
  mocks.authedFetch.mockImplementation((path: string) => {
    asked.push(path);
    if (path === "/api/discover/add") return addReply();
    if (path.startsWith("/api/discover")) return reply(true, shows);
    return reply(true, []);
  });
});

async function renderTv() {
  const { DiscoverPage } = await import("./discover-page");
  render(<MemoryRouter><DiscoverPage /></MemoryRouter>);
  await waitFor(() => expect(screen.getByText("Reacher")).toBeInTheDocument());
}
const addCall = () => {
  const call = mocks.authedFetch.mock.calls.find(([path]) => path === "/api/discover/add") as [string, RequestInit] | undefined;
  return call ? JSON.parse(String(call[1].body)) : undefined;
};

describe("The TV popular list", () => {
  it("is asked for as TV, and marks the show the library holds and the show that was excluded", async () => {
    await renderTv();

    expect(asked).toContain("/api/discover?mediaType=series");
    expect(screen.getByText("In library")).toBeInTheDocument();
    expect(screen.getByText("Excluded")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Add The Simpsons" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Add Reacher" })).toBeInTheDocument();
  });
});

describe("Add a TV show from Discover", () => {
  it("offers only the shows' library and profile, and posts the series branch with the provider record", async () => {
    await renderTv();

    fireEvent.click(screen.getByRole("button", { name: "Add Reacher" }));
    await waitFor(() => expect(screen.getByRole("dialog", { name: /Add Reacher/ })).toBeInTheDocument());
    const dialog = screen.getByRole("dialog", { name: /Add Reacher/ });
    expect(dialog).toHaveTextContent("Shows");
    expect(dialog).not.toHaveTextContent("Films HD");
    fireEvent.click(screen.getByRole("button", { name: "Add" }));

    await waitFor(() => expect(addCall()).toBeDefined());
    expect(addCall()).toMatchObject({
      mediaType: "series", title: "Reacher", year: 2022, provider: "tmdb", providerId: "108978",
      libraryId: "lib-shows", monitored: true, searchForMissingNow: true, searchForCutoffUnmetNow: true
    });
  });

  it("a successful add says the title was added and searching, closes the drawer and turns the card to In library", async () => {
    await renderTv();
    fireEvent.click(screen.getByRole("button", { name: "Add Reacher" }));
    await waitFor(() => expect(screen.getByRole("dialog", { name: /Add Reacher/ })).toBeInTheDocument());

    fireEvent.click(screen.getByRole("button", { name: "Add" }));

    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith("Reacher added — searching now."));
    await waitFor(() => expect(screen.queryByRole("dialog", { name: /Add Reacher/ })).not.toBeInTheDocument());
    expect(screen.queryByRole("button", { name: "Add Reacher" })).not.toBeInTheDocument();
    expect(screen.getAllByText("In library")).toHaveLength(2);
  });

  it("with the search switch off the toast does not promise a search", async () => {
    await renderTv();
    fireEvent.click(screen.getByRole("button", { name: "Add Reacher" }));
    await waitFor(() => expect(screen.getByRole("dialog", { name: /Add Reacher/ })).toBeInTheDocument());

    fireEvent.click(screen.getByRole("switch", { name: /Search for it as soon as it is added/ }));
    fireEvent.click(screen.getByRole("button", { name: "Add" }));

    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith("Reacher added."));
  });

  it("a refused add toasts the server's own sentence, keeps the drawer open and leaves the card unmarked", async () => {
    addReply = () => reply(false, "Choose a quality profile made for this kind of title.", 400);
    await renderTv();
    fireEvent.click(screen.getByRole("button", { name: "Add Reacher" }));
    await waitFor(() => expect(screen.getByRole("dialog", { name: /Add Reacher/ })).toBeInTheDocument());

    fireEvent.click(screen.getByRole("button", { name: "Add" }));

    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalledWith("Choose a quality profile made for this kind of title."));
    expect(mocks.toast.success).not.toHaveBeenCalled();
    expect(screen.getByRole("dialog", { name: /Add Reacher/ })).toBeInTheDocument();
    expect(screen.getAllByText("In library")).toHaveLength(1);
  });
});
