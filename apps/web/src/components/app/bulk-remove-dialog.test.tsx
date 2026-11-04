import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { removalDownloadSentence } from "../../lib/removal-download-words";
import { BulkRemoveDialog } from "./bulk-remove-dialog";

afterEach(cleanup);

const show = (over: Partial<Parameters<typeof BulkRemoveDialog>[0]> = {}) => {
  const props = { open: true, onOpenChange: vi.fn(), count: 3, mediaLabel: "film", onConfirm: vi.fn(), ...over };
  const view = render(<BulkRemoveDialog {...props} />);
  return { ...props, ...view, rerenderWith: (next: Partial<Parameters<typeof BulkRemoveDialog>[0]>) => view.rerender(<BulkRemoveDialog {...props} {...next} />) };
};

describe("the bulk remove dialog", () => {
  it("asks about the number of titles, in the plural, and describes what removal does", () => {
    show();
    expect(screen.getByRole("dialog", { name: "Remove 3 films?" })).toHaveAttribute("aria-describedby", "bulk-remove-description");
    const description = document.getElementById("bulk-remove-description")!;
    expect(description.textContent).toBe(`This removes the selected catalogue records and stops Deluno managing them. Imported files are left alone. ${removalDownloadSentence}`);
    expect(screen.getByRole("button", { name: "Remove films" })).toBeInTheDocument();
  });

  it("asks about one title in the singular", () => {
    show({ count: 1 });
    expect(screen.getByRole("dialog", { name: "Remove 1 film?" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Remove film" })).toBeInTheDocument();
  });

  it("draws nothing while it is closed", () => {
    show({ open: false });
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("says no file is deleted, and when the exclusion applies", () => {
    show();
    expect(screen.getByText("Imported files are not deleted by this bulk action.")).toBeInTheDocument();
    expect(screen.getByText("The exclusion is applied only where Deluno can identify the import list that added the title.")).toBeInTheDocument();
  });

  it("removes with the exclusion on, which is where it starts", () => {
    const props = show();
    const box = screen.getByRole("checkbox", { name: /Prevent automatic re-add/ });
    expect(box).toBeChecked();
    expect(screen.getByText(/record an exclusion before removal/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Remove films" }));
    expect(props.onConfirm).toHaveBeenCalledWith({ addImportListExclusion: true });
  });

  it("removes without the exclusion when it is turned off", () => {
    const props = show();
    fireEvent.click(screen.getByRole("checkbox", { name: /Prevent automatic re-add/ }));
    expect(screen.getByRole("checkbox", { name: /Prevent automatic re-add/ })).not.toBeChecked();
    fireEvent.click(screen.getByRole("button", { name: "Remove films" }));
    expect(props.onConfirm).toHaveBeenCalledWith({ addImportListExclusion: false });
  });

  it("starts with the exclusion on again every time it opens", () => {
    const view = show();
    fireEvent.click(screen.getByRole("checkbox", { name: /Prevent automatic re-add/ }));
    expect(screen.getByRole("checkbox", { name: /Prevent automatic re-add/ })).not.toBeChecked();
    view.rerenderWith({ open: false });
    view.rerenderWith({ open: true });
    expect(screen.getByRole("checkbox", { name: /Prevent automatic re-add/ })).toBeChecked();
  });

  it("keeps the choice while it stays open", () => {
    const view = show();
    fireEvent.click(screen.getByRole("checkbox", { name: /Prevent automatic re-add/ }));
    view.rerenderWith({ count: 5 });
    expect(screen.getByRole("checkbox", { name: /Prevent automatic re-add/ })).not.toBeChecked();
  });

  it("closes from Cancel without removing", () => {
    const props = show();
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(props.onOpenChange).toHaveBeenCalledWith(false);
    expect(props.onConfirm).not.toHaveBeenCalled();
  });

  it("says it is removing, and holds both buttons, while it works", () => {
    show({ busy: true });
    expect(screen.getByRole("button", { name: "Removing…" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Cancel" })).toBeDisabled();
    cleanup();
    show();
    expect(screen.getByRole("button", { name: "Remove films" })).not.toBeDisabled();
    expect(screen.getByRole("button", { name: "Cancel" })).not.toBeDisabled();
  });

  it("styles its Remove button as a warning", () => {
    show();
    expect(screen.getByRole("button", { name: "Remove films" }).className).toContain("bg-destructive text-destructive-foreground");
  });
});
