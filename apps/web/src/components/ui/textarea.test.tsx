import { render, screen } from "@testing-library/react";
import { createRef } from "react";
import { describe, expect, it } from "vitest";
import { Field } from "./field";
import { Textarea } from "./textarea";

describe("the text area", () => {
  it("is three rows tall unless asked otherwise, and grows downwards", () => {
    render(<Textarea aria-label="Notes" />);
    const box = screen.getByRole("textbox", { name: "Notes" });
    expect(box).toHaveAttribute("rows", "3");
    expect(box.className).toContain("resize-y");
    expect(box.className).toContain("min-h-[calc(var(--control-height)*2)]");
    expect(box.className).toContain("leading-relaxed");
    expect(box.tagName).toBe("TEXTAREA");
  });

  it("takes the rows, id, class and any other property it is given", () => {
    render(<Textarea aria-label="Notes" rows={6} id="mine" className="extra" placeholder="Say more" />);
    const box = screen.getByRole("textbox", { name: "Notes" });
    expect(box).toHaveAttribute("rows", "6");
    expect(box).toHaveAttribute("id", "mine");
    expect(box.className).toContain("extra");
    expect(box).toHaveAttribute("placeholder", "Say more");
  });

  it("hands its element to a ref", () => {
    const ref = createRef<HTMLTextAreaElement>();
    render(<Textarea ref={ref} aria-label="Notes" />);
    expect(ref.current).toBe(screen.getByRole("textbox"));
  });

  it("has no id, description or invalid mark of its own outside a field", () => {
    render(<Textarea aria-label="Notes" />);
    const box = screen.getByRole("textbox");
    expect(box).not.toHaveAttribute("id");
    expect(box).not.toHaveAttribute("aria-describedby");
    expect(box).not.toHaveAttribute("aria-invalid");
  });

  it("is labelled, described and marked invalid by the field around it", () => {
    render(<Field label="Notes" help="Anything useful" error="Too long"><Textarea /></Field>);
    const box = screen.getByRole("textbox", { name: /Notes/ });
    expect(box.id).not.toBe("");
    expect(box).toHaveAttribute("aria-invalid", "true");
    expect(box.getAttribute("aria-describedby")).toBeTruthy();
    expect(document.getElementById(box.getAttribute("aria-describedby")!)).toHaveTextContent("Too long");
  });

  it("is not marked invalid by a field with nothing wrong", () => {
    render(<Field label="Notes" help="Anything useful"><Textarea /></Field>);
    expect(screen.getByRole("textbox", { name: /Notes/ })).not.toHaveAttribute("aria-invalid");
  });

  it("keeps the description, id and invalid mark it was given over the field's", () => {
    render(<Field label="Notes" error="Too long"><Textarea id="own" aria-describedby="elsewhere" aria-invalid={false} /></Field>);
    const box = screen.getByRole("textbox");
    expect(box).toHaveAttribute("id", "own");
    expect(box).toHaveAttribute("aria-describedby", "elsewhere");
    expect(box).toHaveAttribute("aria-invalid", "false");
  });
});
