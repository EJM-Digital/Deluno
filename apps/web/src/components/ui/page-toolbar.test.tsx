import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { PageToolbar, PageToolbarAction } from "./page-toolbar";

const TABS = [
  { to: "/settings", label: "general", end: true },
  { to: "/settings/profiles", label: "profiles", status: "complete" as const },
  { to: "/settings/custom", label: <span>Custom</span> }
];

function show(ui: React.ReactElement, at = "/settings/profiles/one") {
  return render(<MemoryRouter initialEntries={[at]}>{ui}</MemoryRouter>);
}

function wide(matches: boolean) {
  window.matchMedia = ((query: string) => ({
    matches, media: query, onchange: null, addEventListener: () => {}, removeEventListener: () => {}, addListener: () => {}, removeListener: () => {}, dispatchEvent: () => false
  })) as unknown as typeof window.matchMedia;
}

beforeEach(() => wide(false));
afterEach(() => {
  cleanup();
  document.body.innerHTML = "";
  vi.unstubAllGlobals();
});

describe("PageToolbar in its own row (no header, or below the large breakpoint)", () => {
  it("lists the tabs as links, marks the open area's tab and a completed one, and titles string labels", () => {
    show(<PageToolbar tabs={TABS} />);
    const nav = screen.getByRole("navigation", { name: "Sections" });
    expect(nav.querySelectorAll("a")).toHaveLength(3);
    const profiles = screen.getByRole("link", { name: /Profiles/ });
    expect(profiles.getAttribute("aria-label")).toBe("Profiles — complete");
    expect(profiles.getAttribute("data-status")).toBe("complete");
    expect(profiles.className).toContain("text-(--toolbar-accent)");
    expect(screen.getByRole("link", { name: "General" }).getAttribute("data-status")).toBe("pending");
    expect(screen.getByRole("link", { name: "General" }).className).not.toContain("text-(--toolbar-accent)");
    expect(screen.getByText("Custom")).toBeTruthy();
  });

  it("keeps an end tab open only on its exact address", () => {
    show(<PageToolbar tabs={TABS} />, "/settings/profiles");
    expect(screen.getByRole("link", { name: "General" }).className).not.toContain("text-(--toolbar-accent)");
    cleanup();
    show(<PageToolbar tabs={TABS} />, "/settings");
    expect(screen.getByRole("link", { name: "General" }).className).toContain("text-(--toolbar-accent)");
  });

  it("shows sections as a segmented control that reports the chosen one", async () => {
    const onValueChange = vi.fn();
    show(<PageToolbar sections={{ label: "Range", value: "week", onValueChange, options: [{ value: "day", label: "Day" }, { value: "week", label: "Week" }] }} />);
    expect(screen.getByRole("radio", { name: "Week" }).getAttribute("aria-checked")).toBe("true");
    await userEvent.click(screen.getByRole("radio", { name: "Day" }));
    expect(onValueChange).toHaveBeenCalledWith("day");
  });

  it("shows what a page puts on the left, and its buttons on the right", () => {
    show(<PageToolbar left={<span>Filter chips</span>} actions={<PageToolbarAction>New thing</PageToolbarAction>} />);
    expect(screen.getByText("Filter chips")).toBeTruthy();
    expect(screen.getByRole("button", { name: "New thing" })).toBeTruthy();
  });

  it("draws an empty row, hidden, when there is nothing to put in it", () => {
    const { container } = show(<PageToolbar />);
    expect(container.firstElementChild?.className).toContain("hidden");
  });

  it("ignores sections when tabs are given, and takes an extra class", () => {
    const { container } = show(<PageToolbar className="extra" tabs={TABS} sections={{ label: "Range", value: "day", onValueChange: () => {}, options: [{ value: "day", label: "Day" }] }} />);
    expect(screen.queryByRole("radio")).toBeNull();
    expect(container.firstElementChild?.className).toContain("extra");
  });
});

describe("PageToolbar with the header's slots (large screens)", () => {
  function addHeader() {
    document.body.insertAdjacentHTML("afterbegin", '<div id="topbar-main"><span id="topbar-title"></span><div id="topbar-page-tabs"></div><div id="topbar-page-actions"></div></div>');
  }

  it("puts the tabs and the buttons on the title line and leaves no row of its own", () => {
    wide(true);
    addHeader();
    const { container } = show(<PageToolbar tabs={TABS} actions={<PageToolbarAction>New thing</PageToolbarAction>} />);
    expect(document.getElementById("topbar-page-tabs")?.querySelector("nav, [role=tablist]")).not.toBeNull();
    expect(document.getElementById("topbar-page-actions")?.textContent).toContain("New thing");
    expect(container.querySelector("nav")).toBeNull();
    expect(container.firstElementChild?.className).toContain("hidden");
  });

  it("puts sections on the title line as tabs", () => {
    wide(true);
    addHeader();
    show(<PageToolbar sections={{ label: "Range", value: "day", onValueChange: () => {}, options: [{ value: "day", label: "Day" }, { value: "week", label: "Week" }] }} />);
    expect(document.getElementById("topbar-page-tabs")?.querySelector("[role=tablist]")).not.toBeNull();
  });

  it("keeps the buttons in its own row when they do not fit beside the title", () => {
    wide(true);
    addHeader();
    class FakeObserver { constructor(public cb: () => void) {} observe() {} disconnect() {} unobserve() {} }
    vi.stubGlobal("ResizeObserver", FakeObserver);
    const main = document.getElementById("topbar-main")!;
    main.getBoundingClientRect = () => ({ width: 300 }) as DOMRect;
    document.getElementById("topbar-title")!.getBoundingClientRect = () => ({ width: 250 }) as DOMRect;
    const { container } = show(<PageToolbar tabs={TABS} actions={<PageToolbarAction>New thing</PageToolbarAction>} />);
    expect(container.querySelector("button")?.textContent).toContain("New thing");
    expect(document.getElementById("topbar-page-actions")?.textContent).toBe("");
  });

  it("keeps the buttons on the title line when they fit", () => {
    wide(true);
    addHeader();
    class FakeObserver { constructor(public cb: () => void) {} observe() {} disconnect() {} unobserve() {} }
    vi.stubGlobal("ResizeObserver", FakeObserver);
    document.getElementById("topbar-main")!.getBoundingClientRect = () => ({ width: 1200 }) as DOMRect;
    document.getElementById("topbar-title")!.getBoundingClientRect = () => ({ width: 100 }) as DOMRect;
    show(<PageToolbar actions={<PageToolbarAction>New thing</PageToolbarAction>} />);
    expect(document.getElementById("topbar-page-actions")?.textContent).toContain("New thing");
  });
});

describe("PageToolbarAction", () => {
  it("is a button with a plus and the accent fill by default, and plain styling for another variant", () => {
    show(<><PageToolbarAction>Add one</PageToolbarAction><PageToolbarAction variant="outline" style={{ color: "red" }}>Add two</PageToolbarAction></>);
    const first = screen.getByRole("button", { name: "Add one" });
    expect(first.getAttribute("type")).toBe("button");
    expect(first.querySelector("svg")).not.toBeNull();
    expect(first.style.backgroundImage).toContain("--toolbar-accent");
    const second = screen.getByRole("button", { name: "Add two" });
    expect(second.style.backgroundImage).toBe("");
    expect(second.style.color).toBe("red");
  });
});
