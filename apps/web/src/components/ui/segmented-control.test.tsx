import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { Field } from "./field";
import { SegmentedControl, type SegmentedOption } from "./segmented-control";

afterEach(cleanup);

const options: SegmentedOption<"a" | "b" | "c">[] = [
  { value: "a", label: "Alpha" },
  { value: "b", label: "Beta" },
  { value: "c", label: "Gamma" }
];
const show = (over: Partial<Parameters<typeof SegmentedControl<"a" | "b" | "c">>[0]> = {}) => {
  const onValueChange = vi.fn();
  render(<SegmentedControl value="a" onValueChange={onValueChange} options={options} aria-label="Pick one" {...over} />);
  return onValueChange;
};
const radio = (name: string) => screen.getByRole("radio", { name });

describe("the segmented control", () => {
  it("is a group of radios named by its label, with the chosen one checked and the only one in the tab order", () => {
    show({ value: "b" });
    expect(screen.getByRole("radiogroup", { name: "Pick one" })).toBeInTheDocument();
    expect(screen.getAllByRole("radio").map((button) => [button.textContent, button.getAttribute("aria-checked"), button.getAttribute("tabindex")])).toEqual([
      ["Alpha", "false", "-1"], ["Beta", "true", "0"], ["Gamma", "false", "-1"]
    ]);
  });

  it("chooses what is clicked", () => {
    const change = show();
    fireEvent.click(radio("Gamma"));
    expect(change).toHaveBeenCalledWith("c");
  });

  it("moves to the next option with the right or down arrow, and the previous with the left or up arrow", () => {
    const change = show({ value: "b" });
    fireEvent.keyDown(radio("Beta"), { key: "ArrowRight" });
    expect(change).toHaveBeenLastCalledWith("c");
    expect(radio("Gamma")).toHaveFocus();
    fireEvent.keyDown(radio("Beta"), { key: "ArrowDown" });
    expect(change).toHaveBeenLastCalledWith("c");
    fireEvent.keyDown(radio("Beta"), { key: "ArrowLeft" });
    expect(change).toHaveBeenLastCalledWith("a");
    expect(radio("Alpha")).toHaveFocus();
    fireEvent.keyDown(radio("Beta"), { key: "ArrowUp" });
    expect(change).toHaveBeenLastCalledWith("a");
  });

  it("goes round from the last option to the first and from the first to the last", () => {
    const change = show({ value: "c" });
    fireEvent.keyDown(radio("Gamma"), { key: "ArrowRight" });
    expect(change).toHaveBeenLastCalledWith("a");
    fireEvent.keyDown(radio("Alpha"), { key: "ArrowLeft" });
    expect(change).toHaveBeenLastCalledWith("c");
  });

  it("holds the arrow keys back from the page, and ignores every other key", () => {
    const change = show();
    const right = fireEvent.keyDown(radio("Alpha"), { key: "ArrowRight" });
    expect(right).toBe(false);
    const left = fireEvent.keyDown(radio("Alpha"), { key: "ArrowLeft" });
    expect(left).toBe(false);
    change.mockClear();
    expect(fireEvent.keyDown(radio("Alpha"), { key: "Enter" })).toBe(true);
    expect(fireEvent.keyDown(radio("Alpha"), { key: "a" })).toBe(true);
    expect(change).not.toHaveBeenCalled();
  });

  it("skips an option that is disabled, going on to the next one that is not", () => {
    const change = show({ options: [options[0], { ...options[1], disabled: true }, options[2]] });
    expect(radio("Beta")).toBeDisabled();
    fireEvent.keyDown(radio("Alpha"), { key: "ArrowRight" });
    expect(change).toHaveBeenLastCalledWith("c");
    // Going left onto a disabled option looks forward for the next enabled one, which is where it started.
    fireEvent.keyDown(radio("Gamma"), { key: "ArrowLeft" });
    expect(change).toHaveBeenLastCalledWith("c");
  });

  it("goes back to the first enabled option when the ones after it are disabled", () => {
    const change = show({ value: "a", options: [options[0], options[1], { ...options[2], disabled: true }] });
    fireEvent.keyDown(radio("Beta"), { key: "ArrowRight" });
    expect(change).toHaveBeenLastCalledWith("a");
    expect(radio("Alpha")).toHaveFocus();
  });

  it("holds every option while it is disabled, and dims itself", () => {
    show({ disabled: true });
    for (const button of screen.getAllByRole("radio")) expect(button).toBeDisabled();
    expect(screen.getByRole("radiogroup").className).toContain("opacity-60");
    cleanup();
    show();
    expect(screen.getByRole("radiogroup").className).not.toContain("opacity-60");
    for (const button of screen.getAllByRole("radio")) expect(button).not.toBeDisabled();
  });

  it("takes an extra class, and draws the chosen option in the accent colour", () => {
    show({ className: "wide" });
    expect(screen.getByRole("radiogroup").className).toContain("wide");
    expect(radio("Alpha").className).toContain("text-primary");
    expect(radio("Beta").className).toContain("text-muted-foreground");
    expect(radio("Beta").className).not.toContain("text-primary");
  });

  it("is named and described by the field around it when it has no label of its own", () => {
    render(<Field label="Quality" help="Choose carefully"><SegmentedControl value="a" onValueChange={() => undefined} options={options} /></Field>);
    const group = screen.getByRole("radiogroup", { name: /Quality/ });
    expect(group.id).not.toBe("");
    expect(group.getAttribute("aria-describedby")).toBeTruthy();
    expect(group).not.toHaveAttribute("aria-label");
  });

  it("keeps its own label over the field's", () => {
    render(<Field label="Quality"><SegmentedControl value="a" onValueChange={() => undefined} options={options} aria-label="Own name" /></Field>);
    const group = screen.getByRole("radiogroup", { name: "Own name" });
    expect(group).not.toHaveAttribute("aria-labelledby");
  });

  it("has no id or description outside a field", () => {
    show();
    const group = screen.getByRole("radiogroup");
    expect(group).not.toHaveAttribute("id");
    expect(group).not.toHaveAttribute("aria-describedby");
    expect(group).not.toHaveAttribute("aria-labelledby");
  });
});
