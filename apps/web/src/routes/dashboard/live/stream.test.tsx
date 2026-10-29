import { render } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { MemoryRouter } from "react-router-dom";
import { ActivityStream, boldTitle } from "./stream";

describe("boldTitle", () => {
  it("puts the title in bold where the sentence says it", () => {
    const { container } = render(<p>{boldTitle("Imported Sintel (2010) into your library", "Sintel (2010)")}</p>);
    expect(container.querySelector("b")?.textContent).toBe("Sintel (2010)");
    expect(container.textContent).toBe("Imported Sintel (2010) into your library");
  });
  it("leaves a sentence that does not say the title alone", () => {
    const { container } = render(<p>{boldTitle("Searched TV", "Sintel")}</p>);
    expect(container.querySelector("b")).toBeNull();
  });
});

describe("the Activity stream's links (#1114)", () => {
  it("opens All from the panel's All activity link and from a row about no title", () => {
    const rows = [{ id: "e1", category: "library.scan.completed", message: "Read the library", label: "Library", relatedEntityType: null, relatedEntityId: null, createdUtc: new Date().toISOString(), severity: null }] as never;
    const { container } = render(<MemoryRouter><ActivityStream rows={rows} titles={new Map() as never} /></MemoryRouter>);
    const hrefs = [...container.querySelectorAll("a")].map((a) => a.getAttribute("href"));
    expect(hrefs).toContain("/queue?view=history");
    expect(hrefs.some((href) => href?.includes("section="))).toBe(false);
  });
});
