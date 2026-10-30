import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { RetentionKeepDialog } from "./retention-keep-dialog";

const props = { title: "Dune", ruleName: "Old films", onOpenChange: vi.fn(), onConfirm: vi.fn() };

describe("keeping a Leaving soon title", () => {
  it("keeps it from the rule that caught it, with a note, by default", () => {
    const onConfirm = vi.fn();
    render(<RetentionKeepDialog open {...props} onConfirm={onConfirm} />);
    expect(screen.getByText("Keep Dune?")).toBeTruthy();
    expect(screen.getByText(/stays off that rule's list/)).toBeTruthy();
    fireEvent.change(screen.getByPlaceholderText("Kids' favourite"), { target: { value: "Family night" } });
    fireEvent.click(screen.getByRole("button", { name: "Keep" }));
    expect(onConfirm).toHaveBeenCalledWith({ scope: "rule", note: "Family night" });
  });

  it("can keep it from every rule", () => {
    const onConfirm = vi.fn();
    render(<RetentionKeepDialog open {...props} onConfirm={onConfirm} />);
    fireEvent.click(screen.getByRole("radio", { name: "Every rule" }));
    expect(screen.getByText("No rule will mark it while it is kept.")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Keep" }));
    expect(onConfirm).toHaveBeenCalledWith({ scope: "all", note: "" });
  });

  it("starts over each time it opens, and cannot be pressed while busy", () => {
    const { rerender } = render(<RetentionKeepDialog open {...props} />);
    fireEvent.click(screen.getByRole("radio", { name: "Every rule" }));
    fireEvent.change(screen.getByPlaceholderText("Kids' favourite"), { target: { value: "x" } });
    rerender(<RetentionKeepDialog open={false} {...props} />);
    rerender(<RetentionKeepDialog open {...props} busy />);
    expect((screen.getByPlaceholderText("Kids' favourite") as HTMLTextAreaElement).value).toBe("");
    expect(screen.getByText(/stays off that rule's list/)).toBeTruthy();
    expect((screen.getByRole("button", { name: "Keep" }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole("button", { name: "Cancel" }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("closes on Cancel", () => {
    const onOpenChange = vi.fn();
    render(<RetentionKeepDialog open {...props} onOpenChange={onOpenChange} />);
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(onOpenChange).toHaveBeenCalledWith(false);
  });
});
