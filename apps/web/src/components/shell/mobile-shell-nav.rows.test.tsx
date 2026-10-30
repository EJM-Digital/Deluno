import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";
import type { AttentionSnapshot } from "../../lib/use-attention";
import { emptyAttentionSummary } from "../../lib/attention-summary";
import { MobileShellNav } from "./mobile-shell-nav";

// #1329: the phone's bottom tabs: which one is marked, where the dots show, and what keeps a page clear of the bar.

const calm = {
  failedJobs: 0, failedTitleJobs: 0, failedBackgroundJobs: 0, deadLetteredJobs: 0, indexerAlerts: 0, downloadsNeedingAction: 0,
  failedImportJobs: 0, movieWanted: 0, tvWanted: 0, healthProblems: 0, total: 0, summary: emptyAttentionSummary, loading: false
} as AttentionSnapshot;

function renderAt(path: string, attention: AttentionSnapshot = calm) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <MobileShellNav attention={attention} user={{ displayName: "Sam Example", username: "sam", avatarInitials: "SE" }} onLogout={vi.fn()} />
    </MemoryRouter>
  );
}

const tabs = () => screen.getByRole("navigation", { name: "Primary navigation" });
const moreTab = () => within(tabs()).getByRole("button", { name: "More destinations" });
/** The bar marks the tab it is on with a short rule across its top; the More tab also turns its text darker. */
const marked = (element: HTMLElement) => element.querySelector("[aria-hidden].absolute.top-1") !== null;

describe("the In progress tab's dot (#1329)", () => {
  it("shows a red pulsing dot while jobs have failed, and no dot while none have", () => {
    const quiet = renderAt("/");
    const quietLink = within(tabs()).getByRole("link", { name: /In progress/ });
    expect(quietLink.querySelector(".bg-state-danger")).toBeNull();
    quiet.unmount();

    renderAt("/", { ...calm, failedJobs: 2 });
    const link = within(tabs()).getByRole("link", { name: /In progress/ });
    const dot = link.querySelector(".bg-state-danger");
    expect(dot).not.toBeNull();
    expect(dot!.className).toContain("animate-pulse");
    expect(within(tabs()).getByRole("link", { name: /Movies/ }).querySelector(".bg-state-danger")).toBeNull();
  });
});

describe("the More tab (#1329)", () => {
  it.each([
    "/collections", "/calendar", "/discover", "/indexers/indexers", "/search-cycles", "/system", "/system/updates", "/setup-guide", "/settings", "/settings/general"
  ])("is marked as the current one on %s", (path) => {
    renderAt(path);
    expect(marked(moreTab())).toBe(true);
    expect(moreTab().className).toContain("text-foreground");
  });

  it.each(["/", "/movies", "/tv", "/queue"])("is not marked on %s, where one of the four tabs is", (path) => {
    renderAt(path);
    expect(marked(moreTab())).toBe(false);
    expect(moreTab().className).not.toContain("text-foreground");
  });

  it("carries a dot when an indexer needs attention, and none otherwise", () => {
    const quiet = renderAt("/movies");
    expect(moreTab().querySelector(".bg-state-warn")).toBeNull();
    quiet.unmount();

    renderAt("/movies", { ...calm, indexerAlerts: 1 });
    expect(moreTab().querySelector(".bg-state-warn")).not.toBeNull();
  });
});

describe("pages end above the fixed tab bar (#1329)", () => {
  const css = readFileSync(resolve(__dirname, "../../index.css"), "utf8");
  const layout = readFileSync(resolve(__dirname, "../../layouts/app-layout.tsx"), "utf8");

  it("the page content carries the bar's height as bottom padding on a phone and none from the large width up", () => {
    expect(layout).toMatch(/className="min-w-0 pb-mobile-tabbar lg:ml-\(--sidebar-width\) lg:pb-0"/);
    expect(css).toMatch(/--spacing-mobile-tabbar:\s*var\(--mobile-tabbar-height\)/);
  });

  it("the bar is as tall as the padding that clears it, and is hidden from the large width up", () => {
    renderAt("/");
    expect(tabs().getAttribute("style")).toContain("var(--mobile-tabbar-height)");
    expect(tabs().className).toContain("lg:hidden");
    expect(tabs().className).toContain("fixed");
    expect(css).toMatch(/--mobile-tabbar-height:\s*56px/);
  });
});
