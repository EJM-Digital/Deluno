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
});
