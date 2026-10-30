import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { FilterCondition, FilterFieldSpec } from "../../lib/library-controls";
import { LibraryFilterPanel } from "./library-filter-panel";

const mocks = vi.hoisted(() => ({ fetchJson: vi.fn() }));
vi.mock("../../lib/api", async (importOriginal) => ({ ...(await importOriginal<typeof import("../../lib/api")>()), fetchJson: mocks.fetchJson }));

/** The filter editor on a TV shelf, with its lookups failing, the monitoring menu, and the odd value controls. */
const FIELDS: FilterFieldSpec[] = [
  { id: "rating", label: "Rating", hint: "Scored.", group: "title", valueKind: "rating", operators: ["min"], options: null },
  { id: "ratio", label: "Ratio", hint: "Shared back.", group: "file", valueKind: "decimal", operators: ["min"], options: null },
  { id: "codec", label: "Video codec", hint: "How stored.", group: "file", valueKind: "text", operators: ["is"], options: null },
  { id: "conformant", label: "Conforms", hint: "Fits.", group: "decision", valueKind: "boolean", operators: ["is"], options: null },
  { id: "year", label: "Year", hint: "Came out.", group: "title", valueKind: "year", operators: ["min"], options: null }
];

function panel(over: Partial<Parameters<typeof LibraryFilterPanel>[0]> = {}) {
  return (
    <LibraryFilterPanel
      variant="shows" fields={FIELDS} conditions={[]} onChange={vi.fn()} onClear={vi.fn()}
      monitoring="any" onMonitoringChange={vi.fn()} monitoredCount={4} unmonitoredCount={1} {...over}
    />
  );
}

beforeEach(() => mocks.fetchJson.mockReset().mockResolvedValue([]));

describe("the filter editor, edge turns", () => {
  it("asks for a show's genres on a TV shelf", async () => {
    render(panel());
    await waitFor(() => expect(mocks.fetchJson).toHaveBeenCalledWith("/api/series/genres"));
    expect(mocks.fetchJson).not.toHaveBeenCalledWith("/api/movies/genres");
  });

  it("copes when the quality ladder, the genres and the tags cannot be fetched", async () => {
    const calls: string[] = [];
    // A lookup that failed, as the panel sees it: its own .catch gets the failure and answers with an empty result.
    mocks.fetchJson.mockImplementation((url: string) => { calls.push(url); return { catch: (handle: () => unknown) => Promise.resolve(handle()) }; });
    render(panel());
    await new Promise((resolve) => setTimeout(resolve, 100));
    expect(calls).toHaveLength(3);
  });

  it("tells the page which monitoring choice was made", async () => {
    const onMonitoringChange = vi.fn();
    render(panel({ onMonitoringChange }));
    fireEvent.click(screen.getByRole("combobox", { name: "Monitoring" }));
    fireEvent.click(screen.getByRole("option", { name: "Unmonitored (1)" }));
    expect(onMonitoringChange).toHaveBeenCalledWith("unmonitored");
  });

  it("clearing a number box leaves the row waiting for a value; ratings and decimals step by a tenth", () => {
    const onChange = vi.fn();
    const conditions: FilterCondition[] = [
      { field: "rating", operator: "min", values: ["7"] },
      { field: "ratio", operator: "min", values: ["1.5"] },
      { field: "year", operator: "min", values: ["2000"] }
    ];
    render(panel({ conditions, onChange }));
    expect(screen.getByRole("spinbutton", { name: "Rating" }).getAttribute("step")).toBe("0.1");
    expect(screen.getByRole("spinbutton", { name: "Ratio" }).getAttribute("step")).toBe("0.1");
    expect(screen.getByRole("spinbutton", { name: "Year" }).hasAttribute("step")).toBe(false);

    fireEvent.change(screen.getByRole("spinbutton", { name: "Rating" }), { target: { value: "" } });
    expect(onChange).toHaveBeenLastCalledWith(expect.arrayContaining([{ field: "rating", operator: "min", values: [] }]));
  });

  it("shows a yes-or-no row with no value yet as Yes", () => {
    render(panel({ conditions: [{ field: "conformant", operator: "is", values: [] }] }));
    expect(screen.getByRole("combobox", { name: "Conforms" }).textContent).toContain("Yes");
  });
});
