import { render, screen } from "@testing-library/react";
import type { ComponentProps } from "react";
import { MemoryRouter } from "react-router-dom";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { LibraryActions } from "./library-actions";

function renderActions(overrides: Partial<ComponentProps<typeof LibraryActions>> = {}) {
  return render(
    <MemoryRouter>
      <LibraryActions
        label="TV"
        singular="show"
        shownCount={2}
        onToggleCreate={vi.fn()}
        isUpdatingMetadata={false}
        onUpdateMetadata={vi.fn()}
        showSeasonPassLink
        {...overrides}
      />
    </MemoryRouter>
  );
}

/**
 * #959: at 1669px on TV, Season Pass was the one extra action Movies does not
 * have, and it was what tipped the actions row into wrapping onto its own
 * line under the search row (four lines total, where Movies at the same
 * width has three). Its label now hides below 2100px, leaving an icon and a
 * tooltip — the same treatment as the refresh button beside it — so the row
 * loses the width without losing the control. jsdom cannot assert the
 * 1669px measurement itself, so this asserts the two things that survive the
 * responsive class: the link keeps a stable accessible name regardless of
 * whether the text paints, and the label is marked hidden by default.
 */
describe("LibraryActions Season Pass link (#959)", () => {
  it("keeps a stable accessible name and hides its label below 2100px", () => {
    renderActions();

    const link = screen.getByRole("link", { name: "Season Pass" });
    expect(link).toBeInTheDocument();

    const label = screen.getByText("Season Pass", { selector: "span" });
    expect(label).toHaveClass("hidden");
    expect(label).toHaveClass("min-[2100px]:inline");
  });

  it("the Season Pass link opens /tv/season-pass", () => {
    renderActions();

    expect(screen.getByRole("link", { name: "Season Pass" })).toHaveAttribute("href", "/tv/season-pass");
  });

  it("does not render the Season Pass link for movies", () => {
    renderActions({ showSeasonPassLink: false });

    expect(screen.queryByRole("link", { name: "Season Pass" })).not.toBeInTheDocument();
  });
});

describe("LibraryActions search and refresh buttons", () => {
  const search = () => screen.getByRole("button", { name: /^Search these|^Searching/ });

  it("leaves the Season Pass link out unless the shelf asks for it", () => {
    renderActions({ showSeasonPassLink: undefined });
    expect(screen.queryByRole("link", { name: "Season Pass" })).not.toBeInTheDocument();
  });

  it("adds a title, and says the singular in the button", async () => {
    const onToggleCreate = vi.fn();
    renderActions({ onToggleCreate, showSeasonPassLink: false });
    await userEvent.click(screen.getByRole("button", { name: "Add show" }));
    expect(onToggleCreate).toHaveBeenCalledTimes(1);
  });

  it("searches everything on screen, and says how many in its tooltip", async () => {
    const onSearchShown = vi.fn();
    renderActions({ shownCount: 5, label: "shows", onSearchShown });
    expect(search()).toHaveTextContent("Search these 5");
    expect(search()).toHaveAttribute("title", "Search now for all 5 shows on screen. Narrow the shelf first to search fewer.");
    expect(search()).toBeEnabled();
    await userEvent.click(search());
    expect(onSearchShown).toHaveBeenCalledTimes(1);
  });

  it("names the one title on screen", () => {
    renderActions({ shownCount: 1, singular: "Show", onSearchShown: vi.fn() });
    expect(search()).toHaveAttribute("title", "Search now for the show on screen");
    expect(search()).toBeEnabled();
  });

  it("cannot search when nothing is on screen, and says so", () => {
    renderActions({ shownCount: 0, label: "shows", onSearchShown: vi.fn() });
    expect(search()).toBeDisabled();
    expect(search()).toHaveAttribute("title", "No shows on screen to search for");
  });

  it("shows that a search is running, and cannot start another", () => {
    renderActions({ isSearchingShown: true, onSearchShown: vi.fn() });
    const button = screen.getByRole("button", { name: "Searching…" });
    expect(button).toBeDisabled();
    expect(button.querySelector("svg")).toHaveClass("animate-pulse");
  });

  it("does not pulse the search icon when no search is running", () => {
    renderActions({ onSearchShown: vi.fn() });
    expect(search().querySelector("svg")).not.toHaveClass("animate-pulse");
    expect(search().querySelector("svg")).toHaveClass("h-4 w-4");
  });

  it("cannot search when the page gives it nothing to run", () => {
    renderActions({ shownCount: 3 });
    expect(search()).toBeDisabled();
  });

  it("queues a metadata refresh, spinning while one is queued", async () => {
    const onUpdateMetadata = vi.fn();
    const view = renderActions({ onUpdateMetadata, singular: "Show" });
    const refresh = screen.getByRole("button", { name: "Update all metadata" });
    expect(refresh).toHaveAttribute("title", "Queue a metadata refresh for every show in Deluno");
    expect(refresh.querySelector("svg")).not.toHaveClass("animate-spin");
    expect(refresh.querySelector("svg")).toHaveClass("h-4 w-4");
    await userEvent.click(refresh);
    expect(onUpdateMetadata).toHaveBeenCalledTimes(1);
    view.unmount();

    renderActions({ isUpdatingMetadata: true });
    const busy = screen.getByRole("button", { name: "Update all metadata" });
    expect(busy).toBeDisabled();
    expect(busy.querySelector("svg")).toHaveClass("animate-spin");
  });
});
