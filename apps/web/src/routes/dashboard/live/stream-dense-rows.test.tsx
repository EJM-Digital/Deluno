import { cleanup, render } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it } from "vitest";
import { ActivityStream } from "./stream";

afterEach(cleanup);

const at = new Date().toISOString();
const event = (id: string) => ({ id, category: "import.completed", message: "Read the library", label: "Library", relatedEntityType: null, relatedEntityId: null, createdUtc: at, severity: null }) as never;

describe("Live Activity stream: dense rows", () => {
  it("puts each row on one line when the stream is dense, with its time after it, and keeps the two-line form otherwise", () => {
    const { container, unmount } = render(<MemoryRouter><ActivityStream rows={[event("e1")]} titles={new Map() as never} dense /></MemoryRouter>);
    const dense = container.querySelector("li.lact-ev")!;
    expect(dense).toHaveClass("lact-dense");
    expect(dense.querySelector(".lact-k")).toBeNull();
    expect(dense.querySelector(".when")!.textContent).toMatch(/ago|now|just/);
    unmount();

    const { container: normal } = render(<MemoryRouter><ActivityStream rows={[event("e2")]} titles={new Map() as never} /></MemoryRouter>);
    const full = normal.querySelector("li.lact-ev")!;
    expect(full).not.toHaveClass("lact-dense");
    expect(full.querySelector(".lact-k")).not.toBeNull();
  });

  it("says how many times a repeated line happened, in front of its time, when the stream is dense", () => {
    const { container } = render(<MemoryRouter><ActivityStream rows={[event("a"), event("b")]} titles={new Map() as never} dense /></MemoryRouter>);
    const when = container.querySelector("li.lact-ev .when")!;
    expect(when.textContent!.startsWith("2 times · ")).toBe(true);
  });
});
