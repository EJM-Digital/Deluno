import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { Disclosure } from "./disclosure";

describe("the fine-tune disclosure", () => {
  it("shows its title and summary closed, and asks to open when pressed", async () => {
    const onOpenChange = vi.fn();
    render(<Disclosure title="Fine-tune" summary="Seeding and speed limits" open={false} onOpenChange={onOpenChange}><p>Inside</p></Disclosure>);
    const button = screen.getByRole("button", { name: /Fine-tune/ });
    expect(button).toHaveAttribute("aria-expanded", "false");
    expect(screen.getByText("Seeding and speed limits")).toBeVisible();
    expect(screen.queryByText("Inside")).toBeNull();
    await userEvent.click(button);
    expect(onOpenChange).toHaveBeenCalledWith(true);
  });

  it("shows what is inside when open, asks to close when pressed, and leaves out a missing summary", async () => {
    const onOpenChange = vi.fn();
    render(<Disclosure title="Fine-tune" open onOpenChange={onOpenChange}><p>Inside</p></Disclosure>);
    const button = screen.getByRole("button", { name: "Fine-tune" });
    expect(button).toHaveAttribute("aria-expanded", "true");
    expect(button).toHaveAttribute("aria-controls", screen.getByText("Inside").parentElement!.id);
    await userEvent.click(button);
    expect(onOpenChange).toHaveBeenCalledWith(false);
  });
});
