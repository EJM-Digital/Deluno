import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter, useLocation } from "react-router-dom";
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

function Where() {
  const location = useLocation();
  return <output data-testid="where">{location.pathname}</output>;
}

function renderAt(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <MobileShellNav attention={attention} user={{ displayName: "Sam Example", username: "sam", avatarInitials: "SE" }} onLogout={vi.fn()} />
      <Where />
    </MemoryRouter>
  );
}

const where = () => screen.getByTestId("where").textContent;

describe("the phone's four bottom tabs", () => {
  it("each tab goes to its screen and the tab for the screen you are on is marked as the current one", () => {
    renderAt("/");
    const tabs = screen.getByRole("navigation", { name: "Primary navigation" });

    const destinations: Array<[string, string]> = [["Movies", "/movies"], ["TV", "/tv"], ["Transfers", "/queue"], ["Dashboard", "/"]];
    for (const [name, path] of destinations) {
      fireEvent.click(within(tabs).getByRole("link", { name }));
      expect(where()).toBe(path);
      expect(within(tabs).getByRole("link", { name }).getAttribute("aria-current")).toBe("page");
      // Exactly one tab is marked: the others are not.
      for (const [other] of destinations.filter(([n]) => n !== name)) {
        expect(within(tabs).getByRole("link", { name: other }).getAttribute("aria-current")).toBeNull();
      }
    }
  });

  it("the Dashboard tab is not marked while you are on another screen, though every address starts with a slash", () => {
    renderAt("/movies");
    const tabs = screen.getByRole("navigation", { name: "Primary navigation" });
    expect(within(tabs).getByRole("link", { name: "Dashboard" }).getAttribute("aria-current")).toBeNull();
    expect(within(tabs).getByRole("link", { name: "Movies" }).getAttribute("aria-current")).toBe("page");
  });
});

describe("the More sheet", () => {
  const groups: Array<[string, string[]]> = [
    ["Your Media", ["Collections", "Schedule", "Discover", "Search"]],
    ["Live operations", ["Activity"]]
  ];

  it("lists Your Media and Live operations with their destinations, then Setup and Deluno", () => {
    renderAt("/");
    fireEvent.click(screen.getByRole("button", { name: "More destinations" }));
    const sheet = screen.getByRole("navigation", { name: "Additional destinations" });

    for (const [group, links] of groups) {
      expect(within(sheet).getByText(group)).toBeTruthy();
      for (const link of links) expect(within(sheet).getByRole("link", { name: link })).toBeTruthy();
    }
    expect(within(sheet).getByText("Setup")).toBeTruthy();
    expect(within(sheet).getByText("Deluno")).toBeTruthy();
  });

  it("choosing a destination from each group goes there and closes the sheet", async () => {
    const choices: Array<[string, string]> = [["Collections", "/collections"], ["Schedule", "/calendar"], ["Discover", "/discover"], ["Search", "/search"], ["Activity", "/activity"]];
    for (const [name, path] of choices) {
      const { unmount } = renderAt("/");
      fireEvent.click(screen.getByRole("button", { name: "More destinations" }));
      fireEvent.click(within(screen.getByRole("navigation", { name: "Additional destinations" })).getByRole("link", { name }));

      expect(where()).toBe(path);
      await waitFor(() => expect(screen.queryByRole("navigation", { name: "Additional destinations" })).toBeNull());
      unmount();
    }
  });

  it("a destination under Setup and one under Deluno also go there and close the sheet", async () => {
    const { unmount } = renderAt("/");
    fireEvent.click(screen.getByRole("button", { name: "More destinations" }));
    const sheet = screen.getByRole("navigation", { name: "Additional destinations" });
    const links = within(sheet).getAllByRole("link").map((a) => a.getAttribute("href") ?? "");
    const setup = links.find((href) => href.startsWith("/settings") || href.startsWith("/indexers"));
    const deluno = links.find((href) => href.startsWith("/system"));
    expect(setup).toBeTruthy();
    expect(deluno).toBeTruthy();

    fireEvent.click(within(sheet).getAllByRole("link").find((a) => a.getAttribute("href") === setup)!);
    expect(where()).toBe(setup!.split("?")[0]);
    await waitFor(() => expect(screen.queryByRole("navigation", { name: "Additional destinations" })).toBeNull());
    unmount();

    renderAt("/");
    fireEvent.click(screen.getByRole("button", { name: "More destinations" }));
    fireEvent.click(within(screen.getByRole("navigation", { name: "Additional destinations" })).getAllByRole("link").find((a) => a.getAttribute("href") === deluno)!);
    expect(where()).toBe(deluno!.split("?")[0]);
    await waitFor(() => expect(screen.queryByRole("navigation", { name: "Additional destinations" })).toBeNull());
  });
});
