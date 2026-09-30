import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { authedFetch } from "../../lib/use-auth";
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
