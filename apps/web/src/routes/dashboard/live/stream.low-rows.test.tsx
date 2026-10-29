import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

const layout = vi.hoisted(() => ({ fits: Number.MAX_SAFE_INTEGER }));
vi.mock("./fit-rows", async (importOriginal) => {
  const { useRef } = await import("react");
  return {
    ...(await importOriginal<typeof import("./fit-rows")>()),
    useFittingRows: () => [useRef<HTMLDivElement | null>(null), layout.fits]
  };
});

const { ActivityStream } = await import("./stream");

afterEach(() => {
  cleanup();
  layout.fits = Number.MAX_SAFE_INTEGER;
});

const at = new Date().toISOString();
const rows = (count: number) => Array.from({ length: count }, (_, index) => ({
  id: `e${index}`, category: "import.completed", message: `Imported film number ${index}`, label: "Library", relatedEntityType: null, relatedEntityId: null, createdUtc: at, severity: null
})) as never;

function draw(count: number) {
  return render(<MemoryRouter><ActivityStream rows={rows(count)} titles={new Map() as never} /></MemoryRouter>);
}

describe("Live Activity stream (low rows)", () => {
  it("draws up to 20 entries and puts the rest in one 'N more' link to the activity log beside 'All activity'", () => {
    const { container } = draw(30);

    expect(container.querySelectorAll("li.lact-ev")).toHaveLength(20);
    const more = screen.getByRole("link", { name: "10 more in the activity log" });
    expect(more).toHaveAttribute("href", "/activity");
    expect(more).toHaveTextContent("10 more");
    expect(screen.getByRole("link", { name: /All activity/ })).toHaveAttribute("href", "/activity");
  });

  it("counts the entries that do not fit the card's height as more, and says nothing more when they all fit", () => {
    layout.fits = 7;
    const { unmount } = draw(30);
    expect(screen.getByRole("link", { name: "23 more in the activity log" })).toBeInTheDocument();
    unmount();

    layout.fits = Number.MAX_SAFE_INTEGER;
    draw(5);
    expect(screen.queryByRole("link", { name: /more in the activity log/ })).toBeNull();
  });
});
