import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ConfirmDialog } from "./confirm-dialog";

afterEach(cleanup);

const show = (overrides: Partial<Parameters<typeof ConfirmDialog>[0]> = {}) => {
  const props = { open: true, onOpenChange: vi.fn(), title: "Remove it?", description: "This takes it away.", onConfirm: vi.fn(), ...overrides };
  render(<ConfirmDialog {...props} />);
  return props;
};

describe("the confirmation dialog", () => {
  it("is named by its title and describes itself with its words", () => {
    show();
    const dialog = screen.getByRole("dialog", { name: "Remove it?" });
    expect(dialog).toHaveAttribute("aria-describedby", "confirm-description");
    expect(screen.getByText("This takes it away.")).toHaveAttribute("id", "confirm-description");
  });

  it("draws nothing while it is closed", () => {
    show({ open: false });
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("confirms with the button it is given a label for, and says Confirm when it is given none", () => {
    const props = show({ confirmLabel: "Remove" });
    fireEvent.click(screen.getByRole("button", { name: "Remove" }));
    expect(props.onConfirm).toHaveBeenCalledTimes(1);
    cleanup();
    show();
    expect(screen.getByRole("button", { name: "Confirm" })).toBeInTheDocument();
  });

  it("closes from Cancel without confirming", () => {
    const props = show();
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(props.onOpenChange).toHaveBeenCalledWith(false);
    expect(props.onConfirm).not.toHaveBeenCalled();
  });

  it("warns with a triangle when the confirmation is destructive, and not when it is plain", () => {
    show();
    expect(screen.getByRole("dialog").querySelector("svg")).not.toBeNull();
    cleanup();
    show({ confirmVariant: "default" });
    expect(screen.getByRole("dialog").querySelector("svg")).toBeNull();
  });

  it("offers a second way forward only when it has both a label and something to do", () => {
    const onSecondary = vi.fn();
    show({ secondaryLabel: "Keep files", onSecondary });
    fireEvent.click(screen.getByRole("button", { name: "Keep files" }));
    expect(onSecondary).toHaveBeenCalledTimes(1);
    cleanup();
    show({ secondaryLabel: "Keep files" });
    expect(screen.queryByRole("button", { name: "Keep files" })).toBeNull();
    cleanup();
    show({ onSecondary });
    expect(screen.getAllByRole("button")).toHaveLength(2);
  });

  it("holds every button while it is busy", () => {
    show({ busy: true, secondaryLabel: "Keep files", onSecondary: vi.fn(), confirmLabel: "Remove" });
    for (const name of ["Cancel", "Keep files", "Remove"]) expect(screen.getByRole("button", { name })).toBeDisabled();
    cleanup();
    show({ secondaryLabel: "Keep files", onSecondary: vi.fn(), confirmLabel: "Remove" });
    for (const name of ["Cancel", "Keep files", "Remove"]) expect(screen.getByRole("button", { name })).not.toBeDisabled();
  });

  it("shows extra content between the words and the buttons only when it is given some", () => {
    show({ extraContent: <label><input type="checkbox" /> Also delete files</label> });
    expect(screen.getByRole("checkbox", { name: "Also delete files" })).toBeInTheDocument();
    cleanup();
    show();
    expect(screen.queryByRole("checkbox")).toBeNull();
  });

  it("makes a decision large with its buttons stacked, and a plain confirmation small with its buttons in a row", () => {
    show();
    expect(screen.getByRole("dialog").className).toContain("max-w-xl");
    expect(screen.getByRole("button", { name: "Confirm" }).className).toContain("order-1");
    expect(screen.getByRole("button", { name: "Cancel" }).className).toContain("order-3");
    expect(screen.getByRole("button", { name: "Cancel" }).parentElement!.className).toContain("flex-col gap-3");
    cleanup();
    show({ presentation: "default", secondaryLabel: "Other", onSecondary: vi.fn() });
    expect(screen.getByRole("dialog").className).toContain("max-w-sm");
    expect(screen.getByRole("button", { name: "Confirm" }).className).not.toContain("order-1");
    expect(screen.getByRole("button", { name: "Confirm" }).className).toContain("sm:w-auto");
    expect(screen.getByRole("button", { name: "Cancel" }).className).toContain("sm:w-auto");
    expect(screen.getByRole("button", { name: "Other" }).className).toContain("sm:w-auto");
    expect(screen.getByRole("button", { name: "Other" }).className).not.toContain("order-2");
    expect(screen.getByRole("button", { name: "Cancel" }).parentElement!.className).toContain("sm:justify-end");
  });

  it("uses the destructive style for the secondary button when asked", () => {
    show({ secondaryLabel: "Delete", onSecondary: vi.fn(), secondaryVariant: "destructive" });
    const destructive = screen.getByRole("button", { name: "Delete" }).className;
    cleanup();
    show({ secondaryLabel: "Delete", onSecondary: vi.fn() });
    expect(screen.getByRole("button", { name: "Delete" }).className).not.toBe(destructive);
  });

  it("styles the title and words larger for a decision than for a plain confirmation", () => {
    show();
    const decisionTitle = screen.getByText("Remove it?").className;
    const decisionWords = screen.getByText("This takes it away.").className;
    cleanup();
    show({ presentation: "default" });
    expect(screen.getByText("Remove it?").className).not.toBe(decisionTitle);
    expect(screen.getByText("This takes it away.").className).not.toBe(decisionWords);
  });
});
