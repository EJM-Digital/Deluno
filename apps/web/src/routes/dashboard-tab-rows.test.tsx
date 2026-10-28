import { describe, expect, it } from "vitest";

const { tabFromSearch } = await import("./dashboard-page");

describe("Dashboard tab links", () => {
  it("opens System for the old ?tab=setup link, whose content moved there", () => {
    expect(tabFromSearch("?tab=setup")).toBe("system");
    expect(tabFromSearch(new URLSearchParams("tab=setup"))).toBe("system");
  });
});
