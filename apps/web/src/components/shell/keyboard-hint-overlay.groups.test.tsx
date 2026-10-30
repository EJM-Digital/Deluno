import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { KeyboardHintOverlay } from "./keyboard-hint-overlay";

describe("the keyboard shortcut reference groups", () => {
  it("puts shortcuts with no group under General and keeps those sharing a group together", () => {
    render(
      <KeyboardHintOverlay
        open
        onOpenChange={() => {}}
        shortcuts={[
          { keys: ["?"], label: "Show shortcuts" },
          { keys: ["g", "d"], label: "Go to Dashboard", group: "Go to" },
          { keys: ["g", "m"], label: "Go to Movies", group: "Go to" },
          { keys: ["/"], label: "Focus search" }
        ]}
      />
    );
    expect(screen.getAllByText("General")).toHaveLength(1);
    expect(screen.getAllByText("Go to")).toHaveLength(1);
    for (const label of ["Show shortcuts", "Go to Dashboard", "Go to Movies", "Focus search"]) expect(screen.getByText(label)).toBeTruthy();
  });
});
