import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it } from "vitest";
import { AlsoWorthALook } from "./needs-you";

afterEach(cleanup);

const view = (leftovers: number, suggestions: number) => ({ alsoWorthALook: { leftovers, suggestions } });

describe("Dashboard: also worth a look", () => {
  it("links \"N leftover folders\" to Queue and \"N suggestions\" to System, on one quiet line", () => {
    render(<MemoryRouter><AlsoWorthALook attention={view(2, 1)} /></MemoryRouter>);
    expect(screen.getByRole("link", { name: "2 leftover folders" })).toHaveAttribute("href", "/queue");
    expect(screen.getByRole("link", { name: "1 suggestion" })).toHaveAttribute("href", "/system");
    expect(screen.getByText("Also worth a look:")).toBeInTheDocument();
  });

  it("draws nothing when there are no leftover folders and no suggestions", () => {
    const { container } = render(<MemoryRouter><AlsoWorthALook attention={view(0, 0)} /></MemoryRouter>);
    expect(container).toBeEmptyDOMElement();
  });
});
