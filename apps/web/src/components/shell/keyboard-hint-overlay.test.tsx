import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { KeyboardHintOverlay } from "./keyboard-hint-overlay";

describe("KeyboardHintOverlay", () => {
  it("describes itself as a reference and never promises a search it does not have", () => {
    render(
      <KeyboardHintOverlay
        open
        onOpenChange={() => {}}
        shortcuts={[{ keys: ["g", "d"], label: "Go to Dashboard", group: "Go to" }]}
      />
    );
    const dialog = screen.getByRole("dialog");
    const description = dialog.getAttribute("aria-describedby");
    const text = document.getElementById(description ?? "")?.textContent ?? "";
    expect(text).toMatch(/reference of Deluno keyboard shortcuts/);
    expect(text).not.toMatch(/search/i);
    expect(screen.queryByRole("searchbox")).toBeNull();
    expect(screen.queryByRole("textbox")).toBeNull();
    expect(screen.getByText("Go to Dashboard")).toBeInTheDocument();
  });
});
