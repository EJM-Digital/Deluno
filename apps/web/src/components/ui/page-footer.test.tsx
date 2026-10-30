import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { PageFooter } from "./page-footer";

describe("the pinned footer of a page-level form (#1329)", () => {
  it.each([
    ["dirty", "Unsaved changes", true],
    ["saving", "Saving…", false],
    ["saved", "Saved just now", false],
    ["error", "Could not save", true]
  ] as const)("in the %s state it says %s and Save is %s", (state, label, enabled) => {
    render(<PageFooter state={state} saveLabel="Save changes" />);

    expect(screen.getByRole("status")).toHaveTextContent(label);
    const save = screen.getByRole("button", { name: "Save changes" });
    if (enabled) expect(save).toBeEnabled();
    else expect(save).toBeDisabled();
  });

  it("a clean form says nothing unless it has a message, and Save is off", () => {
    const { rerender } = render(<PageFooter state="clean" saveLabel="Save safeguards" />);
    expect(screen.getByRole("status")).toBeEmptyDOMElement();
    expect(screen.getByRole("button", { name: "Save safeguards" })).toBeDisabled();

    rerender(<PageFooter state="clean" message="Nothing to save yet." saveLabel="Save safeguards" />);
    expect(screen.getByRole("status")).toHaveTextContent("Nothing to save yet.");
  });

  it("the server's message replaces the stock words after a save or a failure, and a button-type Save calls onSave", () => {
    const onSave = vi.fn();
    const { rerender } = render(<PageFooter state="error" message="The folder is missing." saveLabel="Save import settings" saveType="button" onSave={onSave} />);
    expect(screen.getByRole("status")).toHaveTextContent("The folder is missing.");

    fireEvent.click(screen.getByRole("button", { name: "Save import settings" }));
    expect(onSave).toHaveBeenCalledTimes(1);

    rerender(<PageFooter state="saved" message="Saved at 10:42." saveLabel="Save import settings" saveType="button" onSave={onSave} />);
    expect(screen.getByRole("status")).toHaveTextContent("Saved at 10:42.");
  });
});
