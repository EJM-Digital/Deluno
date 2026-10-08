import { fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";
import type { AttentionSnapshot } from "../../lib/use-attention";
import { computeAttention } from "../../routes/dashboard/attention";
import { MobileShellNav } from "./mobile-shell-nav";

const attention = {
  failedJobs: 0,
  failedTitleJobs: 0,
  failedBackgroundJobs: 0,
  deadLetteredJobs: 0,
  indexerAlerts: 0,
  downloadsNeedingAction: 0,
  failedImportJobs: 0,
  movieWanted: 0,
  tvWanted: 0,
  healthProblems: 0,
  total: 0,
  needs: computeAttention(undefined),
  loading: false
} as AttentionSnapshot;

/** #1149: the phone menu names the person on one line, the way the desktop account button does. */
describe("the phone menu's account line", () => {
  it("shows the name only, with no second @username line", () => {
    render(
      <MemoryRouter>
        <MobileShellNav attention={attention} user={{ displayName: "Sam Example", username: "sam", avatarInitials: "SE" }} onLogout={vi.fn()} />
      </MemoryRouter>
    );
    fireEvent.click(screen.getByRole("button", { name: /more/i }));
    expect(screen.getByText("Sam Example")).toBeTruthy();
    expect(screen.queryByText("@sam")).toBeNull();
  });
});
