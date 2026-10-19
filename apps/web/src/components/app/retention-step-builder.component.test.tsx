import { fireEvent, render, screen, within } from "@testing-library/react";
import { useState } from "react";
import { describe, expect, it, vi } from "vitest";
import { RetentionStepBuilder, type RetentionField, type RetentionStep } from "./retention-step-builder";

/**
 * The step builder of a retention rule (#494): the count it starts from, the three buttons that add a step or a block, the trash
 * button that removes one, the field list grouped the way a person thinks of it, and the number left after each step.
 */

const field = (id: string, label: string, group: string, valueKind: string, operators: string[], extra: Partial<RetentionField> = {}): RetentionField => ({
  id, label, hint: "", group, valueKind, operators, options: null, source: "deluno", unsupportedOn: [], ...extra
});

const fields: RetentionField[] = [
  field("server.watchedBy", "Watched by", "watching", "users", ["nobody", "anyone"], { source: "server", unsupportedOn: ["plex"] }),
  field("server.lastViewed", "Last viewed", "server", "date", ["beyond", "within"], { source: "server" }),
  field("part.finale", "Is a season finale", "part", "boolean", ["is"]),
  field("requests.requestedBy", "Requested by", "requests", "users", ["anyone"], { source: "server" }),
  field("storage.size", "Size on disk", "storage", "gigabytes", ["above", "below"], { source: "disk" }),
  field("title.genre", "Genre", "title", "text", ["is", "isnot"], { options: ["Drama", "Comedy"] }),
  field("added", "Added", "time", "date", ["beyond", "within"])
];

function Harness({ initial = [], counts = null, considered = 120, serverKinds = [] }: { initial?: RetentionStep[]; counts?: Parameters<typeof RetentionStepBuilder>[0]["counts"]; considered?: number | null; serverKinds?: string[] }) {
  const [steps, setSteps] = useState<RetentionStep[]>(initial);
  return (
    <>
      <RetentionStepBuilder steps={steps} onChange={setSteps} fields={fields} serverKinds={serverKinds} counts={counts} considered={considered} noun="film" />
      <output data-testid="steps">{JSON.stringify(steps)}</output>
    </>
  );
}

const stepsNow = () => JSON.parse(screen.getByTestId("steps").textContent ?? "[]") as RetentionStep[];
const keep = (condition: string): RetentionStep => ({ type: "condition", op: "keep", condition });

describe("the rule's step builder", () => {
  it("starts from every title Deluno holds, with the count it considered, and says … until it knows", () => {
    const { rerender } = render(<Harness considered={120} />);
    expect(screen.getByText("Start with every film Deluno holds")).toBeVisible();
    expect(screen.getByText("120")).toBeVisible();
    rerender(<Harness considered={null} />);
    expect(screen.getByText("…")).toBeVisible();
  });

  it("offers Keep only, Also include (off while there are no steps) and Block, and each adds its own kind of step", () => {
    render(<Harness />);
    const alsoInclude = screen.getByRole("button", { name: "Also include…" });
    expect(alsoInclude).toBeDisabled();

    fireEvent.click(screen.getByRole("button", { name: "Keep only…" }));
    expect(stepsNow()).toHaveLength(1);
    expect(stepsNow()[0]).toMatchObject({ type: "condition", op: "keep" });
    expect(alsoInclude).toBeEnabled();

    fireEvent.click(alsoInclude);
    expect(stepsNow()[1]).toMatchObject({ type: "condition", op: "include" });

    fireEvent.click(screen.getByRole("button", { name: "Block" }));
    expect(stepsNow()[2]).toMatchObject({ type: "block", op: "include", name: "" });
    expect(stepsNow()[2].steps).toHaveLength(1);
  });

  it("a block as the very first step keeps rather than includes, and takes a name", () => {
    render(<Harness />);
    fireEvent.click(screen.getByRole("button", { name: "Block" }));
    expect(stepsNow()[0]).toMatchObject({ type: "block", op: "keep" });

    fireEvent.change(screen.getByLabelText("Step 1 block name"), { target: { value: "Low rated" } });

    expect(stepsNow()[0].name).toBe("Low rated");
  });

  it("the first step is fixed as Keep only and a later one chooses between Keep only and Also include", () => {
    render(<Harness initial={[keep("added:beyond:30"), { type: "condition", op: "include", condition: "added:within:3" }]} />);
    expect(screen.queryByLabelText("Step 1 joins as")).not.toBeInTheDocument();
    const joins = screen.getByLabelText("Step 2 joins as") as HTMLSelectElement;
    expect(Array.from(joins.options).map((option) => option.textContent)).toEqual(["Keep only", "Also include"]);

    fireEvent.change(joins, { target: { value: "keep" } });

    expect(stepsNow()[1].op).toBe("keep");
  });

  it("the trash button removes just that step", () => {
    render(<Harness initial={[keep("added:beyond:30"), keep("added:within:3")]} />);

    fireEvent.click(screen.getByRole("button", { name: "Remove step 1" }));

    expect(stepsNow()).toEqual([keep("added:within:3")]);
  });

  it("removes a block with its own trash button", () => {
    render(<Harness initial={[{ type: "block", op: "keep", name: "Old", steps: [keep("added:beyond:30")] }]} />);

    fireEvent.click(screen.getByRole("button", { name: "Remove step 1" }));

    expect(stepsNow()).toEqual([]);
  });

  it("a block adds steps of its own inside it", () => {
    render(<Harness initial={[{ type: "block", op: "keep", name: "Old", steps: [keep("added:beyond:30")] }]} />);
    const block = screen.getByLabelText("Step 1 block name").closest("div[class*='border']") as HTMLElement;

    fireEvent.click(within(block).getAllByRole("button", { name: "Also include…" })[0]);

    expect(stepsNow()[0].steps).toHaveLength(2);
    expect(stepsNow()[0].steps![1]).toMatchObject({ op: "include" });
  });

  it("groups the fields the way a person thinks of them and says which the connected servers cannot answer", () => {
    render(<Harness initial={[keep("server.lastViewed:beyond:30")]} serverKinds={["plex"]} />);
    const select = screen.getByLabelText("Step 1 field") as HTMLSelectElement;

    const groups = Array.from(select.querySelectorAll("optgroup")).map((group) => group.label);
    expect(groups).toEqual(["Watching", "From the media servers", "This season or episode", "Requests (Seerr)", "Storage", "Title", "Dates"]);
    const watchedBy = within(select).getByRole("option", { name: "Watched by (not on these servers)" });
    expect(watchedBy).toBeDisabled();
    expect(within(select).getByRole("option", { name: "Genre" })).toBeEnabled();
  });

  it("choosing another field starts from that field's first test and clears the value", () => {
    render(<Harness initial={[keep("added:beyond:30")]} />);

    fireEvent.change(screen.getByLabelText("Step 1 field"), { target: { value: "storage.size" } });

    expect(stepsNow()[0].condition).toBe("storage.size:above:");
    expect((screen.getByLabelText("Step 1 test") as HTMLSelectElement).value).toBe("above");
    expect(screen.getByLabelText("Step 1 value")).toHaveAttribute("type", "number");
    expect(screen.getByLabelText("Step 1 value")).toHaveAttribute("placeholder", "GB");
  });

  it("asks for days on a date test, a choice on a field with options, and nothing on a test that needs no value", () => {
    render(<Harness initial={[keep("added:beyond:30"), keep("title.genre:is:Drama"), keep("server.watchedBy:nobody:")]} />);

    expect(screen.getByLabelText("Step 1 value")).toHaveAttribute("placeholder", "days");
    expect(screen.getByLabelText("Step 1 value")).toHaveValue(30);
    const genre = screen.getByLabelText("Step 2 value") as HTMLSelectElement;
    expect(Array.from(genre.options).map((option) => option.textContent)).toContain("Comedy");
    expect(screen.queryByLabelText("Step 3 value")).not.toBeInTheDocument();
  });

  it("shows how many are left after each step, and how many a later Also include added", () => {
    render(
      <Harness
        initial={[keep("added:beyond:30"), keep("storage.size:above:2"), { type: "condition", op: "include", condition: "added:within:3" }]}
        counts={[{ count: 40 }, { count: 12 }, { count: 20 }]}
      />
    );

    expect(screen.getByText("40 left")).toBeVisible();
    expect(screen.getByText("12 left")).toBeVisible();
    expect(screen.getByText("+8")).toBeVisible();
  });

  it("says … beside a step it has no count for yet", () => {
    render(<Harness initial={[keep("added:beyond:30")]} counts={null} />);
    expect(screen.getAllByText("…").length).toBeGreaterThan(0);
  });
});

describe("the builder's changes reach the page", () => {
  it("every edit is handed to the page as a whole new list and the old one is not changed", () => {
    const onChange = vi.fn();
    const original: RetentionStep[] = [keep("added:beyond:30")];
    render(<RetentionStepBuilder steps={original} onChange={onChange} fields={fields} serverKinds={[]} counts={null} considered={1} noun="show" />);

    fireEvent.click(screen.getByRole("button", { name: "Keep only…" }));

    expect(onChange).toHaveBeenCalledTimes(1);
    expect(onChange.mock.calls[0][0]).toHaveLength(2);
    expect(original).toHaveLength(1);
  });
});
