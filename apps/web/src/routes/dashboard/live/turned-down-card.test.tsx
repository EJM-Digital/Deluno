import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const size = vi.hoisted(() => ({ height: 0 }));
vi.mock("./hooks", async (importOriginal) => ({
  ...(await importOriginal<typeof import("./hooks")>()),
  useElementSize: () => [{ current: null }, { width: 300, height: size.height }]
}));

const { TurnedDownCard, moreLine } = await import("./turned-down-card");

const view = (counts: number[], total = counts.reduce((a, b) => a + b, 0)) => ({
  total,
  reasons: counts.map((count, index) => ({ group: (["language", "tooBig", "fake", "other"] as const)[index], label: index === 3 ? "" : `Reason ${index}`, count })),
  latest: []
});

const show = (v: ReturnType<typeof view>, lit: string[] = []) => render(<MemoryRouter><TurnedDownCard view={v} lit={new Set(lit)} /></MemoryRouter>);

beforeEach(() => {
  size.height = 0;
  vi.spyOn(HTMLElement.prototype, "offsetHeight", "get").mockReturnValue(30);
});
afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("the Turned down today card", () => {
  it("counts one release in the singular and links to Activity filtered to today's turned-down releases", () => {
    show(view([1, 0]));
    expect(screen.getByText("1 release")).toBeTruthy();
    expect(screen.getByRole("link", { name: /In Activity/ }).getAttribute("href")).toBe("/queue?view=history&type=turned-down&since=today");
  });

  it("counts many with a thousands separator, draws a bar per reason as a link, and marks an empty one and a lit one", () => {
    const { container } = show(view([1500, 0, 3, 4]), ["tooBig"]);
    expect(screen.getByText("1,507 releases")).toBeTruthy();
    const empty = screen.getByRole("link", { name: "Reason 1: 0 today, show in Activity" });
    expect(empty.className).toContain("zero");
    expect(empty.hasAttribute("data-hit")).toBe(true);
    expect(screen.getByRole("link", { name: "Reason 0: 1,500 today, show in Activity" }).hasAttribute("data-hit")).toBe(false);
    expect(screen.getByRole("link", { name: "Reason 0: 1,500 today, show in Activity" }).getAttribute("href")).toContain("reason=language");
    // A reason without a label of its own reads in the plain words for its group.
    expect(container.querySelector('[data-reason="other"] span')?.textContent).toBeTruthy();
    expect(container.querySelector(".ltd-more")).toBeNull();
  });

  it("holds only the whole rows that fit, and says what the rest hold", () => {
    size.height = 14 + 8 + 24 + 38;
    const { container } = show(view([5, 4, 3, 2]));
    expect(container.querySelectorAll(".ltd-br")).toHaveLength(1);
    expect(container.querySelector(".ltd-more")?.textContent).toBe("and 3 more reasons · 9 releases");
  });
});

describe("the line under the bars", () => {
  it("says none today when the hidden reasons hold nothing, and counts a single reason and release", () => {
    expect(moreLine(2, 0)).toBe("and 2 more reasons, none today");
    expect(moreLine(1, 1)).toBe("and 1 more reason · 1 release");
  });
});
