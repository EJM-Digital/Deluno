import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { TooltipProvider } from "./tooltip";

// #1329: the app wraps its screens in one tooltip provider.
describe("the tooltip provider", () => {
  it("shows the screens it wraps", () => {
    render(<TooltipProvider delayDuration={400}><p>the app</p></TooltipProvider>);
    expect(screen.getByText("the app")).toBeTruthy();
  });
});
