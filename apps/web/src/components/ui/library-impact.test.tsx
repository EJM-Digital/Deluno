import { fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";
import type { LibraryItem } from "../../lib/api";
import { LibraryImpactLinks, LibraryImpactPanel } from "./library-impact";

const lib = (id: string, name: string, mediaType: string) => ({ id, name, mediaType }) as LibraryItem;
const wrap = (node: React.ReactNode) => render(<MemoryRouter>{node}</MemoryRouter>);

describe("the libraries a setting is used by", () => {
  it("says so plainly when no library uses it, with the caller's own words if given", () => {
    const { rerender } = wrap(<LibraryImpactLinks libraries={[]} />);
    expect(screen.getByText("Not used by any library yet")).toBeTruthy();
    rerender(<MemoryRouter><LibraryImpactLinks libraries={[]} emptyLabel="None" className="x" /></MemoryRouter>);
    expect(screen.getByText("None").className).toContain("x");
  });

  it("links each library to its settings, adds the type unless the name already says it, and counts the rest past four", () => {
    const click = vi.fn();
    wrap(
      <div onClick={click}>
        <LibraryImpactLinks libraries={[lib("a b", "Films", "movies"), lib("2", "Movies", "movies"), lib("3", "Shows", "tv"), lib("4", "TV Shows", "tv"), lib("5", "Five", "movies"), lib("6", "Six", "movies")]} />
      </div>
    );
    const films = screen.getByRole("link", { name: /^Films ?Movies$/ });
    expect(films.getAttribute("href")).toBe("/settings/libraries?libraryId=a%20b");
    expect(screen.getByRole("link", { name: "Movies" })).toBeTruthy();
    expect(screen.getByRole("link", { name: /^Shows ?TV$/ })).toBeTruthy();
    expect(screen.getByRole("link", { name: "TV Shows" })).toBeTruthy();
    expect(screen.queryByText("Five")).toBeNull();
    expect(screen.getByText("+2 more")).toBeTruthy();
    fireEvent.click(films);
    expect(click).not.toHaveBeenCalled();
  });
});

describe("a short list of libraries", () => {
  it("shows every library with no count of more", () => {
    wrap(<LibraryImpactLinks libraries={[lib("1", "Films", "movies")]} />);
    expect(screen.queryByText(/more$/)).toBeNull();
  });
});

describe("the map of where a library is configured", () => {
  it("lists each setting with its value and detail, in the tone it was given", () => {
    wrap(
      <LibraryImpactPanel
        items={[
          { label: "Quality", value: "HD", href: "/settings/quality", tone: "ok", detail: "Cutoff 1080p" },
          { label: "Folders", value: "2", href: "/settings/folders" }
        ]}
      />
    );
    expect(screen.getByText("Where this library is configured")).toBeTruthy();
    expect(screen.getByText(/change how Deluno searches for/)).toBeTruthy();
    const quality = screen.getByRole("link", { name: /Quality/ });
    expect(quality.getAttribute("href")).toBe("/settings/quality");
    expect(quality.className).toContain("border-success/30");
    expect(screen.getByText("Cutoff 1080p")).toBeTruthy();
    expect(screen.getByRole("link", { name: /Folders/ }).className).toContain("border-hairline");
  });

  it("takes its own title and description", () => {
    wrap(<LibraryImpactPanel title="Custom title" description="Custom words" items={[]} className="mine" />);
    expect(screen.getByText("Custom title")).toBeTruthy();
    expect(screen.getByText("Custom words")).toBeTruthy();
  });
});
