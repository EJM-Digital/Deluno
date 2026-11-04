import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";
import type { AttentionSnapshot } from "../../lib/use-attention";
import { emptyAttentionSummary } from "../../lib/attention-summary";
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
  summary: emptyAttentionSummary,
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

describe("signing out from the phone menu's sheet", () => {
  it("signs out and closes the sheet", async () => {
    const onLogout = vi.fn();
    render(
      <MemoryRouter>
        <MobileShellNav attention={attention} user={{ displayName: "Sam Example", username: "sam", avatarInitials: "SE" }} onLogout={onLogout} />
      </MemoryRouter>
    );
    fireEvent.click(screen.getByRole("button", { name: /more/i }));
    expect(screen.getByText("Sam Example")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Sign out" }));

    expect(onLogout).toHaveBeenCalledTimes(1);
    await waitFor(() => expect(screen.queryByText("Sam Example")).toBeNull());
  });
});

describe("the phone menu's areas", () => {
  it("every area keeps its pages as tabs, so the sheet draws no expand or collapse chevron", () => {
    render(
      <MemoryRouter>
        <MobileShellNav attention={attention} user={{ displayName: "Sam Example", username: "sam", avatarInitials: "SE" }} onLogout={vi.fn()} />
      </MemoryRouter>
    );
    fireEvent.click(screen.getByRole("button", { name: /more/i }));
    expect(screen.queryAllByRole("button", { name: /^(Expand|Collapse) / })).toHaveLength(0);
  });
});

describe("the phone menu, with and without a signed-in person, and inside an area", () => {
  const open = (path: string, user: { displayName: string; username: string; avatarInitials?: string } | null = null) => {
    render(
      <MemoryRouter initialEntries={[path]}>
        <MobileShellNav attention={attention} user={user} onLogout={vi.fn()} />
      </MemoryRouter>
    );
    fireEvent.click(screen.getByRole("button", { name: /more/i }));
  };

  it("shows no account line when nobody is known, but still offers Sign out", () => {
    open("/");
    expect(screen.queryByText("Sam Example")).toBeNull();
    expect(screen.getByRole("button", { name: "Sign out" })).toBeTruthy();
  });

  it("shows DU in the avatar when the person has no initials", () => {
    open("/", { displayName: "Sam Example", username: "sam" });
    expect(screen.getByText("DU")).toBeTruthy();
  });

  it("shows the person's own initials in the avatar", () => {
    open("/", { displayName: "Sam Example", username: "sam", avatarInitials: "SE" });
    expect(screen.getByText("SE")).toBeTruthy();
  });

  it("marks the area the person is in, and no other, with the accent colour", () => {
    open("/settings/libraries");
    const area = screen.getByRole("link", { name: "Media Management" });
    expect(area.className).toContain("text-(--nav-accent)");
    expect(area.className).not.toContain("text-muted-foreground");
    expect(screen.getByRole("link", { name: "Quality & Release" }).className).toContain("text-muted-foreground");
  });
});
