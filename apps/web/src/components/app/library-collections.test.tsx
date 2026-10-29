import { fireEvent, render, screen, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { LibraryCollections, MEMBER_STATUS, addMissingLabel } from "./library-collections";
import type { LibraryCollectionItem, LibraryItem } from "../../lib/api";

const film = (id: string, title: string, status: "held" | "wanted" | "missing", year: number | null = 2000, excluded = false) => ({
  providerId: id, title, releaseYear: year, posterUrl: null, status, localMovieId: status === "missing" ? null : `m${id}`, isExcluded: excluded
});

const collection = (over: Partial<LibraryCollectionItem> = {}): LibraryCollectionItem => ({
  providerId: "1", name: "Alpha Collection", overview: null, posterUrl: null, followedCollectionId: null, following: false,
  heldCount: 1, wantedCount: 0, missingCount: 1, membersKnown: true, why: null,
  members: [film("10", "One", "held"), film("11", "Two", "missing")], ...over
});

const libraries = [{ id: "l1", name: "Films" }] as LibraryItem[];

function show(items: LibraryCollectionItem[], extra: { recommended?: LibraryCollectionItem[]; busy?: string | null; libs?: LibraryItem[] } = {}) {
  const onAddMissing = vi.fn();
  render(
    <LibraryCollections
      recommended={extra.recommended ?? []}
      collections={items}
      libraries={extra.libs ?? libraries}
      libraryId="l1"
      onLibraryChange={() => undefined}
      busyProviderId={extra.busy ?? null}
      onAddMissing={onAddMissing}
    />
  );
  return onAddMissing;
}

describe("Collections from the library (#1425)", () => {
  it("says each status in the words and tones the screen uses", () => {
    expect(MEMBER_STATUS).toEqual({
      held: { label: "Held", tone: "ok" },
      wanted: { label: "Wanted", tone: "info" },
      missing: { label: "Missing", tone: "warn" }
    });
    expect(addMissingLabel({ missingCount: 1 })).toBe("Add the missing film");
    expect(addMissingLabel({ missingCount: 3 })).toBe("Add 3 missing films");
  });

  it("shows nothing when no film belongs to a collection", () => {
    const { container } = render(
      <LibraryCollections recommended={[]} collections={[]} libraries={libraries} libraryId="l1" onLibraryChange={() => undefined} busyProviderId={null} onAddMissing={() => undefined} />
    );
    expect(container).toBeEmptyDOMElement();
  });

  it("writes a film with its year, or by its title alone when the year is unknown, and links the ones in the library", () => {
    show([collection({ members: [film("10", "One", "held", 1999), film("11", "Two", "missing", null)] })]);

    const films = within(screen.getByRole("list", { name: "Films of Alpha Collection" }));
    expect(films.getByRole("link", { name: "One (1999)" })).toHaveAttribute("href", "/movies/m10");
    expect(films.getByText("Two")).toBeInTheDocument();
    expect(films.queryByRole("link", { name: "Two" })).toBeNull();
    expect(films.getByText("Two").closest("li")).toHaveAttribute("data-status", "missing");
  });

  it("marks a collection followed or not, and counts wanted films only when there are some", () => {
    show([
      collection({ providerId: "1", name: "Followed", following: true, wantedCount: 2, heldCount: 0, missingCount: 0 }),
      collection({ providerId: "2", name: "Loose", following: false, wantedCount: 0 })
    ]);

    expect(screen.getAllByText("Following")).toHaveLength(1);
    expect(screen.getAllByText("Not followed")).toHaveLength(1);
    expect(screen.getByText("2 wanted")).toBeInTheDocument();
    expect(screen.getAllByText(/ wanted$/)).toHaveLength(1);
  });

  it("colours the missing count only when something is missing, and hides it when the films are not known", () => {
    show([
      collection({ providerId: "1", name: "Short", missingCount: 2 }),
      collection({ providerId: "2", name: "Full", missingCount: 0, heldCount: 2 }),
      collection({ providerId: "3", name: "Blind", membersKnown: false, missingCount: 0 })
    ]);

    expect(screen.getByText("2 missing").className).toContain("text-warning");
    expect(screen.getByText("0 missing").className).toContain("text-muted-foreground");
    expect(screen.getAllByText(/ missing$/)).toHaveLength(2);
  });

  it("shows an excluded film as excluded only, and any other by its own status", () => {
    show([collection({ members: [film("10", "One", "held"), film("11", "Two", "missing", 2001, true)] })]);

    const films = within(screen.getByRole("list", { name: "Films of Alpha Collection" }));
    expect(within(films.getByText("One (2000)").closest("li")!).getByText("Held")).toBeInTheDocument();
    const excluded = films.getByText("Two (2001)").closest("li")!;
    expect(within(excluded).getByText("Excluded")).toBeInTheDocument();
    expect(within(excluded).queryByText("Missing")).toBeNull();
    expect(excluded).toHaveAttribute("data-status", "excluded");
  });

  it("draws the poster when there is one and a film icon when there is none", () => {
    const { container } = render(
      <LibraryCollections
        recommended={[]}
        collections={[collection({ providerId: "1", posterUrl: "https://image.example/a.jpg" }), collection({ providerId: "2", name: "Bare" })]}
        libraries={libraries}
        libraryId="l1"
        onLibraryChange={() => undefined}
        busyProviderId={null}
        onAddMissing={() => undefined}
      />
    );

    const posters = container.querySelectorAll("div.rounded-xl");
    expect(posters).toHaveLength(2);
    expect(posters[0].querySelector("img")).not.toBeNull();
    expect(posters[1].querySelector("img")).toBeNull();
    expect(posters[1].querySelector("svg")).not.toBeNull();
    expect(posters[0].className).toContain("overflow-hidden");
  });

  it("adds the missing films of the collection pressed, and is off while one is being added", () => {
    const onAdd = show([collection({ providerId: "1", name: "Alpha Collection", missingCount: 1 }), collection({ providerId: "2", name: "Beta Collection", missingCount: 2 })]);

    fireEvent.click(screen.getByRole("button", { name: "Add the missing film of Alpha Collection" }));
    expect(onAdd).toHaveBeenCalledWith(expect.objectContaining({ providerId: "1" }));
    expect(screen.getByRole("button", { name: "Add 2 missing films of Beta Collection" })).toBeEnabled();
  });

  it("turns every add off while one runs, and shows the one running as busy", () => {
    show([collection({ providerId: "1", name: "Alpha Collection" }), collection({ providerId: "2", name: "Beta Collection", missingCount: 2 })], { busy: "1" });

    const running = screen.getByRole("button", { name: "Add the missing film of Alpha Collection" });
    const other = screen.getByRole("button", { name: "Add 2 missing films of Beta Collection" });
    expect(running).toBeDisabled();
    expect(running.querySelector("svg.animate-spin")).not.toBeNull();
    expect(other).toBeDisabled();
    expect(other.querySelector("svg.animate-spin")).toBeNull();
  });

  it("turns the add off when there is no movie library, and offers a choice of library only when there are several", () => {
    show([collection()], { libs: [] });
    expect(screen.getByRole("button", { name: "Add the missing film of Alpha Collection" })).toBeDisabled();
    expect(screen.queryByRole("combobox")).toBeNull();
  });

  it("lists the recommended collections with their reason and their own add", () => {
    const rec = collection({ providerId: "7", name: "Seven Collection", why: "You have 2 of 3 films. One more completes Seven Collection.", missingCount: 1 });
    const onAdd = show([rec], { recommended: [rec] });

    const section = screen.getByRole("heading", { name: /Recommended/ }).closest("section")!;
    expect(within(section).getByText("You have 2 of 3 films. One more completes Seven Collection.")).toBeInTheDocument();
    fireEvent.click(within(section).getByRole("button", { name: "Add the missing film of Seven Collection" }));
    expect(onAdd).toHaveBeenCalledWith(rec);
  });
});
