import { fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";
import { IndexerLink } from "./indexer-link";

const show = (indexerName: string | null | undefined, className?: string) =>
  render(<MemoryRouter><button type="button" onClick={rowOpened}><IndexerLink indexerName={indexerName} className={className}>Some.Release.2026</IndexerLink></button></MemoryRouter>);
const rowOpened = vi.fn();

describe("a release name linked to its indexer (#1426)", () => {
  it("links to the indexer's page, says where in its title, and keeps the click off the row it sits in", () => {
    rowOpened.mockClear();
    show("NZBgeek");
    const link = screen.getByRole("link", { name: "Some.Release.2026" });
    expect(link).toHaveAttribute("href", "/indexers/indexers?indexer=NZBgeek");
    expect(link).toHaveAttribute("title", "Open NZBgeek");
    fireEvent.click(link);
    expect(rowOpened).not.toHaveBeenCalled();
  });

  it("ignores spaces around the name, and takes a class of its own", () => {
    show("  NZBgeek  ", "extra-class");
    const link = screen.getByRole("link");
    expect(link).toHaveAttribute("href", "/indexers/indexers?indexer=NZBgeek");
    expect(link.className).toBe("hover:text-primary hover:underline focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-ring extra-class");
  });

  it("without a class of its own wears only its own", () => {
    show("NZBgeek");
    expect(screen.getByRole("link").className).toBe("hover:text-primary hover:underline focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-ring");
  });

  it.each([[null], [undefined], [""], ["   "]])("with no indexer name (%j) shows the words and no link", (name) => {
    show(name);
    expect(screen.queryByRole("link")).toBeNull();
    expect(screen.getByText("Some.Release.2026")).toBeInTheDocument();
  });
});
