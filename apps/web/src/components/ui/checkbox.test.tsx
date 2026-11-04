import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import * as React from "react";
import { describe, expect, it, vi } from "vitest";
import { Checkbox, CheckboxRow } from "./checkbox";
import { Field } from "./field";

describe("a checkbox", () => {
  it("reports the new state when it is clicked", async () => {
    const onCheckedChange = vi.fn();
    render(<Checkbox aria-label="Keep it" checked={false} onCheckedChange={onCheckedChange} />);
    await userEvent.click(screen.getByRole("checkbox", { name: "Keep it" }));
    expect(onCheckedChange).toHaveBeenCalledWith(true);
  });

  it("shows whether it is ticked and whether it is available", () => {
    render(<Checkbox aria-label="Keep it" checked disabled onCheckedChange={vi.fn()} />);
    const box = screen.getByRole("checkbox", { name: "Keep it" });
    expect(box).toBeChecked();
    expect(box).toBeDisabled();
  });

  it("shows a mixed state when some but not all are ticked, and clears it again", () => {
    const { rerender } = render(<Checkbox aria-label="All" checked={false} indeterminate onCheckedChange={vi.fn()} />);
    expect(screen.getByRole("checkbox", { name: "All" })).toBePartiallyChecked();
    rerender(<Checkbox aria-label="All" checked={false} onCheckedChange={vi.fn()} />);
    expect(screen.getByRole("checkbox", { name: "All" })).not.toBePartiallyChecked();
  });

  it("hands its input to a ref", () => {
    const ref = React.createRef<HTMLInputElement>();
    render(<Checkbox ref={ref} aria-label="Keep it" checked={false} onCheckedChange={vi.fn()} />);
    expect(ref.current).toBe(screen.getByRole("checkbox"));
  });

  it("takes its name from the field around it, and says when the field has a problem", () => {
    render(
      <Field label="Keep files" error="Choose one">
        <Checkbox checked={false} onCheckedChange={vi.fn()} />
      </Field>
    );
    const box = screen.getByRole("checkbox", { name: "Keep files" });
    expect(box).toHaveAttribute("aria-invalid", "true");
    expect(box).toHaveAccessibleDescription("Choose one");
  });

  it("is not marked invalid in a field with no problem", () => {
    render(
      <Field label="Keep files">
        <Checkbox checked={false} onCheckedChange={vi.fn()} />
      </Field>
    );
    expect(screen.getByRole("checkbox", { name: "Keep files" })).not.toHaveAttribute("aria-invalid");
  });

  it("lets the caller's own description and invalid state win over the field's", () => {
    render(
      <Field label="Keep files" error="Choose one">
        <Checkbox checked={false} onCheckedChange={vi.fn()} aria-invalid={false} aria-describedby="mine" />
      </Field>
    );
    const box = screen.getByRole("checkbox");
    expect(box).toHaveAttribute("aria-invalid", "false");
    expect(box).toHaveAttribute("aria-describedby", "mine");
  });
});

describe("a checkbox row", () => {
  it("is ticked by clicking its words, and shows its description", async () => {
    const onCheckedChange = vi.fn();
    render(<CheckboxRow label="Stop it" description="Also removes its files." checked={false} onCheckedChange={onCheckedChange} />);
    await userEvent.click(screen.getByText("Stop it"));
    expect(onCheckedChange).toHaveBeenCalledWith(true);
    expect(screen.getByText("Also removes its files.")).toBeInTheDocument();
  });

  it("shows no description line when it has none, and takes a class", () => {
    const { container } = render(<CheckboxRow label="Stop it" checked onCheckedChange={vi.fn()} className="extra" />);
    expect(container.querySelectorAll("label span")).toHaveLength(1);
    expect(container.firstElementChild).toHaveClass("extra");
  });

  it("uses the id it was given, and is not clickable when disabled", async () => {
    const onCheckedChange = vi.fn();
    render(<CheckboxRow id="mine" label="Stop it" checked={false} disabled onCheckedChange={onCheckedChange} />);
    const box = screen.getByRole("checkbox");
    expect(box).toHaveAttribute("id", "mine");
    expect(box).toBeDisabled();
    await userEvent.click(screen.getByText("Stop it"));
    expect(onCheckedChange).not.toHaveBeenCalled();
  });
});
