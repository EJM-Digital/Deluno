import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { DesktopSidebar } from "./app-layout";

// The sidebar gets a stand-in sign-in context below; the sign-out and session tests use the real module.
const { applyRenewedToken, authedFetch, AuthProvider, readSignOutReason, useAuth } = await vi.importActual<typeof import("../lib/use-auth")>("../lib/use-auth");

/**
 * The account menu (#1294): Change password, Sign out, and what the open tab does when a password change
 * ends its session. The sidebar is driven with a changePassword that answers the way the server does.
 */

const changePasswordMock = vi.hoisted(() => vi.fn());
vi.mock("../lib/use-auth", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/use-auth")>()),
  useAuth: () => ({ changePassword: changePasswordMock, token: "t" })
}));
vi.mock("../lib/use-host-name", () => ({ useHostName: () => "WORKSTATION", hostTitle: () => "Deluno" }));

const attention = {
  movieWanted: 0, tvWanted: 0, indexerAlerts: 0, failedJobs: 0, downloadsNeedingAction: 0
} as unknown as Parameters<typeof DesktopSidebar>[0]["attention"];
const user = { id: "1", username: "ann", displayName: "Ann Lee", avatarInitials: "AL" } as Parameters<typeof DesktopSidebar>[0]["user"];

function renderSidebar(onLogout: () => void = () => undefined) {
  return render(
    <MemoryRouter>
      <DesktopSidebar attention={attention} user={user} onLogout={onLogout} collapsed={false} onToggleCollapsed={() => undefined} />
    </MemoryRouter>
  );
}

function openChangePassword() {
  fireEvent.click(screen.getByRole("button", { name: "Change password" }));
  return screen.getByRole("heading", { name: "Change password" }).closest("form") as HTMLFormElement;
}

function fill(form: HTMLFormElement, current: string, next: string, confirm: string) {
  const [currentBox, nextBox, confirmBox] = Array.from(form.querySelectorAll("input[type=password]")) as HTMLInputElement[];
  fireEvent.change(currentBox!, { target: { value: current } });
  fireEvent.change(nextBox!, { target: { value: next } });
  fireEvent.change(confirmBox!, { target: { value: confirm } });
  return [currentBox!, nextBox!, confirmBox!];
}

describe("Change password", () => {
  afterEach(() => changePasswordMock.mockReset());

  it("opens a dialog with current, new and confirm boxes, and Save password stays off until all three are filled", () => {
    renderSidebar();
    const form = openChangePassword();
    expect(form.querySelectorAll("input[type=password]")).toHaveLength(3);
    expect(screen.getByText("Current password")).toBeInTheDocument();
    expect(screen.getByText("New password")).toBeInTheDocument();
    expect(screen.getByText("Confirm new password")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Save password" })).toBeDisabled();

    fill(form, "old", "new-one-123", "");
    expect(screen.getByRole("button", { name: "Save password" })).toBeDisabled();
    fill(form, "old", "new-one-123", "new-one-123");
    expect(screen.getByRole("button", { name: "Save password" })).not.toBeDisabled();
  });

  it("a mismatch says the new passwords do not match, before any request is made", () => {
    renderSidebar();
    const form = openChangePassword();
    fill(form, "old", "new-one-123", "different-123");

    fireEvent.click(screen.getByRole("button", { name: "Save password" }));

    expect(screen.getByText("New passwords do not match.")).toBeInTheDocument();
    expect(changePasswordMock).not.toHaveBeenCalled();
  });

  it("a correct change says Password changed, clears the fields and closes the dialog after a moment", async () => {
    changePasswordMock.mockResolvedValueOnce(undefined);
    renderSidebar();
    const form = openChangePassword();
    const boxes = fill(form, "old", "new-one-123", "new-one-123");

    fireEvent.click(screen.getByRole("button", { name: "Save password" }));

    await waitFor(() => expect(changePasswordMock).toHaveBeenCalledWith("old", "new-one-123"));
    expect(await screen.findByText("Password changed.")).toBeInTheDocument();
    for (const box of boxes) expect(box).toHaveValue("");
    await waitFor(() => expect(screen.queryByRole("heading", { name: "Change password" })).toBeNull(), { timeout: 3000 });
  });

  it("a wrong current password shows the server's message and leaves the dialog open", async () => {
    changePasswordMock.mockRejectedValueOnce(new Error("The current password is not right."));
    renderSidebar();
    const form = openChangePassword();
    fill(form, "wrong", "new-one-123", "new-one-123");

    fireEvent.click(screen.getByRole("button", { name: "Save password" }));

    expect(await screen.findByText("The current password is not right.")).toBeInTheDocument();
    expect(screen.queryByText("Password changed.")).toBeNull();
    expect(screen.getByRole("heading", { name: "Change password" })).toBeInTheDocument();
  });

  it("a password the server finds too short is refused with its message", async () => {
    changePasswordMock.mockRejectedValueOnce(new Error("The new password must be at least 8 characters."));
    renderSidebar();
    const form = openChangePassword();
    fill(form, "old", "short", "short");

    fireEvent.click(screen.getByRole("button", { name: "Save password" }));

    expect(await screen.findByText("The new password must be at least 8 characters.")).toBeInTheDocument();
  });
});

describe("Sign out", () => {
  it("the account menu's Sign out calls the sign-out", () => {
    const onLogout = vi.fn();
    renderSidebar(onLogout);
    fireEvent.click(screen.getByRole("button", { name: "Sign out" }));
    expect(onLogout).toHaveBeenCalledTimes(1);
  });
});

describe("Signing out and what a password change does to the open tab", () => {
  const realLocation = window.location;
  beforeEach(() => {
    localStorage.clear();
    vi.restoreAllMocks();
  });
  afterEach(() => {
    Object.defineProperty(window, "location", { configurable: true, value: realLocation });
  });

  function seed(expiresUtc = "2999-01-01T00:00:00Z") {
    localStorage.setItem("deluno-auth-token", "tok-1");
    localStorage.setItem("deluno-auth-user", JSON.stringify({ id: "1", username: "ann", displayName: "Ann" }));
    localStorage.setItem("deluno-auth-expires", expiresUtc);
  }
  function fakeLocation() {
    const replace = vi.fn();
    Object.defineProperty(window, "location", { configurable: true, value: { pathname: "/system", search: "", hash: "", replace } });
    return replace;
  }

  it("signing out posts the logout with the user's token, forgets the session and goes to sign-in", async () => {
    seed();
    const replace = fakeLocation();
    const fetchSpy = vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(null, { status: 204 }));
    function SignOutButton() {
      const { logout } = useAuth();
      return <button type="button" onClick={logout}>Leave</button>;
    }
    render(
      <AuthProvider>
        <SignOutButton />
      </AuthProvider>
    );

    fireEvent.click(await screen.findByRole("button", { name: "Leave" }));

    expect(fetchSpy).toHaveBeenCalledWith("/api/auth/logout", { method: "POST", headers: { Authorization: "Bearer tok-1" } });
    expect(localStorage.getItem("deluno-auth-token")).toBeNull();
    expect(localStorage.getItem("deluno-auth-user")).toBeNull();
    expect(replace).toHaveBeenCalledWith("/login?return=" + encodeURIComponent("/system"));
  });

  it("after a password change ends the session, the next request sends the tab to sign-in and says why", async () => {
    seed();
    const replace = fakeLocation();
    vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(null, { status: 401 }));

    await authedFetch("/api/system/health");

    expect(replace).toHaveBeenCalledWith("/login?return=" + encodeURIComponent("/system"));
    expect(localStorage.getItem("deluno-auth-token")).toBeNull();
    expect(readSignOutReason()).toContain("your password changed");
  });

  it("a token the server renews keeps the tab signed in without a visit to sign-in", () => {
    seed();
    const replace = fakeLocation();
    applyRenewedToken(new Response(null, { headers: { "X-Deluno-Access-Token": "tok-2", "X-Deluno-Access-Token-Expires": "2999-02-01T00:00:00Z" } }));
    expect(localStorage.getItem("deluno-auth-token")).toBe("tok-2");
    expect(replace).not.toHaveBeenCalled();
  });
});
