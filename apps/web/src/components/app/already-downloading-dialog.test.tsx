import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { AlreadyDownloadingDialog } from "./already-downloading-prompt";

describe("the already-downloading question", () => {
  it("shows the message and lets the person replace the first download or send as well", () => {
    const onChoose = vi.fn();
    render(<AlreadyDownloadingDialog prompt={{ message: "Dune is already downloading." }} busy={false} onChoose={onChoose} onCancel={vi.fn()} />);
    expect(screen.getByText("Dune is already downloading.")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Replace it" }));
    fireEvent.click(screen.getByRole("button", { name: "Send as well" }));
    expect(onChoose.mock.calls).toEqual([["replace"], ["send-as-well"]]);
  });

  it("cancels when the dialog is dismissed, and shows nothing without a question", () => {
    const onCancel = vi.fn();
    const { rerender } = render(<AlreadyDownloadingDialog prompt={{ message: "m" }} busy={false} onChoose={vi.fn()} onCancel={onCancel} />);
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(onCancel).toHaveBeenCalled();
    rerender(<AlreadyDownloadingDialog prompt={null} busy={false} onChoose={vi.fn()} onCancel={onCancel} />);
    expect(screen.queryByText("Already downloading")).toBeNull();
  });
});
