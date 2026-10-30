import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { RemovalReasonDialog } from "./removal-reason-dialog";

function open(props: Partial<React.ComponentProps<typeof RemovalReasonDialog>> = {}) {
  const onOpenChange = vi.fn();
  const onChoose = vi.fn();
  render(<RemovalReasonDialog open onOpenChange={onOpenChange} title="Arrival" busyReason={null} onChoose={onChoose} {...props} />);
  return { onOpenChange, onChoose };
}

describe("asking why a download is being removed", () => {
  it("names the title, offers the three reasons, and reports the one picked", async () => {
    const { onChoose } = open();
    expect(screen.getByRole("dialog")).toHaveTextContent("Why remove “Arrival”?");
    await userEvent.click(screen.getByRole("button", { name: "Remove “Arrival”: I don't want it" }));
    expect(onChoose).toHaveBeenCalledWith("unwanted");
    expect(screen.getByRole("button", { name: "Remove “Arrival”: It's bad" })).toBeEnabled();
    expect(screen.getByRole("button", { name: "Remove “Arrival”: Just stop tracking it" })).toBeEnabled();
  });

  it("can be cancelled while nothing is running", async () => {
    const { onOpenChange } = open();
    await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(onOpenChange).toHaveBeenCalledWith(false);
  });

  it("cannot be closed or chosen from again while a removal is running", async () => {
    const { onOpenChange, onChoose } = open({ busyReason: "failed" });
    expect(screen.getByRole("button", { name: "Cancel" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Remove “Arrival”: It's bad" })).toBeDisabled();
    await userEvent.keyboard("{Escape}");
    expect(onOpenChange).not.toHaveBeenCalled();
    await userEvent.click(screen.getByRole("button", { name: "Remove “Arrival”: I don't want it" }));
    expect(onChoose).not.toHaveBeenCalled();
  });
});
