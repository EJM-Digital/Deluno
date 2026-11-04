import { fireEvent, render, screen } from "@testing-library/react";
import { useState } from "react";
import { describe, expect, it } from "vitest";
import { describeCondition, operatorLabel, parseCondition, RetentionStepBuilder, type RetentionField, type RetentionStep } from "./retention-step-builder";

// #1329: the rest of the retention step builder: how a rule reads, and every control of a condition row.

const field = (id: string, label: string, group: string, valueKind: string, operators: string[], extra: Partial<RetentionField> = {}): RetentionField => ({
  id, label, hint: "", group, valueKind, operators, options: null, source: "deluno", unsupportedOn: [], ...extra
});

const fields: RetentionField[] = [
  field("server.personViews", "Times one person finished it", "watching", "integer", ["min", "is"], { source: "server", perPerson: true }),
  field("part.finale", "Is a season finale", "part", "boolean", ["is"]),
  field("storage.size", "Size on disk", "storage", "gigabytes", ["above", "below"], { source: "disk" }),
  field("title.genre", "Genre", "title", "text", ["is", "isnot"], { options: ["Drama", "Comedy"] }),
  field("title.name", "Title", "title", "text", ["is", "in"]),
  field("added", "Added", "time", "date", ["beyond", "before", "set"]),
  field("year", "Year", "title", "year", ["is"]),
  field("odd", "Odd one", "decision", "text", [])
];

function Harness({ initial = [], withFields = fields }: { initial?: RetentionStep[]; withFields?: RetentionField[] }) {
  const [steps, setSteps] = useState<RetentionStep[]>(initial);
  return (
    <>
      <RetentionStepBuilder steps={steps} onChange={setSteps} fields={withFields} serverKinds={[]} counts={null} considered={5} noun="film" />
      <output data-testid="steps">{JSON.stringify(steps)}</output>
    </>
  );
}

const stepsNow = () => JSON.parse(screen.getByTestId("steps").textContent ?? "[]") as RetentionStep[];
const keep = (condition: string | null): RetentionStep => ({ type: "condition", op: "keep", condition });
const change = (label: string, value: string) => fireEvent.change(screen.getByLabelText(label), { target: { value } });

describe("reading a rule's condition", () => {
  it("reads a missing condition as empty, and a field with no test as having an empty test", () => {
    expect(parseCondition(null)).toEqual({ field: "", person: "", operator: "", value: "" });
    expect(parseCondition(undefined)).toEqual({ field: "", person: "", operator: "", value: "" });
    expect(parseCondition("added")).toEqual({ field: "added", person: "", operator: "", value: "" });
  });

  it("keeps a value that itself holds colons", () => {
    expect(parseCondition("title.name:is:a:b:c").value).toBe("a:b:c");
  });

  it("keeps a person's name as it is when it cannot be decoded", () => {
    expect(parseCondition("server.personViews@100%:min:3").person).toBe("100%");
  });

  it("says a field it does not know by its id, with the person in brackets", () => {
    expect(describeCondition("gone.field@Ben:is:x", fields)).toBe("gone.field (Ben) is x");
  });

  it("reads a test that takes no value, a yes or no, a sentence with a gap, and a plain test", () => {
    expect(describeCondition("added:set:", fields)).toBe("Added has a value");
    expect(describeCondition("part.finale:is:true", fields)).toBe("Is a season finale is yes");
    expect(describeCondition("part.finale:is:false", fields)).toBe("Is a season finale is no");
    expect(describeCondition("server.lastViewed:beyond:30", [field("server.lastViewed", "Last viewed", "server", "date", ["beyond"], { source: "server" })])).toBe("Last viewed more than 30 days ago");
    expect(describeCondition("added:beyond:30", fields)).toBe("Added not in the last 30");
    expect(describeCondition("storage.size:above:5", fields)).toBe("Size on disk above 5");
  });

  it("labels a test the vocabulary does not know as itself", () => {
    expect(operatorLabel(undefined, "weird")).toBe("weird");
    expect(operatorLabel(field("w", "W", "g", "users", []), "weird")).toBe("weird");
    expect(operatorLabel(field("w", "W", "g", "date", [], { source: "server" }), "weird")).toBe("weird");
    expect(operatorLabel(field("w", "W", "g", "date", [], { source: "server" }), "beyond")).toBe("more than … days ago");
  });
});

describe("adding a first step when the page gives different fields", () => {
  it("starts a keep step on the first field with its first test, and its person left empty for a per-person field", () => {
    render(<Harness />);
    fireEvent.click(screen.getByRole("button", { name: "Keep only…" }));
    expect(stepsNow()[0].condition).toBe("server.personViews@:min:");
  });

  it("starts a yes on a yes or no field", () => {
    render(<Harness withFields={[fields[1]]} />);
    fireEvent.click(screen.getByRole("button", { name: "Keep only…" }));
    expect(stepsNow()[0].condition).toBe("part.finale:is:true");
  });

  it("starts with an empty test for a field that has none", () => {
    render(<Harness withFields={[fields[7]]} />);
    fireEvent.click(screen.getByRole("button", { name: "Keep only…" }));
    expect(stepsNow()[0].condition).toBe("odd::");
  });

  it("starts with nothing at all when there are no fields", () => {
    render(<Harness withFields={[]} />);
    fireEvent.click(screen.getByRole("button", { name: "Keep only…" }));
    expect(stepsNow()[0].condition).toBe("");
  });
});

describe("a condition row", () => {
  it("changing the field to a yes or no field starts at yes", () => {
    render(<Harness initial={[keep("added:beyond:30")]} />);
    change("Step 1 field", "part.finale");
    expect(stepsNow()[0].condition).toBe("part.finale:is:true");
  });

  it("changing the field to one with no tests starts with an empty test", () => {
    render(<Harness initial={[keep("added:beyond:30")]} />);
    change("Step 1 field", "odd");
    expect(stepsNow()[0].condition).toBe("odd::");
  });

  it("changing the test keeps the value when the new test takes one, and clears it when it does not", () => {
    render(<Harness initial={[keep("added:beyond:30")]} />);
    change("Step 1 test", "before");
    expect(stepsNow()[0].condition).toBe("added:before:30");
    change("Step 1 test", "set");
    expect(stepsNow()[0].condition).toBe("added:set:");
  });

  it("types a day count, a date, a number and a word into the right kind of box", () => {
    render(<Harness initial={[keep("added:beyond:30"), keep("added:before:2026-01-01"), keep("storage.size:above:5"), keep("title.name:is:Dune"), keep("year:is:2020")]} />);
    expect(screen.getByLabelText("Step 1 value")).toHaveAttribute("type", "number");
    expect(screen.getByLabelText("Step 1 value")).toHaveAttribute("min", "1");
    expect(screen.getByLabelText("Step 2 value")).toHaveAttribute("type", "date");
    expect(screen.getByLabelText("Step 2 value")).not.toHaveAttribute("min");
    expect(screen.getByLabelText("Step 3 value")).toHaveAttribute("type", "number");
    expect(screen.getByLabelText("Step 3 value")).toHaveAttribute("placeholder", "GB");
    expect(screen.getByLabelText("Step 4 value")).toHaveAttribute("type", "text");
    expect(screen.getByLabelText("Step 4 value")).toHaveAttribute("placeholder", "value");
    expect(screen.getByLabelText("Step 5 value")).toHaveAttribute("type", "number");
  });

  it("edits the value, the yes or no, and the choice from a list, each straight into the rule", () => {
    render(<Harness initial={[keep("storage.size:above:5"), keep("part.finale:is:true"), keep("title.genre:is:Drama")]} />);
    change("Step 1 value", "9");
    change("Step 2 value", "false");
    change("Step 3 value", "Comedy");
    expect(stepsNow().map((step) => step.condition)).toEqual(["storage.size:above:9", "part.finale:is:false", "title.genre:is:Comedy"]);
  });

  it("holds a list as comma separated words and stores it with bars", () => {
    render(<Harness initial={[keep("title.name:in:Dune|Heat")]} />);
    expect(screen.getByLabelText("Step 1 value")).toHaveValue("Dune, Heat");
    expect(screen.getByLabelText("Step 1 value")).toHaveAttribute("placeholder", "comma separated");
    change("Step 1 value", "Dune,  Heat , Tenet");
    expect(stepsNow()[0].condition).toBe("title.name:in:Dune|Heat|Tenet");
  });

  it("asks whose answer a per-person field wants, and keeps the person when another box changes", () => {
    render(<Harness initial={[keep("server.personViews@Ben:min:2")]} />);
    expect(screen.getByLabelText("Step 1 person")).toHaveValue("Ben");
    change("Step 1 person", "Sam Jones");
    expect(stepsNow()[0].condition).toBe("server.personViews@Sam%20Jones:min:2");
    change("Step 1 value", "4");
    expect(stepsNow()[0].condition).toBe("server.personViews@Sam%20Jones:min:4");
    change("Step 1 test", "is");
    expect(stepsNow()[0].condition).toBe("server.personViews@Sam%20Jones:is:4");
  });

  it("still draws a rule that names a field that has gone, with no tests to choose from", () => {
    render(<Harness initial={[keep("vanished:is:1")]} />);
    expect(screen.getByLabelText("Step 1 test")).toBeInTheDocument();
    expect((screen.getByLabelText("Step 1 test") as HTMLSelectElement).options).toHaveLength(0);
    expect(screen.getByLabelText("Step 1 value")).toHaveAttribute("type", "text");
  });

  it("draws a step with no condition as an empty row", () => {
    render(<Harness initial={[keep(null)]} />);
    expect((screen.getByLabelText("Step 1 test") as HTMLSelectElement).options).toHaveLength(0);
  });
});

describe("a block", () => {
  const block = (over: Partial<RetentionStep> = {}): RetentionStep => ({ type: "block", op: "keep", name: "Old", steps: [keep("added:beyond:30")], ...over });

  it("renames, changes how it joins, and takes steps added inside it", () => {
    render(<Harness initial={[keep("added:beyond:1"), block({ op: "include" })]} />);
    change("Step 2 block name", "Stale");
    expect(stepsNow()[1].name).toBe("Stale");
    change("Step 2 joins as", "keep");
    expect(stepsNow()[1].op).toBe("keep");
    fireEvent.click(screen.getAllByRole("button", { name: "Keep only…" })[0]);
    expect(stepsNow()[1].steps).toHaveLength(2);
  });

  it("adds an Also include inside a block that already has a step", () => {
    render(<Harness initial={[block()]} />);
    fireEvent.click(screen.getAllByRole("button", { name: "Also include…" })[0]);
    expect(stepsNow()[0].steps).toHaveLength(2);
    expect(stepsNow()[0].steps![1].op).toBe("include");
  });

  it("starts a block that has no name or steps yet, and can have its first step added", () => {
    render(<Harness initial={[{ type: "block", op: "keep" }]} />);
    expect(screen.getByLabelText("Step 1 block name")).toHaveValue("");
    expect(screen.getAllByRole("button", { name: "Also include…" })[0]).toBeDisabled();
    fireEvent.click(screen.getAllByRole("button", { name: "Keep only…" })[0]);
    expect(stepsNow()[0].steps).toHaveLength(1);
  });

  it("edits a step inside a block and removes it", () => {
    render(<Harness initial={[block()]} />);
    change("Step 1 part 1 value", "60");
    expect(stepsNow()[0].steps![0].condition).toBe("added:beyond:60");
    fireEvent.click(screen.getByRole("button", { name: "Remove step 1 part 1" }));
    expect(stepsNow()[0].steps).toHaveLength(0);
  });
});
