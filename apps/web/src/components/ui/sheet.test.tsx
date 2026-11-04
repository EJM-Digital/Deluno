import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { Sheet, SheetClose, SheetContent, SheetTrigger } from "./sheet";

function renderSheet(content: Parameters<typeof SheetContent>[0]) {
  render(
    <Sheet defaultOpen>
      <SheetContent {...content}>
        <p>Inside the panel</p>
      </SheetContent>
    </Sheet>
  );
  return screen.getByRole("dialog");
}

describe("a side panel", () => {
  it("is called Panel and describes itself as details and actions when it has no name", () => {
    const dialog = renderSheet({});
    expect(dialog).toHaveAccessibleName("Panel");
    expect(dialog).toHaveAccessibleDescription("Details and actions.");
    expect(screen.getByText("Inside the panel")).toBeInTheDocument();
  });

  it("is called by its label and says it holds that label's details", () => {
    const dialog = renderSheet({ "aria-label": "Heat" });
    expect(dialog).toHaveAccessibleName("Heat");
    expect(dialog).toHaveAccessibleDescription("Details and actions for Heat.");
  });

  it("falls back to Panel when its label is only spaces, or not words", () => {
    expect(renderSheet({ "aria-label": "   " })).toHaveAccessibleName("Panel");
  });

  it("uses a description it was given in place of the generated one", () => {
    const dialog = renderSheet({ "aria-label": "Heat", description: "Edit how Heat is searched for." });
    expect(dialog).toHaveAccessibleDescription("Edit how Heat is searched for.");
  });

  it("slides in from the right by default", () => {
    expect(renderSheet({})).toHaveClass("right-0", "border-l");
  });

  it("sits against the left edge when it comes in from the left", () => {
    expect(renderSheet({ side: "left" })).toHaveClass("left-0", "border-r");
  });

  it("floats near the bottom of a small screen with its close button tucked in the corner", () => {
    const dialog = renderSheet({ side: "bottom" });
    expect(dialog).toHaveClass("rounded-2xl", "inset-x-0");
    expect(dialog).not.toHaveClass("right-0");
    expect(screen.getByRole("button", { name: "Close" })).toHaveClass("right-2", "top-2.5");
  });

  it("puts the close button at the top right of a side panel", () => {
    renderSheet({});
    expect(screen.getByRole("button", { name: "Close" })).toHaveClass("right-4", "top-4");
  });

  it("closes from its close button", async () => {
    renderSheet({});
    await userEvent.click(screen.getByRole("button", { name: "Close" }));
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("opens from its trigger and closes from a SheetClose inside it", async () => {
    render(
      <Sheet>
        <SheetTrigger>Open it</SheetTrigger>
        <SheetContent aria-label="Heat"><SheetClose>Done here</SheetClose></SheetContent>
      </Sheet>
    );
    expect(screen.queryByRole("dialog")).toBeNull();
    await userEvent.click(screen.getByRole("button", { name: "Open it" }));
    await userEvent.click(await screen.findByRole("button", { name: "Done here" }));
    expect(screen.queryByRole("dialog")).toBeNull();
  });
});
