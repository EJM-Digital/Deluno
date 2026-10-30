import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { GlossaryModal } from "./glossary-modal";

describe("the glossary", () => {
  it("draws nothing while closed", () => {
    const { container } = render(<GlossaryModal open={false} onOpenChange={vi.fn()} />);
    expect(container).toBeEmptyDOMElement();
  });

  it("explains the terms and closes from the X, the backdrop and the Close button", () => {
    const onOpenChange = vi.fn();
    const { container } = render(<GlossaryModal open onOpenChange={onOpenChange} />);
    expect(screen.getByRole("dialog", { name: "Glossary" })).toBeTruthy();
    expect(screen.getByText("Monitored")).toBeTruthy();
    expect(screen.getByText(/Deluno stops searching for upgrades/)).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Close" }));
    fireEvent.click(container.querySelector("button:not([class*='h-8'])") as HTMLElement);
    fireEvent.click(container.querySelector(".backdrop-blur-\\[2px\\]") as HTMLElement);
    expect(onOpenChange).toHaveBeenCalledTimes(3);
    expect(onOpenChange).toHaveBeenCalledWith(false);
  });
});
