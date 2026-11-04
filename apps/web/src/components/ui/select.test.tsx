import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { Field } from "./field";
import { Select } from "./select";

const OPTIONS = [{ value: "a", label: "Alpha" }, { value: "b", label: "Beta", disabled: true }];

describe("a select", () => {
  it("lists its options, with a placeholder first when it has one", () => {
    render(<Select aria-label="Pick" options={OPTIONS} placeholder="Choose" />);
    expect(screen.getAllByRole("option").map((option) => option.textContent)).toEqual(["Choose", "Alpha", "Beta"]);
    expect(screen.getByRole("option", { name: "Beta" })).toBeDisabled();
    expect(screen.getByRole("option", { name: "Alpha" })).toBeEnabled();
    expect((screen.getByRole("option", { name: "Choose" }) as HTMLOptionElement).value).toBe("");
  });

  it("has no empty first option without a placeholder, and takes ordinary options as children", () => {
    render(<Select aria-label="Pick"><option value="x">Child one</option></Select>);
    expect(screen.getAllByRole("option").map((option) => option.textContent)).toEqual(["Child one"]);
  });

  it("tells the caller which option was picked", async () => {
    const onChange = vi.fn();
    render(<Select aria-label="Pick" options={OPTIONS} onChange={onChange} />);
    await userEvent.selectOptions(screen.getByRole("combobox", { name: "Pick" }), "a");
    expect(onChange).toHaveBeenCalledTimes(1);
  });

  it("wears the control box by default and leaves it off inside chrome of its own", () => {
    const { rerender } = render(<Select aria-label="Pick" options={OPTIONS} />);
    expect(screen.getByRole("combobox")).toHaveClass("hover:bg-surface-2");
    rerender(<Select aria-label="Pick" options={OPTIONS} chrome={false} />);
    expect(screen.getByRole("combobox")).not.toHaveClass("hover:bg-surface-2");
  });

  it("puts width classes on the box it shares with its arrow, and the rest on the select", () => {
    const { container } = render(<Select aria-label="Pick" options={OPTIONS} className="w-auto md:min-w-40 text-sm" />);
    expect(container.firstElementChild).toHaveClass("w-auto", "md:min-w-40");
    expect(screen.getByRole("combobox")).toHaveClass("text-sm");
    expect(screen.getByRole("combobox")).not.toHaveClass("w-auto");
  });

  it("fills its row when it is given no width", () => {
    const { container } = render(<Select aria-label="Pick" options={OPTIONS} />);
    expect(container.firstElementChild).toHaveClass("w-full");
  });

  it("takes its name and its problem from the field around it", () => {
    render(
      <Field label="Quality" error="Pick one">
        <Select options={OPTIONS} />
      </Field>
    );
    const select = screen.getByRole("combobox", { name: "Quality" });
    expect(select).toHaveAttribute("aria-invalid", "true");
    expect(select).toHaveAccessibleDescription("Pick one");
  });

  it("is not marked invalid in a field with no problem, and the caller's own settings win", () => {
    const { rerender } = render(
      <Field label="Quality"><Select options={OPTIONS} /></Field>
    );
    expect(screen.getByRole("combobox")).not.toHaveAttribute("aria-invalid");
    rerender(
      <Field label="Quality" error="Pick one"><Select options={OPTIONS} aria-invalid={false} aria-describedby="mine" /></Field>
    );
    expect(screen.getByRole("combobox")).toHaveAttribute("aria-invalid", "false");
    expect(screen.getByRole("combobox")).toHaveAttribute("aria-describedby", "mine");
  });

  it("uses the id it was given", () => {
    render(<Select aria-label="Pick" id="mine" options={OPTIONS} />);
    expect(screen.getByRole("combobox")).toHaveAttribute("id", "mine");
  });
});
