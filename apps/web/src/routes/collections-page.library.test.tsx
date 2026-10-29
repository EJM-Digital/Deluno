import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { LibraryCollectionItem, LibraryCollectionMember, LibraryCollectionsResponse } from "../lib/api";

/**
 * Collections as Radarr shows them (#1425): every collection a film in the library belongs to with each film held, wanted or
 * missing and one action that adds the missing ones, and a Recommended section with the reason for each.
 */
const mocks = vi.hoisted(() => ({
  authedFetch: vi.fn(),
  toast: { success: vi.fn(), error: vi.fn(), info: vi.fn(), warning: vi.fn() }
}));
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => mocks.authedFetch(...args) }));
vi.mock("../components/app/tag-picker", () => ({ TagPicker: ({ label }: { label: string }) => <span>{label}</span> }));
vi.mock("../components/shell/toaster", () => ({ toast: mocks.toast }));

const { CollectionsPage } = await import("./collections-page");

const member = (providerId: string, title: string, year: number, status: LibraryCollectionMember["status"], over: Partial<LibraryCollectionMember> = {}): LibraryCollectionMember => ({
  providerId, title, releaseYear: year, posterUrl: null, status, localMovieId: status === "missing" ? null : `movie-${providerId}`, isExcluded: false, ...over
});

const matrix: LibraryCollectionItem = {
  providerId: "2344", name: "The Matrix Collection", overview: null, posterUrl: null, followedCollectionId: null, following: false,
  heldCount: 1, wantedCount: 1, missingCount: 2, membersKnown: true, why: null,
  members: [
    member("603", "The Matrix", 1999, "held"),
    member("604", "The Matrix Reloaded", 2003, "wanted"),
    member("605", "The Matrix Revolutions", 2003, "missing"),
    member("624860", "The Matrix Resurrections", 2021, "missing")
  ]
};
const ironMan: LibraryCollectionItem = {
  providerId: "131292", name: "Iron Man Collection", overview: null, posterUrl: "https://image.example/iron.jpg", followedCollectionId: "c9", following: true,
  heldCount: 2, wantedCount: 0, missingCount: 1, membersKnown: true, why: null,
  members: [member("1726", "Iron Man", 2008, "held"), member("10138", "Iron Man 2", 2010, "held"), member("68721", "Iron Man 3", 2013, "missing", { isExcluded: true })]
};
const dune: LibraryCollectionItem = {
  providerId: "726871", name: "Dune Collection", overview: null, posterUrl: null, followedCollectionId: null, following: false,
  heldCount: 1, wantedCount: 0, missingCount: 0, membersKnown: false, why: null,
  members: [member("438631", "Dune", 2021, "held")]
};
const complete: LibraryCollectionItem = { ...dune, providerId: "10", name: "Complete Collection", membersKnown: true };

let library: LibraryCollectionsResponse;
let libraries: Array<{ id: string; name: string; mediaType: string; rootPath: string }>;

const reply = (ok: boolean, text = "") => Promise.resolve({ ok, status: ok ? 200 : 400, text: async () => text, json: async () => (text ? JSON.parse(text) : {}) } as Response);
const calls = () => mocks.authedFetch.mock.calls.map(([url, init]) => ({ url: String(url), method: (init as RequestInit | undefined)?.method ?? "GET", body: (init as RequestInit | undefined)?.body ? JSON.parse(String((init as RequestInit).body)) : undefined }));

function renderPage(withLibraryCollections = true) {
  const router = createMemoryRouter(
    [{
      path: "/",
      element: <CollectionsPage />,
      loader: () => ({ collections: [], libraries, qualityProfiles: [], membersByCollection: {}, ...(withLibraryCollections ? { libraryCollections: library } : {}) })
    }],
    { initialEntries: ["/"] }
  );
  return render(<RouterProvider router={router} />);
}

beforeEach(() => {
  mocks.authedFetch.mockReset();
  mocks.authedFetch.mockImplementation(() => reply(true));
  for (const fn of Object.values(mocks.toast)) fn.mockClear();
  library = {
    collections: [matrix, ironMan, dune],
    recommended: [{ ...matrix, why: "You have 2 of 4 films, so 2 more would complete The Matrix Collection." }]
  };
  libraries = [{ id: "l1", name: "Films", mediaType: "movies", rootPath: "/films" }];
});

describe("Your collections (#1425)", () => {
  it("lists every collection a held film belongs to, each film marked held, wanted or missing, and counts them", async () => {
    renderPage();

    const section = (await screen.findByRole("heading", { name: /Your collections/ })).closest("section")!;
    const matrixFilms = within(within(section).getByRole("list", { name: "Films of The Matrix Collection" }));
    const status = (title: string) => within(matrixFilms.getByText(`${title}`, { exact: false }).closest("li")!);
    expect(status("The Matrix (1999)").getByText("Held")).toBeInTheDocument();
    expect(status("The Matrix Reloaded (2003)").getByText("Wanted")).toBeInTheDocument();
    expect(status("The Matrix Revolutions (2003)").getByText("Missing")).toBeInTheDocument();
    expect(status("The Matrix Resurrections (2021)").getByText("Missing")).toBeInTheDocument();
    expect(matrixFilms.getByRole("link", { name: "The Matrix (1999)" })).toHaveAttribute("href", "/movies/movie-603");
    expect(matrixFilms.queryByRole("link", { name: "The Matrix Revolutions (2003)" })).toBeNull();
    expect(within(section).getByText("Iron Man Collection")).toBeInTheDocument();
    expect(within(section).getByText("Dune Collection")).toBeInTheDocument();
    expect(within(section).getAllByText("Following")).toHaveLength(1);
    expect(within(section).getAllByText("Not followed")).toHaveLength(2);
    expect(within(section).getByText("1 wanted")).toBeInTheDocument();
    expect(within(section).getByText("2 held")).toBeInTheDocument();
    expect(within(section).getByText("2 missing")).toBeInTheDocument();
  });

  it("shows an excluded film as Excluded and does not count it as missing", async () => {
    renderPage();

    const section = (await screen.findByRole("heading", { name: /Your collections/ })).closest("section")!;
    const films = within(section).getByRole("list", { name: "Films of Iron Man Collection" });
    const row = within(films).getByText("Iron Man 3 (2013)").closest("li")!;
    expect(row).toHaveAttribute("data-status", "excluded");
    expect(within(row).getByText("Excluded")).toBeInTheDocument();
    expect(within(row).queryByText("Missing")).toBeNull();
  });

  it("says only the films held are shown when the collection's own list could not be read, and offers no add", async () => {
    renderPage();

    const section = (await screen.findByRole("heading", { name: /Your collections/ })).closest("section")!;
    expect(within(section).getByText(/could not be read just now, so only the films you hold are shown/)).toBeInTheDocument();
    expect(within(section).queryByRole("button", { name: /of Dune Collection/ })).toBeNull();
  });

  it("offers no add on a collection with nothing missing", async () => {
    library = { collections: [complete], recommended: [] };
    renderPage();

    await screen.findByRole("heading", { name: /Your collections/ });
    expect(screen.queryByRole("button", { name: /^Add/ })).toBeNull();
    expect(screen.queryByRole("heading", { name: /Recommended/ })).toBeNull();
    expect(screen.getByText("0 missing")).toBeInTheDocument();
  });

  it("one action adds the missing films, says what was added, and reads the page again", async () => {
    mocks.authedFetch.mockImplementation((url: string) => (String(url).includes("add-missing")
      ? reply(true, JSON.stringify({ collectionId: "c1", collectionName: "The Matrix Collection", addedCount: 2, message: "Added 2 films of The Matrix Collection, and Deluno now follows the collection.", searchRequested: true }))
      : reply(true)));
    renderPage();

    const section = (await screen.findByRole("heading", { name: /Your collections/ })).closest("section")!;
    fireEvent.click(within(section).getByRole("button", { name: "Add 2 missing films of The Matrix Collection" }));

    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith("Added 2 films of The Matrix Collection, and Deluno now follows the collection."));
    expect(calls().find((call) => call.method === "POST")).toMatchObject({ url: "/api/movie-collections/from-library/2344/add-missing", body: { libraryId: "l1" } });
  });

  it("says 'the missing film' when one is missing, and says why the add failed in the server's own sentence", async () => {
    library = { collections: [{ ...ironMan, members: [member("68721", "Iron Man 3", 2013, "missing")] }], recommended: [] };
    mocks.authedFetch.mockImplementation(() => reply(false, JSON.stringify({ errors: { libraryId: ["Create a movie library first, so Deluno knows where the missing films go."] } })));
    renderPage();

    fireEvent.click(await screen.findByRole("button", { name: "Add the missing film of Iron Man Collection" }));

    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalledWith("Create a movie library first, so Deluno knows where the missing films go."));
  });

  it("falls back to a plain sentence when the server gives none, and the buttons are off while no movie library exists", async () => {
    mocks.authedFetch.mockImplementation(() => reply(false, "not json"));
    renderPage();
    const button = await screen.findAllByRole("button", { name: "Add 2 missing films of The Matrix Collection" });
    fireEvent.click(button[0]);
    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalledWith("The missing films of The Matrix Collection could not be added."));
  });

  it("disables the add when there is no movie library", async () => {
    libraries = [];
    renderPage();

    const buttons = await screen.findAllByRole("button", { name: "Add 2 missing films of The Matrix Collection" });
    for (const button of buttons) expect(button).toBeDisabled();
  });

  it("asks which library the films go to only when there is more than one, and sends the one chosen", async () => {
    libraries = [{ id: "l1", name: "Films", mediaType: "movies", rootPath: "/films" }, { id: "l3", name: "Kids films", mediaType: "movies", rootPath: "/kids" }, { id: "l2", name: "Shows", mediaType: "tv", rootPath: "/shows" }];
    renderPage();

    const choose = await screen.findByRole("combobox", { name: "Library for the missing films" });
    expect(within(choose).getAllByRole("option").map((option) => option.textContent)).toEqual(["Films", "Kids films"]);
    fireEvent.change(choose, { target: { value: "l3" } });
    fireEvent.click(screen.getAllByRole("button", { name: "Add 2 missing films of The Matrix Collection" })[0]);

    await waitFor(() => expect(calls().find((call) => call.method === "POST")?.body).toEqual({ libraryId: "l3" }));
  });

  it("does not ask for a library when there is just one", async () => {
    renderPage();

    await screen.findByRole("heading", { name: /Your collections/ });
    expect(screen.queryByRole("combobox", { name: "Library for the missing films" })).toBeNull();
  });
});

describe("Recommended collections (#1425)", () => {
  it("suggests the collections worth completing, each with why, and adds their missing films from there", async () => {
    renderPage();

    const section = (await screen.findByRole("heading", { name: /Recommended/ })).closest("section")!;
    expect(within(section).getByText("The Matrix Collection")).toBeInTheDocument();
    expect(within(section).getByText("You have 2 of 4 films, so 2 more would complete The Matrix Collection.")).toBeInTheDocument();
    fireEvent.click(within(section).getByRole("button", { name: "Add 2 missing films of The Matrix Collection" }));

    await waitFor(() => expect(calls().some((call) => call.method === "POST" && call.url.endsWith("/2344/add-missing"))).toBe(true));
  });

  it("has no Recommended section when nothing is worth suggesting", async () => {
    library = { ...library, recommended: [] };
    renderPage();

    await screen.findByRole("heading", { name: /Your collections/ });
    expect(screen.queryByRole("heading", { name: /Recommended/ })).toBeNull();
  });

  it("shows neither section, and the follow prompt, when no film belongs to a collection", async () => {
    library = { collections: [], recommended: [] };
    renderPage();

    expect(await screen.findByText("No collections yet")).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: /Your collections/ })).toBeNull();
  });

  it("reads the same when the loader gave no library collections at all", async () => {
    renderPage(false);

    expect(await screen.findByText("No collections yet")).toBeInTheDocument();
  });
});
