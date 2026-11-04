import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { PresetField, type PresetOption } from "./preset-field";

afterEach(cleanup);

const options: PresetOption[] = [{ label: "Daily", value: "24" }, { label: "Weekly", value: "168" }];
const show = (over: Partial<Parameters<typeof PresetField>[0]> = {}) => {
  const onChange = vi.fn();
  const view = render(<PresetField value="24" onChange={onChange} options={options} {...over} />);
  return { onChange, ...view };
};
const select = () => screen.getByRole("combobox") as HTMLSelectElement;
const optionTexts = () => Array.from(select().options).map((option) => [option.value, option.textContent, option.disabled]);

describe("the preset field", () => {
  it("offers its presets and a custom one, with the matching preset chosen", () => {
    show();
    expect(select().value).toBe("24");
    expect(optionTexts()).toEqual([["24", "Daily", false], ["168", "Weekly", false], ["__custom", "Custom", false]]);
    expect(screen.queryByRole("textbox")).toBeNull();
  });

  it("picks another preset", () => {
    const { onChange } = show();
    fireEvent.change(select(), { target: { value: "168" } });
    expect(onChange).toHaveBeenCalledWith("168");
  });

  it("asks to choose while nothing is chosen, and says what to choose in the words it is given", () => {
    show({ value: "" });
    expect(optionTexts()[0]).toEqual(["", "Choose…", true]);
    expect(select().value).toBe("");
    cleanup();
    show({ value: "", placeholder: "Pick…" });
    expect(optionTexts()[0]).toEqual(["", "Pick…", true]);
  });

  it("does not ask to choose when one of its presets is the empty one", () => {
    show({ value: "", options: [{ label: "Never", value: "" }, ...options] });
    expect(optionTexts().filter(([value]) => value === "")).toEqual([["", "Never", false]]);
    expect(select().value).toBe("");
  });

  it("shows a value that is not a preset as custom, with the value ready to edit", () => {
    const { onChange } = show({ value: "6", customPlaceholder: "Hours" });
    expect(select().value).toBe("__custom");
    const input = screen.getByRole("textbox", { name: "Custom" });
    expect(input).toHaveValue("6");
    expect(input).toHaveAttribute("placeholder", "Hours");
    fireEvent.change(input, { target: { value: "12" } });
    expect(onChange).toHaveBeenCalledWith("12");
  });

  it("opens the custom box empty when custom is chosen from a preset, and keeps it open while it is typed in", () => {
    const view = show();
    fireEvent.change(select(), { target: { value: "__custom" } });
    expect(view.onChange).toHaveBeenCalledWith("");
    view.rerender(<PresetField value="" onChange={view.onChange} options={options} />);
    expect(select().value).toBe("__custom");
    expect(screen.getByRole("textbox", { name: "Custom" })).toHaveValue("");
    expect(optionTexts().some(([value]) => value === "")).toBe(false);
  });

  it("keeps the custom value when custom is chosen again", () => {
    const { onChange } = show({ value: "6" });
    fireEvent.change(select(), { target: { value: "__custom" } });
    expect(onChange).toHaveBeenCalledWith("6");
  });

  it("closes the custom box when a preset is typed in, or picked", () => {
    const view = show({ value: "6" });
    expect(screen.getByRole("textbox")).toBeInTheDocument();
    view.rerender(<PresetField value="24" onChange={view.onChange} options={options} />);
    expect(screen.queryByRole("textbox")).toBeNull();
    expect(select().value).toBe("24");
  });

  it("closes the custom box once a preset is picked from it", () => {
    const view = show({ value: "6" });
    fireEvent.change(select(), { target: { value: "168" } });
    expect(view.onChange).toHaveBeenCalledWith("168");
    view.rerender(<PresetField value="168" onChange={view.onChange} options={options} />);
    expect(screen.queryByRole("textbox")).toBeNull();
  });

  it("stays open for an edit that comes back as empty, while the person is typing", () => {
    const view = show({ value: "6" });
    fireEvent.change(screen.getByRole("textbox"), { target: { value: "" } });
    view.rerender(<PresetField value="" onChange={view.onChange} options={options} />);
    expect(screen.getByRole("textbox")).toHaveValue("");
    expect(select().value).toBe("__custom");
  });

  it("closes the custom box when what was typed becomes one of the presets", () => {
    const view = show({ value: "24" });
    fireEvent.change(select(), { target: { value: "__custom" } });
    view.rerender(<PresetField value="" onChange={view.onChange} options={options} />);
    expect(screen.getByRole("textbox")).toBeInTheDocument();
    view.rerender(<PresetField value="168" onChange={view.onChange} options={options} />);
    expect(screen.queryByRole("textbox")).toBeNull();
    expect(select().value).toBe("168");
  });

  it("leaves custom for the empty preset when that is picked", () => {
    const withNever: PresetOption[] = [{ label: "Never", value: "" }, ...options];
    const view = show({ value: "24", options: withNever });
    fireEvent.change(select(), { target: { value: "__custom" } });
    view.rerender(<PresetField value="" onChange={view.onChange} options={withNever} />);
    expect(screen.getByRole("textbox")).toBeInTheDocument();
    fireEvent.change(select(), { target: { value: "" } });
    expect(view.onChange).toHaveBeenLastCalledWith("");
    view.rerender(<PresetField value="" onChange={view.onChange} options={withNever} />);
    expect(screen.queryByRole("textbox")).toBeNull();
    expect(select().value).toBe("");
  });

  it("follows a change in its presets", () => {
    const view = show({ value: "6" });
    expect(screen.getByRole("textbox")).toBeInTheDocument();
    view.rerender(<PresetField value="6" onChange={view.onChange} options={[...options, { label: "Six hours", value: "6" }]} />);
    expect(screen.queryByRole("textbox")).toBeNull();
    expect(select().value).toBe("6");
  });

  it("has no custom choice when custom is not allowed", () => {
    show({ allowCustom: false, value: "6" });
    expect(optionTexts().some(([value]) => value === "__custom")).toBe(false);
    expect(screen.queryByRole("textbox")).toBeNull();
  });

  it("calls its custom choice by the name it is given, and its box by the same", () => {
    show({ customLabel: "My own", value: "6" });
    expect(optionTexts().at(-1)).toEqual(["__custom", "My own", false]);
    expect(screen.getByRole("textbox", { name: "My own" })).toBeInTheDocument();
  });

  it("makes the custom box a number box when asked", () => {
    show({ value: "6", inputType: "number" });
    expect(screen.getByRole("spinbutton", { name: "Custom" })).toBeInTheDocument();
  });

  it("holds both the select and the custom box while disabled", () => {
    show({ value: "6", disabled: true });
    expect(select()).toBeDisabled();
    expect(screen.getByRole("textbox")).toBeDisabled();
    cleanup();
    show({ value: "6" });
    expect(select()).not.toBeDisabled();
    expect(screen.getByRole("textbox")).not.toBeDisabled();
  });
});
