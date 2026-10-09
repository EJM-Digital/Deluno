import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { formatResetTime } from "../../lib/reset-times";
import { SaveStatus } from "./save-status";

describe("the status pill beside a one-shot action (#1181)", () => {
  it("shows a UTC reset stamp in the viewer's own time, never raw", () => {
    const stamp = "2026-10-09 03:35 UTC";
    render(<SaveStatus state="error" message={`Couldn't check for updates (GitHub limit, retry after ${stamp}).`} />);
    const shown = formatResetTime(new Date(Date.UTC(2026, 9, 9, 3, 35)));
    expect(document.body.textContent).toContain(shown);
    expect(document.body.textContent).not.toContain("UTC");
  });

  it("leaves a message without a stamp alone", () => {
    render(<SaveStatus state="saved" message="You are on the latest version." />);
    expect(screen.getByText("You are on the latest version.")).toBeInTheDocument();
  });
});
