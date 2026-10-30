import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../../lib/api", () => ({ fetchJson: vi.fn(async () => ({ types: [], sources: [], outcomes: [], reasons: [] })), fetchPage: vi.fn() }));

import { ActivityFilters } from "./activity-history";
import { emptyActivityFilters } from "../../lib/activity-history";

/**
 * The Activity filters' header layout (#1115, #1329): on the title line from `lg` up, they give way in turn as the line runs short
 * (full search, then a magnifier that opens while focused or holding text, then compact dropdowns), take a row of their own under
 * the header when even that does not fit, and are always in that row below `lg`. Room is made by fixing the widths the component
 * measures: the header's width, the title's width and the chips' own width (16px to the rem).
 */
const CHIPS = 300;

function lg(matches: boolean) {
  window.matchMedia = ((query: string) => ({
    matches, media: query, onchange: null, addEventListener: () => {}, removeEventListener: () => {}, addListener: () => {}, removeListener: () => {}, dispatchEvent: () => false
  })) as unknown as typeof window.matchMedia;
}

function header(room: number) {
  document.body.innerHTML = `<div id="topbar-main"><div><span id="topbar-title"></span></div><div id="topbar-page-tabs"></div><div id="topbar-page-actions"></div></div>`;
  document.getElementById("topbar-main")!.getBoundingClientRect = () => ({ width: room + 200 }) as DOMRect;
  document.getElementById("topbar-title")!.getBoundingClientRect = () => ({ width: 200 }) as DOMRect;
}

function show() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <ActivityFilters filters={emptyActivityFilters} setFilters={() => {}} chips={<div><span>chips</span></div>} />
      </MemoryRouter>
    </QueryClientProvider>,
    { container: document.body.appendChild(document.createElement("div")) }
  );
}

const group = () => screen.getByTestId("activity-filters");
const searchBox = () => screen.getByRole("searchbox", { name: "Find a title" });
const isMagnifier = () => searchBox().className.includes("cursor-pointer");
/** The dropdowns are the compact 28px size from the third stage on. */
const isCompact = () => !group().querySelector("[data-activity-scope] button")!.className.includes("h-9.5");

beforeEach(() => {
  vi.spyOn(HTMLElement.prototype, "scrollWidth", "get").mockReturnValue(CHIPS);
});
afterEach(() => {
  cleanup();
  document.body.innerHTML = "";
  vi.restoreAllMocks();
});

describe("where the Activity filters sit", () => {
  it("sit on the header's title line with a full search when there is room for everything", async () => {
    lg(true);
    header(900);
    await act(async () => { show(); });

    expect(group().getAttribute("data-placement")).toBe("header");
    expect(document.getElementById("topbar-page-tabs")!.contains(group())).toBe(true);
    expect(isMagnifier()).toBe(false);
  });

  it("turn the search into a magnifier first, which opens while focused and keeps its width while it holds text", async () => {
    lg(true);
    header(485 + 5);
    await act(async () => { show(); });

    expect(group().getAttribute("data-placement")).toBe("header");
    expect(isMagnifier()).toBe(true);
    expect(isCompact()).toBe(false);
    const box = searchBox().parentElement!;
    expect(box.className).toContain("w-9.5");
    fireEvent.focus(searchBox());
    expect(box.className).toContain("w-44");
    fireEvent.blur(searchBox());
    expect(box.className).toContain("w-9.5");
    fireEvent.change(searchBox(), { target: { value: "Dune" } });
    expect(box.className).toContain("w-44");
  });

  it("stay on the title line as the dropdowns and then the chips give way, down to the least they need", async () => {
    lg(true);
    header(485);
    await act(async () => { show(); });
    expect(group().getAttribute("data-placement")).toBe("header");
    expect(isMagnifier()).toBe(true);
    expect(isCompact()).toBe(true);
    cleanup();

    header(340);
    await act(async () => { show(); });
    expect(group().getAttribute("data-placement")).toBe("header");
    expect(isCompact()).toBe(true);
  });

  it("take a row of their own under the header, with a full search, when even that does not fit", async () => {
    lg(true);
    header(300);
    await act(async () => { show(); });

    expect(group().getAttribute("data-placement")).toBe("row");
    expect(document.getElementById("topbar-page-tabs")!.contains(group())).toBe(false);
    expect(isMagnifier()).toBe(false);
  });

  it("are always in the row below the large width, however wide the header is", async () => {
    lg(false);
    header(2000);
    await act(async () => { show(); });

    expect(group().getAttribute("data-placement")).toBe("row");
    expect(isMagnifier()).toBe(false);
  });
});
