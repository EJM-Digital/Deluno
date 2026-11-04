import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { Button } from "./button";
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

  it("styles its buttons exactly as the button component would with the same size, variant and layout classes", () => {
    const wide = "w-full order-X h-auto min-h-(--control-height-lg) justify-center whitespace-normal px-5 py-3 text-center";
    const buttonClass = (size: "lg" | "sm", variant: "destructive-solid" | "default" | "secondary" | "outline" | "destructive", className: string) => {
      const { container } = render(<Button size={size} variant={variant} className={className}>x</Button>);
      const result = container.firstElementChild!.className;
      cleanup();
      return result;
    };
    const expectedDecision = [buttonClass("lg", "destructive-solid", wide.replace("X", "1")), buttonClass("lg", "secondary", wide.replace("X", "3")), buttonClass("lg", "outline", wide.replace("X", "2"))];
    const expectedPlain = [buttonClass("sm", "default", "w-full sm:w-auto"), buttonClass("sm", "secondary", "w-full sm:w-auto"), buttonClass("sm", "destructive", "w-full sm:w-auto")];
    show({ secondaryLabel: "Other", onSecondary: vi.fn() });
    expect([screen.getByRole("button", { name: "Confirm" }), screen.getByRole("button", { name: "Cancel" }), screen.getByRole("button", { name: "Other" })].map((button) => button.className)).toEqual(expectedDecision);
    cleanup();
    show({ presentation: "default", confirmVariant: "default", secondaryLabel: "Other", secondaryVariant: "destructive", onSecondary: vi.fn() });
    expect([screen.getByRole("button", { name: "Confirm" }), screen.getByRole("button", { name: "Cancel" }), screen.getByRole("button", { name: "Other" })].map((button) => button.className)).toEqual(expectedPlain);
  });

  it("spaces and sizes its heading and words by presentation", () => {
    show();
    expect(screen.getByText("Remove it?").className).toContain("text-(length:--type-title-md)");
    expect(screen.getByText("Remove it?").className).toContain("font-semibold text-foreground");
    expect(screen.getByText("This takes it away.").className).toContain("max-w-prose");
    expect(screen.getByText("This takes it away.").className).toContain("mt-1 text-muted-foreground");
    expect(screen.getByText("Remove it?").parentElement!.parentElement!.className).toContain("mb-8");
    cleanup();
    show({ presentation: "default" });
    expect(screen.getByText("Remove it?").className).not.toContain("text-(length:--type-title-md)");
    expect(screen.getByText("This takes it away.").className).not.toContain("max-w-prose");
    expect(screen.getByText("This takes it away.").className).toContain("mt-1 text-sm text-muted-foreground leading-relaxed");
    expect(screen.getByText("Remove it?").parentElement!.parentElement!.className).not.toContain("mb-8");
    expect(screen.getByText("Remove it?").parentElement!.parentElement!.className).toContain("mb-4");
  });
});
