import { render, screen } from "@testing-library/react";
import type { ComponentProps } from "react";
import { MemoryRouter } from "react-router-dom";
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
