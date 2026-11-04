import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { authedFetch } from "../../lib/use-auth";
import { toast } from "../shell/toaster";
import { LibraryMembershipCard, type LibraryMembership } from "./library-membership-card";

vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../shell/toaster", () => ({ toast: { success: vi.fn(), error: vi.fn() } }));

const memberships: LibraryMembership[] = [
  { libraryId: "lib-hd", libraryName: "Movies", hasFile: true, wantedStatus: "upgrade", canRemove: false, canMove: false },
  { libraryId: "lib-4k", libraryName: "Movies 4K", hasFile: false, wantedStatus: "missing", canRemove: true, canMove: true, notWantedHere: true }
];

function renderCard(onChanged = vi.fn()) {
  render(
    <LibraryMembershipCard
      route="/api/movies"
      mediaId="movie-1"
      noun="film"
      libraries={[]}
      memberships={memberships}
      selectedLibraryId="lib-hd"
      onSelect={vi.fn()}
      onChanged={onChanged}
    />
  );
  return onChanged;
}

describe("LibraryMembershipCard, a film that stopped being wanted in one library (#1065)", () => {
  beforeEach(() => {
    vi.mocked(authedFetch).mockReset();
  });

  it("says which library stopped and offers Want it again for that library only", () => {
    renderCard();

    expect(screen.getByText("Not wanted here")).toBeTruthy();
    expect(screen.getAllByRole("button", { name: /want it again/i })).toHaveLength(1);
    expect(screen.getByRole("button", { name: "Want it again in Movies 4K" })).toBeTruthy();
  });

  it("posts the want-again action for that library and reloads", async () => {
    vi.mocked(authedFetch).mockResolvedValue(new Response("[]", { status: 200 }));
    const onChanged = renderCard();

    await userEvent.click(screen.getByRole("button", { name: "Want it again in Movies 4K" }));

    await waitFor(() => expect(onChanged).toHaveBeenCalled());
    expect(vi.mocked(authedFetch).mock.calls[0]?.[0]).toBe("/api/movies/movie-1/libraries/lib-4k/want-again");
    expect(vi.mocked(authedFetch).mock.calls[0]?.[1]).toMatchObject({ method: "POST" });
  });
});

describe("LibraryMembershipCard, Want it again is for films (#1420)", () => {
  it("offers no library Want it again on a show, even when a row says it stopped being wanted there", () => {
    render(
      <LibraryMembershipCard
        route="/api/series"
        mediaId="show-1"
        noun="show"
        libraries={[]}
        memberships={memberships}
        selectedLibraryId="lib-hd"
        onSelect={vi.fn()}
        onChanged={vi.fn()}
      />
    );

    expect(screen.queryByRole("button", { name: /want it again/i })).toBeNull();
    expect(screen.queryByText("Not wanted here")).toBeNull();
  });
});

describe("LibraryMembershipCard, the libraries a title is in (#530)", () => {
  const libraries = [
    { id: "lib-hd", name: "Movies", mediaType: "movies" },
    { id: "lib-4k", name: "Movies 4K", mediaType: "movies" },
    { id: "lib-kids", name: "Kids", mediaType: "movies" }
  ] as never;
  const held: LibraryMembership[] = [
    { libraryId: "lib-hd", libraryName: "Movies", hasFile: true, wantedStatus: "covered", canRemove: false, canMove: false },
    { libraryId: "lib-4k", libraryName: "Movies 4K", hasFile: false, wantedStatus: "missing", canRemove: true, canMove: true }
  ];

  function renderFull(over: Partial<Parameters<typeof LibraryMembershipCard>[0]> = {}) {
    const onSelect = vi.fn();
    const onChanged = vi.fn();
    render(
      <LibraryMembershipCard route="/api/movies" mediaId="movie-1" noun="film" libraries={libraries} memberships={held} selectedLibraryId="lib-hd" onSelect={onSelect} onChanged={onChanged} {...over} />
    );
    return { onSelect, onChanged };
  }

  beforeEach(() => {
    vi.mocked(authedFetch).mockReset().mockResolvedValue(new Response("{}", { status: 200 }));
    vi.mocked(toast.success).mockClear();
    vi.mocked(toast.error).mockClear();
  });

  it("lists each library the film is in with whether it holds a file there", () => {
    renderFull();

    expect(screen.getByText("Has a file")).toBeTruthy();
    expect(screen.getByText("No file")).toBeTruthy();
    expect(screen.getByText("in 2")).toBeTruthy();
  });

  it("Use for search points the page at that library", async () => {
    const { onSelect } = renderFull();

    await userEvent.click(screen.getAllByRole("button", { name: "Use for search" })[0]);

    expect(onSelect).toHaveBeenCalled();
  });

  it("Keep in another library asks which and posts the chosen library, then says where it is also kept", async () => {
    const { onChanged } = renderFull();

    await userEvent.click(screen.getByRole("button", { name: "Keep in another library…" }));
    const dialog = await screen.findByRole("dialog", { name: "Keep this film in another library too?" });
    await userEvent.click(within(dialog).getByRole("button", { name: "Keep it there too" }));

    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("Also kept in Kids."));
    expect(vi.mocked(authedFetch).mock.calls[0][0]).toBe("/api/movies/movie-1/libraries/");
    expect(JSON.parse(String((vi.mocked(authedFetch).mock.calls[0][1] as RequestInit).body))).toEqual({ libraryId: "lib-kids" });
    expect(onChanged).toHaveBeenCalled();
  });

  it("a show is never offered Keep in another library", () => {
    renderFull({ route: "/api/series", noun: "show" });

    expect(screen.queryByRole("button", { name: /Keep in another library/ })).toBeNull();
  });

  it("Move re-files the copy that holds no file into the chosen library", async () => {
    renderFull();

    await userEvent.click(screen.getByRole("button", { name: "Move…" }));
    const dialog = await screen.findByRole("dialog", { name: "Move out of Movies 4K?" });
    await userEvent.click(within(dialog).getByRole("button", { name: "Move" }));

    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("Moved to Kids."));
    expect(vi.mocked(authedFetch).mock.calls[0][0]).toBe("/api/movies/movie-1/libraries/lib-4k");
    expect((vi.mocked(authedFetch).mock.calls[0][1] as RequestInit).method).toBe("PUT");
  });

  it("Remove asks first, then stops wanting the film in that library", async () => {
    renderFull();

    await userEvent.click(screen.getByRole("button", { name: "Remove" }));
    const dialog = await screen.findByRole("dialog", { name: "Stop wanting it in Movies 4K?" });
    expect(vi.mocked(authedFetch)).not.toHaveBeenCalled();
    await userEvent.click(within(dialog).getByRole("button", { name: "Remove from library" }));

    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("No longer in Movies 4K."));
    expect((vi.mocked(authedFetch).mock.calls[0][1] as RequestInit).method).toBe("DELETE");
  });

  it("a library that holds the file cannot be removed or moved, and says nothing is offered for it", () => {
    renderFull();

    const row = screen.getByText("Movies").closest("[role='row']") as HTMLElement;
    expect(within(row).queryByRole("button", { name: "Remove" })).toBeNull();
    expect(within(row).queryByRole("button", { name: "Move…" })).toBeNull();
  });

  it("a refusal says what the server said, such as the library holding a file", async () => {
    vi.mocked(authedFetch).mockResolvedValue(new Response(JSON.stringify({ message: "It has a file in Movies 4K." }), { status: 409 }));
    renderFull();

    await userEvent.click(screen.getByRole("button", { name: "Remove" }));
    await userEvent.click(within(await screen.findByRole("dialog", { name: "Stop wanting it in Movies 4K?" })).getByRole("button", { name: "Remove from library" }));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("It has a file in Movies 4K."));
  });

  it("a refusal with no words says it could not be done and gives the code", async () => {
    vi.mocked(authedFetch).mockResolvedValue(new Response("not json", { status: 500 }));
    renderFull();

    await userEvent.click(screen.getByRole("button", { name: "Remove" }));
    await userEvent.click(within(await screen.findByRole("dialog", { name: "Stop wanting it in Movies 4K?" })).getByRole("button", { name: "Remove from library" }));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("That could not be done (HTTP 500)."));
  });

  it("says the first thing the server found wrong when it answers with a list of problems", async () => {
    vi.mocked(authedFetch).mockResolvedValue(new Response(JSON.stringify({ errors: { libraryId: ["That library is full.", "Another."] } }), { status: 400 }));
    renderFull();

    await userEvent.click(screen.getByRole("button", { name: "Remove" }));
    await userEvent.click(within(await screen.findByRole("dialog", { name: "Stop wanting it in Movies 4K?" })).getByRole("button", { name: "Remove from library" }));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("That library is full."));
  });

  it("falls back to the code when the list of problems is empty", async () => {
    vi.mocked(authedFetch).mockResolvedValue(new Response(JSON.stringify({ errors: {} }), { status: 422 }));
    renderFull();

    await userEvent.click(screen.getByRole("button", { name: "Remove" }));
    await userEvent.click(within(await screen.findByRole("dialog", { name: "Stop wanting it in Movies 4K?" })).getByRole("button", { name: "Remove from library" }));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("That could not be done (HTTP 422)."));
  });

  it("says Deluno could not be reached when the request does not get an answer, and keeps the question open", async () => {
    vi.mocked(authedFetch).mockRejectedValue(new Error("offline"));
    const { onChanged } = renderFull();

    await userEvent.click(screen.getByRole("button", { name: "Keep in another library…" }));
    await userEvent.click(within(await screen.findByRole("dialog", { name: "Keep this film in another library too?" })).getByRole("button", { name: "Keep it there too" }));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("That could not be done. Deluno could not be reached."));
    expect(onChanged).not.toHaveBeenCalled();
    expect(screen.getByRole("dialog", { name: "Keep this film in another library too?" })).toBeTruthy();
    expect((screen.getByRole("button", { name: "Keep it there too" }) as HTMLButtonElement).disabled).toBe(false);
  });

  it("closes the Keep question with Cancel without sending anything", async () => {
    renderFull();

    await userEvent.click(screen.getByRole("button", { name: "Keep in another library…" }));
    await userEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Cancel" }));

    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(vi.mocked(authedFetch)).not.toHaveBeenCalled();
  });

  it("closes the Remove question with Cancel without sending anything", async () => {
    renderFull();

    await userEvent.click(screen.getByRole("button", { name: "Remove" }));
    await userEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Cancel" }));

    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(vi.mocked(authedFetch)).not.toHaveBeenCalled();
  });

  it("points the page at the library the film moved to when it was the one in use for search, and leaves the choice alone otherwise", async () => {
    const { onSelect } = renderFull({ selectedLibraryId: "lib-4k" });
    await userEvent.click(screen.getByRole("button", { name: "Move…" }));
    await userEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Move" }));
    await waitFor(() => expect(onSelect).toHaveBeenCalledWith("lib-kids"));
  });

  it("does not change the library in use for search when another copy was moved", async () => {
    const { onSelect, onChanged } = renderFull({ selectedLibraryId: "lib-hd" });
    await userEvent.click(screen.getByRole("button", { name: "Move…" }));
    await userEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Move" }));
    await waitFor(() => expect(onChanged).toHaveBeenCalled());
    expect(onSelect).not.toHaveBeenCalled();
  });

  it("does not point search anywhere new after a Keep, even when the selected library is a moved one", async () => {
    const { onSelect, onChanged } = renderFull({ selectedLibraryId: "lib-4k" });
    await userEvent.click(screen.getByRole("button", { name: "Keep in another library…" }));
    await userEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Keep it there too" }));
    await waitFor(() => expect(onChanged).toHaveBeenCalled());
    expect(onSelect).not.toHaveBeenCalled();
  });

  it("moves search to the remaining library when the one in use is removed, and to nothing when none is left", async () => {
    const { onSelect } = renderFull({ selectedLibraryId: "lib-4k" });
    await userEvent.click(screen.getByRole("button", { name: "Remove" }));
    await userEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Remove from library" }));
    await waitFor(() => expect(onSelect).toHaveBeenCalledWith("lib-hd"));
  });

  it("selects nothing when the only library in use is removed", async () => {
    const only: LibraryMembership[] = [{ libraryId: "lib-4k", libraryName: "Movies 4K", hasFile: false, wantedStatus: "missing", canRemove: true, canMove: false }];
    const { onSelect } = renderFull({ memberships: only, selectedLibraryId: "lib-4k" });
    expect(screen.queryByText(/^in d/)).toBeNull();
    await userEvent.click(screen.getByRole("button", { name: "Remove" }));
    await userEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Remove from library" }));
    await waitFor(() => expect(onSelect).toHaveBeenCalledWith(""));
  });

  it("keeps the selection when a library that is not the one in use is removed", async () => {
    const { onSelect, onChanged } = renderFull({ selectedLibraryId: "lib-hd" });
    await userEvent.click(screen.getByRole("button", { name: "Remove" }));
    await userEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Remove from library" }));
    await waitFor(() => expect(onChanged).toHaveBeenCalled());
    expect(onSelect).not.toHaveBeenCalled();
  });

  it("sorts the libraries by name and by whether they hold a file, in the order the headings are pressed", async () => {
    const three: LibraryMembership[] = [
      { libraryId: "b", libraryName: "Bravo", hasFile: false, wantedStatus: "missing", canRemove: false, canMove: false },
      { libraryId: "a", libraryName: "alpha", hasFile: true, wantedStatus: "covered", canRemove: false, canMove: false },
      { libraryId: "c", libraryName: "Charlie", hasFile: false, wantedStatus: "missing", canRemove: false, canMove: false, notWantedHere: true }
    ];
    renderFull({ memberships: three, libraries: [] as never });
    const names = () => screen.getAllByRole("row").slice(1).map((row) => within(row).getAllByRole("cell")[0].textContent?.slice(0, 7));

    await userEvent.click(within(screen.getByRole("columnheader", { name: /^Library/ })).getByRole("button"));
    expect(names()).toEqual(["alpha", "Bravo", "Charlie"]);
    await userEvent.click(within(screen.getByRole("columnheader", { name: /^Copy/ })).getByRole("button"));
    expect(names()).toEqual(["alpha", "Bravo", "Charlie"]);
    await userEvent.click(within(screen.getByRole("columnheader", { name: /^Copy/ })).getByRole("button"));
    expect(names()).toEqual(["Charlie", "Bravo", "alpha"]);
  });

  it("keeps the film in the library chosen from the list, not just the first one", async () => {
    renderFull({ memberships: [held[0]] });

    await userEvent.click(screen.getByRole("button", { name: "Keep in another library…" }));
    const dialog = await screen.findByRole("dialog", { name: "Keep this film in another library too?" });
    await userEvent.selectOptions(within(dialog).getByRole("combobox"), "lib-kids");
    await userEvent.click(within(dialog).getByRole("button", { name: "Keep it there too" }));

    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("Also kept in Kids."));
    expect(JSON.parse(String((vi.mocked(authedFetch).mock.calls[0][1] as RequestInit).body))).toEqual({ libraryId: "lib-kids" });
  });

  it("keeps the Move question open when the server refuses the move", async () => {
    vi.mocked(authedFetch).mockResolvedValue(new Response(JSON.stringify({ message: "Kids cannot take it." }), { status: 409 }));
    const { onSelect } = renderFull({ selectedLibraryId: "lib-4k" });

    await userEvent.click(screen.getByRole("button", { name: "Move…" }));
    await userEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Move" }));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("Kids cannot take it."));
    expect(screen.getByRole("dialog", { name: "Move out of Movies 4K?" })).toBeTruthy();
    expect(onSelect).not.toHaveBeenCalled();
  });
});
