import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { EmptyState } from "./empty-state";
import { EmptyLibraryArt, NoResultsArt, OfflineArt } from "./illustrations";

// The empty-state card: which illustration, the words, the actions and the sizes (shell area, screen states).

afterEach(() => cleanup());

function artOf(container: HTMLElement) {
  return container.querySelector(".relative.mb-4")?.innerHTML ?? "";
}

describe("the EmptyState card", () => {
  it("draws the library, search or offline illustration for its variant", () => {
    const library = render(<EmptyState title="Nothing here" />);
    expect(artOf(library.container)).toBe(render(<EmptyLibraryArt size={150} />).container.innerHTML);
    library.unmount();

    const search = render(<EmptyState variant="search" title="No matches" />);
    expect(artOf(search.container)).toBe(render(<NoResultsArt size={150} />).container.innerHTML);
    search.unmount();

    const offline = render(<EmptyState variant="offline" title="Offline" />);
    expect(artOf(offline.container)).toBe(render(<OfflineArt size={150} />).container.innerHTML);
  });

  it("a custom art node replaces the built-in illustration", () => {
    const { container } = render(<EmptyState title="Mine" art={<span data-testid="own-art" />} />);
    expect(screen.getByTestId("own-art")).toBeTruthy();
    expect(artOf(container)).toBe("<span data-testid=\"own-art\"></span>");
  });

  it("shows the title, the description, the action, the secondary action and the learn-more line", () => {
    render(
      <EmptyState
        title="No films yet"
        description="Add one to start."
        action={<button type="button">Add a film</button>}
        secondaryAction={<a href="/help">Show me how</a>}
        learnMore={<span>Read about libraries</span>}
      />
    );
    expect(screen.getByRole("heading", { name: "No films yet" })).toBeTruthy();
    expect(screen.getByText("Add one to start.")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Add a film" })).toBeTruthy();
    expect(screen.getByRole("link", { name: "Show me how" }).getAttribute("href")).toBe("/help");
    expect(screen.getByText("Read about libraries")).toBeTruthy();
  });

  it("the three sizes use three padding levels and three title sizes", () => {
    const sm = render(<EmptyState title="Small" size="sm" />);
    expect(sm.container.firstElementChild!.className).toContain("px-4 py-6");
    expect(screen.getByRole("heading").className).toContain("text-sm");
    sm.unmount();

    const compact = render(<EmptyState title="Middle" size="compact" />);
    expect(compact.container.firstElementChild!.className).toContain("py-10");
    expect(screen.getByRole("heading").className).toContain("text-base");
    compact.unmount();

    const full = render(<EmptyState title="Big" />);
    expect(full.container.firstElementChild!.className).toContain("py-14");
    expect(screen.getByRole("heading").className).toContain("text-lg");
  });

  it("draws the icon on a tile for the custom variant, and nothing at all when it has neither icon nor art", () => {
    const Icon = ({ className }: { className?: string }) => <svg data-testid="icon" className={className} />;
    const withIcon = render(<EmptyState variant="custom" icon={Icon} title="Custom" />);
    expect(screen.getByTestId("icon")).toBeTruthy();
    expect(artOf(withIcon.container)).toContain("data-testid=\"icon\"");
    withIcon.unmount();
    const bare = render(<EmptyState variant="custom" title="Custom" />);
    expect(bare.container.querySelector(".relative.mb-4")).toBeNull();
  });

  it("draws each illustration at the size of the card", () => {
    const compact = render(<EmptyState size="compact" title="Compact" />);
    expect(artOf(compact.container)).toBe(render(<EmptyLibraryArt size={112} />).container.innerHTML);
    compact.unmount();
    const small = render(<EmptyState size="sm" title="Small" />);
    expect(artOf(small.container)).toBe(render(<EmptyLibraryArt size={88} />).container.innerHTML);
  });

  it("sets the padding and the title by the size of the card", () => {
    const sizes = (size: "default" | "compact" | "sm") => {
      const view = render(<EmptyState size={size} title="Sized" />);
      const result = [view.container.firstElementChild!.className, screen.getByRole("heading", { name: "Sized" }).className];
      view.unmount();
      return result;
    };
    const [defaultCard, defaultTitle] = sizes("default");
    const [compactCard, compactTitle] = sizes("compact");
    const [smallCard, smallTitle] = sizes("sm");
    expect(defaultCard).toContain("px-8 py-14");
    expect(compactCard).toContain("px-6 py-10");
    expect(smallCard).toContain("px-4 py-6");
    expect(defaultTitle).toContain("text-lg");
    expect(compactTitle).toContain("text-base");
    expect(smallTitle).toContain("text-sm");
  });

  it("shows a secondary action alone, and leaves out the description and learn-more line it was not given", () => {
    const { container } = render(<EmptyState title="Only secondary" secondaryAction={<a href="/x">Go</a>} className="extra" />);
    expect(screen.getByRole("link", { name: "Go" })).toBeTruthy();
    expect(container.querySelector("p")).toBeNull();
    expect(container.firstElementChild!.className).toContain("extra");
    const none = render(<EmptyState title="Nothing else" />);
    expect(none.container.querySelectorAll(".mt-5, .mt-3")).toHaveLength(0);
  });
});
