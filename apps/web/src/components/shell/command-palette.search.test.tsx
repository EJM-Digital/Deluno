import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, useLocation } from "react-router-dom";
import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import { CommandPalette } from "./command-palette";

beforeAll(() => {
  globalThis.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
  Element.prototype.scrollIntoView ??= () => {};
});

function Where() {
  const location = useLocation();
  return <p data-testid="where">{location.pathname + location.search}</p>;
}

const onOpenChange = vi.fn();

function renderPalette() {
  return render(
    <MemoryRouter initialEntries={["/somewhere"]}>
      <Where />
      <CommandPalette open onOpenChange={onOpenChange} />
    </MemoryRouter>
  );
}

describe("the search & navigate window searches the indexers (#1427)", () => {
  afterEach(cleanup);

  it("offers no indexer search until something is typed", async () => {
    renderPalette();
    await screen.findByPlaceholderText("Jump to area, run action…");
    expect(screen.queryByText(/Search every indexer for/)).toBeNull();
  });

  it("offers to search every indexer for the typed words, and opens the results for them", async () => {
    renderPalette();
    const box = await screen.findByPlaceholderText("Jump to area, run action…");
    fireEvent.change(box, { target: { value: "  Blade Runner & more " } });

    const entry = await screen.findByText("Search every indexer for “Blade Runner & more”");
    fireEvent.click(entry);

    await waitFor(() => expect(screen.getByTestId("where").textContent).toBe("/search?q=Blade%20Runner%20%26%20more"));
    expect(onOpenChange).toHaveBeenCalledWith(false);
    expect(onOpenChange).not.toHaveBeenCalledWith(true);
  });

  it("offers nothing for spaces alone, and the search entry stays even when no other entry fits the words", async () => {
    renderPalette();
    const box = await screen.findByPlaceholderText("Jump to area, run action…");
    fireEvent.change(box, { target: { value: "   " } });
    expect(screen.queryByText(/Search every indexer for/)).toBeNull();

    fireEvent.change(box, { target: { value: "zzqqxx nothing is named this" } });
    expect(await screen.findByText(/Search every indexer for/)).toBeInTheDocument();
  });
});
