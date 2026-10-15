import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { SIDEBAR_COLLAPSED_KEY, useSidebarCollapsed } from "../lib/use-sidebar-collapsed";
import { DesktopSidebar } from "./app-layout";

vi.mock("../lib/use-auth", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/use-auth")>()),
  useAuth: () => ({ changePassword: vi.fn(), token: "t" })
}));
vi.mock("../lib/use-host-name", () => ({ useHostName: () => "WORKSTATION", hostTitle: () => "Deluno" }));

const attention = {
  movieWanted: 0, tvWanted: 0, indexerAlerts: 0, failedJobs: 0, downloadsNeedingAction: 7
} as unknown as Parameters<typeof DesktopSidebar>[0]["attention"];
const user = { id: "1", username: "ann", displayName: "Ann Lee", avatarInitials: "AL" } as Parameters<typeof DesktopSidebar>[0]["user"];

/** The sidebar as the layout drives it: the hook owns the choice. */
function Harness() {
  const sidebar = useSidebarCollapsed();
  return (
    <MemoryRouter>
      <DesktopSidebar attention={attention} user={user} onLogout={() => undefined} collapsed={sidebar.collapsed} onToggleCollapsed={sidebar.toggle} />
    </MemoryRouter>
  );
}

describe("the sidebar collapse button", () => {
  beforeEach(() => window.localStorage.clear());

  it("shows the words, then only icons with the names kept, and says how to come back", async () => {
    render(<Harness />);
    expect(screen.getByRole("link", { name: /^Movies$/ }).textContent).toContain("Movies");
    expect(screen.getByRole("button", { name: "Collapse navigation" })).toHaveAttribute("aria-expanded", "true");

    await userEvent.click(screen.getByRole("button", { name: "Collapse navigation" }));

    const movies = screen.getByRole("link", { name: "Movies" });
    expect(movies.textContent).toBe("");
    expect(movies).toHaveAttribute("title", "Movies");
    // A count rides on the icon and is still said aloud.
    expect(screen.getByRole("link", { name: "Transfers, 7" })).toHaveAttribute("title", "Transfers");
    expect(screen.queryByText("Deluno on WORKSTATION", { exact: false })).toBeNull();
    expect(screen.getByRole("button", { name: "Expand navigation" })).toHaveAttribute("aria-expanded", "false");
    expect(screen.getByRole("button", { name: /Ann Lee, account menu/ })).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Expand navigation" }));
    expect(screen.getByRole("link", { name: /^Movies$/ }).textContent).toContain("Movies");
  });

  it("remembers the choice in this browser", async () => {
    const first = render(<Harness />);
    await userEvent.click(screen.getByRole("button", { name: "Collapse navigation" }));
    expect(window.localStorage.getItem(SIDEBAR_COLLAPSED_KEY)).toBe("true");
    first.unmount();

    render(<Harness />);
    expect(screen.getByRole("button", { name: "Expand navigation" })).toBeInTheDocument();
  });

  it("still works when the browser will not store anything", async () => {
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => { throw new Error("blocked"); });
    vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => { throw new Error("blocked"); });
    render(<Harness />);
    await userEvent.click(screen.getByRole("button", { name: "Collapse navigation" }));
    expect(screen.getByRole("button", { name: "Expand navigation" })).toBeInTheDocument();
    vi.restoreAllMocks();
  });

  // #1149: the handle and "Deluno on <machine>" only repeated the name and the computer name shown above.
  it("names the person once on the account button and the computer once at the top", () => {
    render(<Harness />);
    expect(screen.getByText("Ann Lee")).toBeInTheDocument();
    expect(screen.queryByText(/@ann/)).toBeNull();
    expect(screen.queryByText(/Deluno on WORKSTATION/)).toBeNull();
    expect(screen.getAllByText("WORKSTATION")).toHaveLength(1);
  });
});

describe("the account menu", () => {
  beforeEach(() => window.localStorage.clear());

  it("Sign out in the account menu signs the person out", async () => {
    const onLogout = vi.fn();
    render(
      <MemoryRouter>
        <DesktopSidebar attention={attention} user={user} onLogout={onLogout} collapsed={false} onToggleCollapsed={() => undefined} />
      </MemoryRouter>
    );

    await userEvent.click(screen.getByRole("button", { name: "Sign out" }));

    expect(onLogout).toHaveBeenCalledTimes(1);
  });
});
