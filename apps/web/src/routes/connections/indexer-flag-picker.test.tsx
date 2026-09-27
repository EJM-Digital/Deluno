import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { IndexerFlagPicker } from "./indexer-flag-picker";

describe("IndexerFlagPicker (#745)", () => {
  it("shows a chip for every known flag, pressed only when already selected", () => {
    render(<IndexerFlagPicker value="Freeleech" onChange={() => undefined} />);
    expect(screen.getByRole("button", { name: "Freeleech" })).toHaveAttribute("aria-pressed", "true");
    expect(screen.getByRole("button", { name: "Internal" })).toHaveAttribute("aria-pressed", "false");
  });

  it("adds the flag when an unselected chip is clicked", () => {
    const onChange = vi.fn();
    render(<IndexerFlagPicker value="" onChange={onChange} />);
    fireEvent.click(screen.getByRole("button", { name: "Freeleech" }));
    expect(onChange).toHaveBeenCalledWith("Freeleech");
  });

  it("removes the flag when a selected chip is clicked", () => {
    const onChange = vi.fn();
    render(<IndexerFlagPicker value="Freeleech, Internal" onChange={onChange} />);
    fireEvent.click(screen.getByRole("button", { name: "Freeleech" }));
    expect(onChange).toHaveBeenCalledWith("Internal");
  });
});
