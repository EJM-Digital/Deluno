import { act, cleanup, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider, useAuth } from "./use-auth";

function SignOutButton() {
  const { logout, isAuthenticated } = useAuth();
  return <button type="button" onClick={logout}>{isAuthenticated ? "Sign out" : "Signed out"}</button>;
}

function seed() {
  localStorage.setItem("deluno-auth-token", "session-token");
  localStorage.setItem("deluno-auth-user", JSON.stringify({ id: "1", username: "owner", displayName: "Owner" }));
  localStorage.setItem("deluno-auth-expires", "2099-01-01T00:00:00Z");
  localStorage.setItem("deluno-auth-keep", "0");
}

describe("signing out", () => {
  const replace = vi.fn();

  beforeEach(() => {
    localStorage.clear();
    replace.mockReset();
    Object.defineProperty(window, "location", {
      configurable: true,
      value: { pathname: "/movies", search: "", hash: "", replace }
    });
  });
  afterEach(() => {
    cleanup();
    vi.restoreAllMocks();
  });

  it("tells the server with the Bearer token, clears the stored session and goes to the sign-in page", async () => {
    seed();
    const fetchMock = vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(null, { status: 204 }));
    render(<AuthProvider><SignOutButton /></AuthProvider>);
    const button = await screen.findByRole("button", { name: "Sign out" });

    await act(async () => { button.click(); });

    expect(fetchMock).toHaveBeenCalledExactlyOnceWith("/api/auth/logout", { method: "POST", headers: { Authorization: "Bearer session-token" } });
    expect(localStorage.getItem("deluno-auth-token")).toBeNull();
    expect(localStorage.getItem("deluno-auth-user")).toBeNull();
    expect(replace).toHaveBeenCalledWith("/login?return=" + encodeURIComponent("/movies"));
    expect(screen.getByRole("button", { name: "Signed out" })).toBeTruthy();
  });

  it("still signs out here when the server cannot be reached", async () => {
    seed();
    vi.spyOn(globalThis, "fetch").mockRejectedValue(new TypeError("offline"));
    render(<AuthProvider><SignOutButton /></AuthProvider>);
    const button = await screen.findByRole("button", { name: "Sign out" });

    await act(async () => { button.click(); });

    expect(localStorage.getItem("deluno-auth-token")).toBeNull();
    expect(replace).toHaveBeenCalled();
  });
});
