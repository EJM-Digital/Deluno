import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ChoiceRows, type ChoiceRowOption } from "./choice-rows";
import { Field } from "./field";

afterEach(cleanup);

const options: ChoiceRowOption<"a" | "b" | "c">[] = [
  { value: "a", label: "Alpha", description: "Does the first thing" },
  { value: "b", label: "Beta" },
  { value: "c", label: "Gamma", description: "Does the last thing" }
];
const show = (over: Partial<Parameters<typeof ChoiceRows<"a" | "b" | "c">>[0]> = {}) => {
  const onValueChange = vi.fn();
  render(<ChoiceRows value="a" onValueChange={onValueChange} options={options} aria-label="Pick one" {...over} />);
  lastChange = onValueChange;
  return onValueChange;
};
let lastChange: ReturnType<typeof vi.fn> | null = null;
const press = (element: Element, init: { key: string }) => { lastChange?.mockClear(); return fireEvent.keyDown(element, init); };
const radio = (name: RegExp | string) => screen.getByRole("radio", { name });

describe("the choice rows", () => {
  it("shows every option on screen with what it does, the chosen one checked and the only one in the tab order", () => {
    show({ value: "c" });
    expect(screen.getByRole("radiogroup", { name: "Pick one" })).toBeInTheDocument();
    expect(screen.getAllByRole("radio").map((button) => [button.getAttribute("aria-checked"), button.getAttribute("tabindex")])).toEqual([["false", "-1"], ["false", "-1"], ["true", "0"]]);
    expect(screen.getByText("Does the first thing")).toBeInTheDocument();
    expect(screen.getByText("Does the last thing")).toBeInTheDocument();
    expect(radio(/Beta/).textContent).toBe("Beta");
  });

  it("chooses what is clicked", () => {
    const change = show();
    fireEvent.click(radio(/Gamma/));
    expect(change).toHaveBeenCalledWith("c");
  });

  it("moves down or right to the next option and up or left to the previous one, taking focus with it", () => {
    const change = show({ value: "b" });
    press(radio(/Beta/), { key: "ArrowDown" });
    expect(change).toHaveBeenCalledTimes(1);
    expect(change).toHaveBeenLastCalledWith("c");
    expect(radio(/Gamma/)).toHaveFocus();
    press(radio(/Beta/), { key: "ArrowRight" });
    expect(change).toHaveBeenCalledTimes(1);
    expect(change).toHaveBeenLastCalledWith("c");
    press(radio(/Beta/), { key: "ArrowUp" });
    expect(change).toHaveBeenCalledTimes(1);
    expect(change).toHaveBeenLastCalledWith("a");
    expect(radio(/Alpha/)).toHaveFocus();
    press(radio(/Beta/), { key: "ArrowLeft" });
    expect(change).toHaveBeenCalledTimes(1);
    expect(change).toHaveBeenLastCalledWith("a");
  });

  it("goes round at either end", () => {
    const change = show({ value: "c" });
    press(radio(/Gamma/), { key: "ArrowDown" });
    expect(change).toHaveBeenCalledTimes(1);
    expect(change).toHaveBeenLastCalledWith("a");
    cleanup();
    const again = show({ value: "a" });
    press(radio(/Alpha/), { key: "ArrowUp" });
    expect(again).toHaveBeenLastCalledWith("c");
  });

  it("holds the arrow keys back from the page, and ignores every other key", () => {
    const change = show();
    expect(press(radio(/Alpha/), { key: "ArrowDown" })).toBe(false);
    expect(press(radio(/Alpha/), { key: "ArrowUp" })).toBe(false);
    change.mockClear();
    expect(press(radio(/Alpha/), { key: "Enter" })).toBe(true);
    expect(change).not.toHaveBeenCalled();
  });

  it("skips an option that cannot be chosen", () => {
    const change = show({ value: "a", options: [options[0], { ...options[1], disabled: true }, options[2]] });
    expect(radio(/Beta/)).toBeDisabled();
    press(radio(/Alpha/), { key: "ArrowDown" });
    expect(change).toHaveBeenCalledTimes(1);
    expect(change).toHaveBeenLastCalledWith("c");
    expect(radio(/Gamma/)).toHaveFocus();
  });

  it("moves to the first option when the chosen one is not among those that can be chosen", () => {
    const change = show({ value: "b", options: [options[0], { ...options[1], disabled: true }, options[2]] });
    press(radio(/Alpha/), { key: "ArrowDown" });
    expect(change).toHaveBeenCalledTimes(1);
    expect(change).toHaveBeenLastCalledWith("a");
  });

  it("holds every option while disabled, and dims itself", () => {
    show({ disabled: true });
    for (const button of screen.getAllByRole("radio")) expect(button).toBeDisabled();
    expect(screen.getByRole("radiogroup").className).toContain("opacity-60");
    cleanup();
    show({ className: "wide" });
    expect(screen.getByRole("radiogroup").className).not.toContain("opacity-60");
    expect(screen.getByRole("radiogroup").className).toContain("wide");
    for (const button of screen.getAllByRole("radio")) expect(button).not.toBeDisabled();
  });

  it("draws the chosen option with the accent colours and the others plain", () => {
    show({ value: "b" });
    expect(radio(/Beta/).className).toContain("border-primary/60 bg-primary/8");
    expect(radio(/Alpha/).className).toContain("border-hairline bg-surface-1");
    expect(radio(/Alpha/).className).not.toContain("border-primary/60");
    const dots = (name: RegExp) => radio(name).querySelector("[aria-hidden]")!;
    expect(dots(/Beta/).className).toContain("border-primary");
    expect(dots(/Beta/).firstElementChild!.className).toContain("bg-primary");
    expect(dots(/Alpha/).className).toContain("border-hairline");
    expect(dots(/Alpha/).firstElementChild!.className).toContain("bg-transparent");
  });

  it("is named and described by the field around it when it has no label of its own", () => {
    render(<Field label="Mode" help="Be careful"><ChoiceRows value="a" onValueChange={() => undefined} options={options} /></Field>);
    const group = screen.getByRole("radiogroup", { name: /Mode/ });
    expect(group.id).not.toBe("");
    expect(group.getAttribute("aria-describedby")).toBeTruthy();
    expect(group).not.toHaveAttribute("aria-label");
  });

  it("keeps its own label over the field's, and has no id or description outside a field", () => {
    render(<Field label="Mode"><ChoiceRows value="a" onValueChange={() => undefined} options={options} aria-label="Own" /></Field>);
    expect(screen.getByRole("radiogroup", { name: "Own" })).not.toHaveAttribute("aria-labelledby");
    cleanup();
    show();
    const group = screen.getByRole("radiogroup");
    expect(group).not.toHaveAttribute("id");
    expect(group).not.toHaveAttribute("aria-describedby");
    expect(group).not.toHaveAttribute("aria-labelledby");
  });
});
