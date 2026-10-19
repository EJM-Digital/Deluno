import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { useState } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { FilterCondition, FilterFieldSpec } from "../../lib/library-controls";
import { LibraryFilterPanel } from "./library-filter-panel";

const mocks = vi.hoisted(() => ({ fetchJson: vi.fn() }));
vi.mock("../../lib/api", async (importOriginal) => ({ ...(await importOriginal<typeof import("../../lib/api")>()), fetchJson: mocks.fetchJson }));

/**
 * The filter editor (coverage register, area library): the field picker with its search, and a row for each question with the
 * operator menu and the value control that kind of field needs. The fields are the server's declaration, so what is asserted is
 * that every kind of value is drawn the way it is entered, and that an unfinished row says it narrows nothing yet.
 */
const FIELDS: FilterFieldSpec[] = [
  { id: "quality", label: "Quality", hint: "The tier of the file you hold.", group: "file", valueKind: "quality", operators: ["in", "notin"], options: null },
  { id: "genre", label: "Genre", hint: "What the title is.", group: "title", valueKind: "genre", operators: ["all", "in", "notin"], options: null },
  { id: "tag", label: "Tag", hint: "Your own labels.", group: "title", valueKind: "tag", operators: ["in", "all", "notin"], options: null },
  { id: "certification", label: "Certification", hint: "Rated for.", group: "title", valueKind: "enum", operators: ["in", "notin"], options: ["PG", "R"] },
  { id: "year", label: "Year", hint: "When it came out.", group: "title", valueKind: "year", operators: ["min", "max", "is"], options: null },
  { id: "size", label: "File size", hint: "How big the file is.", group: "file", valueKind: "gigabytes", operators: ["min", "max"], options: null },
  { id: "runtime", label: "Runtime", hint: "How long it runs.", group: "title", valueKind: "minutes", operators: ["min", "max"], options: null },
  { id: "codec", label: "Video codec", hint: "How the video is stored.", group: "file", valueKind: "text", operators: ["is", "has"], options: null },
  { id: "lastsearched", label: "Last searched", hint: "When Deluno last looked.", group: "time", valueKind: "date", operators: ["within", "before", "unset"], options: null },
  { id: "conformant", label: "Conforms", hint: "Whether the file fits its profile.", group: "decision", valueKind: "boolean", operators: ["is"], options: null }
];

function Harness({ initial = [], fields = FIELDS, onSeen }: { initial?: FilterCondition[]; fields?: FilterFieldSpec[]; onSeen?: (next: FilterCondition[]) => void }) {
  const [conditions, setConditions] = useState<FilterCondition[]>(initial);
  return <LibraryFilterPanel
    variant="movies" fields={fields} conditions={conditions}
    onChange={(next) => { setConditions(next); onSeen?.(next); }} onClear={() => setConditions([])}
    monitoring="any" onMonitoringChange={() => undefined} monitoredCount={7} unmonitoredCount={2}
  />;
}

beforeEach(() => {
  mocks.fetchJson.mockReset();
  mocks.fetchJson.mockImplementation(async (url: string) => {
    if (url === "/api/quality-model") return { tiers: [{ name: "HDTV 720p", rank: 2 }, { name: "Remux 2160p", rank: 9 }, { name: "WEB 1080p", rank: 5 }] };
    if (url === "/api/movies/genres") return ["Drama", "Sci-Fi"];
    if (url === "/api/tags") return [{ name: "kids" }, { name: "4k" }];
    return [];
  });
});

describe("Add a field", () => {
  it("lists every declared field under its group, narrows as you type, and says so when nothing is called that", async () => {
    render(<Harness />);

    expect(screen.getByText(/10 to choose from./)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Add a field" }));
    for (const label of ["The title", "The file you hold", "Time", "What Deluno decided"]) expect(screen.getByText(label)).toBeInTheDocument();
    for (const field of FIELDS) expect(screen.getByRole("button", { name: new RegExp(`^${field.label}`) })).toBeInTheDocument();

    const search = screen.getByPlaceholderText(/Search fields/);
    fireEvent.change(search, { target: { value: "codec" } });
    expect(screen.getByRole("button", { name: /^Video codec/ })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^Genre/ })).toBeNull();
    fireEvent.change(search, { target: { value: "fits its profile" } });
    expect(screen.getByRole("button", { name: /^Conforms/ })).toBeInTheDocument();
    fireEvent.change(search, { target: { value: "zzzz" } });
    expect(screen.getByText(/Nothing here is called that\./)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^Genre/ })).toBeNull();
  });

  it("Stop adding a field closes the picker without adding anything", () => {
    const seen = vi.fn();
    render(<Harness onSeen={seen} />);

    fireEvent.click(screen.getByRole("button", { name: "Add a field" }));
    fireEvent.click(screen.getByRole("button", { name: "Stop adding a field" }));

    expect(screen.getByRole("button", { name: "Add a field" })).toBeInTheDocument();
    expect(screen.queryByPlaceholderText(/Search fields/)).toBeNull();
    expect(seen).not.toHaveBeenCalled();
  });

  it("choosing a field adds its row with the field's first operator, waiting for a value, and closes the picker", () => {
    const seen = vi.fn();
    render(<Harness onSeen={seen} />);

    fireEvent.click(screen.getByRole("button", { name: "Add a field" }));
    fireEvent.click(screen.getByRole("button", { name: /^File size/ }));

    expect(seen).toHaveBeenLastCalledWith([{ field: "size", operator: "min", values: [] }]);
    expect(screen.queryByPlaceholderText(/Search fields/)).toBeNull();
    expect(screen.getByText("Nothing is narrowed by this yet — file size needs a value.")).toBeInTheDocument();
  });

  it("a yes-or-no field starts at Yes, because a switch has to be one of them", () => {
    const seen = vi.fn();
    render(<Harness onSeen={seen} />);

    fireEvent.click(screen.getByRole("button", { name: "Add a field" }));
    fireEvent.click(screen.getByRole("button", { name: /^Conforms/ }));

    expect(seen).toHaveBeenLastCalledWith([{ field: "conformant", operator: "is", values: ["true"] }]);
    expect(screen.getByRole("combobox", { name: "Conforms" })).toHaveTextContent("Yes");
  });
});

describe("a condition row", () => {
  it("draws choice chips for quality (highest tier first), genre and tag from the data, and toggling a chip adds and drops its value", async () => {
    const seen = vi.fn();
    render(<Harness initial={[{ field: "quality", operator: "in", values: [] }, { field: "genre", operator: "all", values: [] }, { field: "tag", operator: "in", values: [] }]} onSeen={seen} />);

    await screen.findByRole("button", { name: "Remux 2160p" });
    const chips = screen.getAllByRole("button", { pressed: false }).map((button) => button.textContent);
    expect(chips.slice(0, 3)).toEqual(["Remux 2160p", "WEB 1080p", "HDTV 720p"]);
    for (const name of ["Drama", "Sci-Fi", "4k", "kids"]) expect(screen.getByRole("button", { name })).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "WEB 1080p" }));
    expect(seen).toHaveBeenLastCalledWith(expect.arrayContaining([{ field: "quality", operator: "in", values: ["WEB 1080p"] }]));
    expect(screen.getByRole("button", { name: "WEB 1080p" })).toHaveAttribute("aria-pressed", "true");
    fireEvent.click(screen.getByRole("button", { name: "WEB 1080p" }));
    expect(seen).toHaveBeenLastCalledWith(expect.arrayContaining([{ field: "quality", operator: "in", values: [] }]));
  });

  it("an enum field draws its own declared choices", async () => {
    render(<Harness initial={[{ field: "certification", operator: "in", values: ["PG"] }]} />);

    expect(await screen.findByRole("button", { name: "PG" })).toHaveAttribute("aria-pressed", "true");
    expect(screen.getByRole("button", { name: "R" })).toHaveAttribute("aria-pressed", "false");
    expect(screen.getByText("Certification is any of PG")).toBeInTheDocument();
  });

  it("a choice list with nothing to choose from says Nothing in this library has one yet", async () => {
    mocks.fetchJson.mockImplementation(async (url: string) => (url === "/api/quality-model" ? { tiers: [] } : []));
    render(<Harness initial={[{ field: "genre", operator: "all", values: [] }]} />);

    expect(await screen.findByText("Nothing in this library has one yet. They arrive with metadata.")).toBeInTheDocument();
  });

  it("a number field takes a number with its unit, a text field takes text, and what is typed is what travels", () => {
    const seen = vi.fn();
    render(<Harness initial={[{ field: "size", operator: "min", values: [] }, { field: "runtime", operator: "max", values: [] }, { field: "codec", operator: "is", values: [] }]} onSeen={seen} />);

    expect(screen.getByText("GB")).toBeInTheDocument();
    expect(screen.getByText("min")).toBeInTheDocument();
    fireEvent.change(screen.getByRole("spinbutton", { name: "File size" }), { target: { value: "40" } });
    expect(seen).toHaveBeenLastCalledWith(expect.arrayContaining([{ field: "size", operator: "min", values: ["40"] }]));
    expect(screen.getByText("File size at least 40 GB")).toBeInTheDocument();
    fireEvent.change(screen.getByRole("textbox", { name: "Video codec" }), { target: { value: "hevc" } });
    expect(seen).toHaveBeenLastCalledWith(expect.arrayContaining([{ field: "codec", operator: "is", values: ["hevc"] }]));
    expect(screen.getByText("Video codec is hevc")).toBeInTheDocument();
  });

  it("a date field with a relative operator counts days, and one with an absolute operator takes a date", () => {
    const { rerender } = render(<Harness initial={[{ field: "lastsearched", operator: "within", values: ["30"] }]} />);

    expect(screen.getByRole("spinbutton", { name: "Last searched" })).toHaveValue(30);
    expect(screen.getByText("days")).toBeInTheDocument();
    expect(screen.getByText("Last searched in the last 30 days")).toBeInTheDocument();

    rerender(<Harness key="absolute" initial={[{ field: "lastsearched", operator: "before", values: ["2026-01-01"] }]} />);
    expect((screen.getByLabelText("Last searched") as HTMLInputElement).type).toBe("date");
  });

  it("an operator that takes no value draws no value box and counts as finished", () => {
    render(<Harness initial={[{ field: "lastsearched", operator: "unset", values: [] }]} />);

    expect(screen.queryByLabelText("Last searched")).toBeNull();
    expect(screen.getByText("Last searched has no value")).toBeInTheDocument();
  });

  it("the operator menu lists the field's own comparisons, and choosing another clears the value that no longer fits", () => {
    const seen = vi.fn();
    render(<Harness initial={[{ field: "year", operator: "min", values: ["2000"] }]} onSeen={seen} />);

    fireEvent.click(screen.getByRole("combobox", { name: "How Year is compared" }));
    const options = within(screen.getByRole("listbox", { name: "How Year is compared" })).getAllByRole("option").map((option) => option.textContent);
    expect(options).toEqual(["at least", "at most", "is"]);
    fireEvent.click(screen.getByRole("option", { name: /^at most/ }));

    expect(seen).toHaveBeenLastCalledWith([{ field: "year", operator: "max", values: [] }]);
  });

  it("a row for a field this library no longer records says so and can be removed", () => {
    const seen = vi.fn();
    render(<Harness initial={[{ field: "retired", operator: "is", values: ["x"] }]} onSeen={seen} />);

    expect(screen.getByText("This filter asks for “retired”, which this library no longer records.")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Remove this filter" }));
    expect(seen).toHaveBeenLastCalledWith([]);
  });

  it("Remove drops one row and Clear drops them all, with the count said", async () => {
    render(<Harness initial={[{ field: "year", operator: "min", values: ["2000"] }, { field: "size", operator: "max", values: ["10"] }]} />);

    expect(screen.getByRole("button", { name: "Clear 2 filters" })).toBeInTheDocument();
    fireEvent.click(screen.getAllByRole("button", { name: "Remove this filter" })[0]);
    expect(screen.queryByText("Year at least 2000")).toBeNull();
    expect(screen.getByText("File size at most 10 GB")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Clear 1 filter" }));
    await waitFor(() => expect(screen.getByText(/Nothing is being narrowed\./)).toBeInTheDocument());
  });
});
