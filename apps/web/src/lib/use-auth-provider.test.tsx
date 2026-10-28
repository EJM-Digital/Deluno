import { act, cleanup, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  AuthProvider,
  applyRenewedToken,
  authedFetch,
  clearStored,
  describeSessionEnd,
  readSignOutReason,
  readStored,
  useAuth
} from "./use-auth";
import { firstRealtimePosition, forgetRealtimePosition } from "./realtime-position";
import { RequestFailedError } from "./search-reasons";

/**
 * The auth provider and its helpers as a person meets them: what sign-in and first-run setup send and keep, what a
 * renewed token and a refused request do, and what a page outside the provider gets. Each test reads the request
 * the server would see, the stored session, or the screen; the mutation run on the changed files (rc.30.36's sign-out
 * fix, #1388) found none of this asserted.
 */
const user = { id: "u1", username: "owner", displayName: "Owner", avatarInitials: "O", createdUtc: "2026-01-01T00:00:00Z" };
const payload = { accessToken: "fresh-token", expiresUtc: "2099-01-01T00:00:00Z", user };
const replace = vi.fn();

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

function seed(token = "stored-token") {
  localStorage.setItem("deluno-auth-token", token);
  localStorage.setItem("deluno-auth-user", JSON.stringify(user));
}

let auth: ReturnType<typeof useAuth>;
const loadingSeen: boolean[] = [];
function Probe() {
  auth = useAuth();
  loadingSeen.push(auth.isLoading);
  return (
    <p>
      {auth.isLoading ? "loading" : "ready"} / {auth.isAuthenticated ? `in as ${auth.user?.displayName}` : "out"} / token {auth.token ?? "none"}
    </p>
  );
}

async function mount() {
  render(<AuthProvider><Probe /></AuthProvider>);
  await screen.findByText(/ready/);
}

beforeEach(() => {
  localStorage.clear();
  replace.mockReset();
  forgetRealtimePosition();
  loadingSeen.length = 0;
  Object.defineProperty(window, "location", { configurable: true, value: { pathname: "/movies", search: "?q=1", hash: "#top", replace } });
});
afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("the stored session", () => {
  it("is read back as it was written, and a damaged user record reads as no session", () => {
    seed();
    expect(readStored()).toEqual({ token: "stored-token", user });
    localStorage.setItem("deluno-auth-user", "{not json");
    expect(readStored()).toEqual({ token: null, user: null });
  });

  it("is cleared to the last key, so nothing of the old session is left to describe", () => {
    seed();
    localStorage.setItem("deluno-auth-expires", "2099-01-01T00:00:00Z");
    localStorage.setItem("deluno-auth-keep", "1");
    clearStored();
    for (const key of ["deluno-auth-token", "deluno-auth-user", "deluno-auth-expires", "deluno-auth-keep"]) expect(localStorage.getItem(key)).toBeNull();
  });

  it("reads as nothing when the browser refuses its storage", () => {
    vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => { throw new Error("denied"); });
    expect(readStored()).toEqual({ token: null, user: null });
    expect(readSignOutReason()).toBeNull();
  });

  it("names why it ended: by time, kept or not, or by a password change elsewhere", () => {
    const now = new Date("2026-06-01T00:00:00Z");
    expect(describeSessionEnd("2026-05-31T23:59:59Z", true, now)).toMatch(/30 days away/);
    expect(describeSessionEnd("2026-06-01T00:00:00Z", false, now)).toMatch(/7 days away/);
    expect(describeSessionEnd("2026-06-01T00:00:01Z", false, now)).toMatch(/password changed/);
    expect(describeSessionEnd(null, false, now)).toMatch(/password changed/);
  });
});

describe("mounting the provider", () => {
  it("signs in from a stored token and user, and is ready at once", async () => {
    seed();
    await mount();
    expect(screen.getByText("ready / in as Owner / token stored-token")).toBeTruthy();
    // It says loading until it has read the stored session, so a guard never sends a signed-in person to sign-in.
    expect(loadingSeen[0]).toBe(true);
  });

  it("stays signed out with a token but no user, or a user but no token", async () => {
    localStorage.setItem("deluno-auth-token", "stored-token");
    await mount();
    expect(screen.getByText(/out \/ token none/)).toBeTruthy();
    cleanup();
    localStorage.clear();
    localStorage.setItem("deluno-auth-user", JSON.stringify(user));
    await mount();
    expect(screen.getByText(/out \/ token none/)).toBeTruthy();
  });

  it("takes a renewed token from the renewal event, ignores an empty one, and stops listening once unmounted", async () => {
    seed();
    const { unmount } = render(<AuthProvider><Probe /></AuthProvider>);
    await screen.findByText(/token stored-token/);
    act(() => { window.dispatchEvent(new CustomEvent("deluno-token-renewed", { detail: "renewed-token" })); });
    expect(screen.getByText(/token renewed-token/)).toBeTruthy();
    act(() => { window.dispatchEvent(new CustomEvent("deluno-token-renewed", { detail: "" })); });
    expect(screen.getByText(/token renewed-token/)).toBeTruthy();
    const removed = vi.spyOn(window, "removeEventListener");
    unmount();
    expect(removed).toHaveBeenCalledWith("deluno-token-renewed", expect.any(Function));
  });
});

describe("signing in and first-run setup", () => {
  it("sign-in posts the name, password and keep choice as JSON, then keeps the session for 30 days when asked", async () => {
    const fetchMock = vi.spyOn(globalThis, "fetch").mockResolvedValue(json(payload));
    localStorage.setItem("deluno-signout-reason", "You were signed out ...");
    await mount();
    await act(async () => { await auth.login("owner", "pw", true); });

    expect(fetchMock).toHaveBeenCalledExactlyOnceWith("/api/auth/login", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ username: "owner", password: "pw", keepSignedIn: true })
    });
    expect(screen.getByText("ready / in as Owner / token fresh-token")).toBeTruthy();
    expect(localStorage.getItem("deluno-auth-token")).toBe("fresh-token");
    expect(JSON.parse(localStorage.getItem("deluno-auth-user")!)).toEqual(user);
    expect(localStorage.getItem("deluno-auth-expires")).toBe("2099-01-01T00:00:00Z");
    expect(localStorage.getItem("deluno-auth-keep")).toBe("1");
    expect(localStorage.getItem("deluno-signout-reason")).toBeNull();
  });

  it("sign-in does not keep the session unless asked", async () => {
    const fetchMock = vi.spyOn(globalThis, "fetch").mockResolvedValue(json(payload));
    await mount();
    await act(async () => { await auth.login("owner", "pw"); });
    expect(JSON.parse(String(fetchMock.mock.calls[0][1]?.body)).keepSignedIn).toBe(false);
    expect(localStorage.getItem("deluno-auth-keep")).toBe("0");
  });

  it("a refused sign-in throws the server's answer and keeps nothing", async () => {
    const refused = new Response("no", { status: 401 });
    vi.spyOn(globalThis, "fetch").mockResolvedValue(refused);
    await mount();
    const error = await auth.login("owner", "wrong").catch((e: unknown) => e);
    expect(error).toBeInstanceOf(RequestFailedError);
    expect((error as RequestFailedError).message).toBe("sign-in-refused");
    expect((error as RequestFailedError).response).toBe(refused);
    expect(localStorage.getItem("deluno-auth-token")).toBeNull();
  });

  it("setup posts the display name, name and password as JSON and signs the new account in, with no keep choice", async () => {
    const fetchMock = vi.spyOn(globalThis, "fetch").mockResolvedValue(json(payload));
    await mount();
    await act(async () => { await auth.bootstrap("Owner", "owner", "pw"); });

    expect(fetchMock).toHaveBeenCalledExactlyOnceWith("/api/auth/bootstrap", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ displayName: "Owner", username: "owner", password: "pw" })
    });
    expect(screen.getByText("ready / in as Owner / token fresh-token")).toBeTruthy();
    expect(localStorage.getItem("deluno-auth-token")).toBe("fresh-token");
    expect(localStorage.getItem("deluno-auth-keep")).toBeNull();
  });

  it("a refused setup throws the server's answer", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response("taken", { status: 409 }));
    await mount();
    const error = await auth.bootstrap("Owner", "owner", "pw").catch((e: unknown) => e);
    expect(error).toBeInstanceOf(RequestFailedError);
    expect((error as Error).message).toBe("setup-refused");
  });
});

describe("changing the password", () => {
  it("puts both passwords as JSON with the session's Bearer token", async () => {
    seed();
    const fetchMock = vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(null, { status: 204 }));
    await mount();
    await act(async () => { await auth.changePassword("old", "new"); });

    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe("/api/auth/password");
    expect(init?.method).toBe("PUT");
    expect(init?.body).toBe(JSON.stringify({ currentPassword: "old", newPassword: "new" }));
    const headers = new Headers(init?.headers);
    expect(headers.get("Content-Type")).toBe("application/json");
    expect(headers.get("Authorization")).toBe("Bearer stored-token");
  });

  it("a refusal throws the server's own words, or plain ones when it sent none", async () => {
    seed();
    vi.spyOn(globalThis, "fetch")
      .mockResolvedValueOnce(new Response("Current password is wrong.", { status: 400 }))
      .mockResolvedValueOnce(new Response("", { status: 400 }));
    await mount();
    await expect(auth.changePassword("bad", "new")).rejects.toThrow("Current password is wrong.");
    await expect(auth.changePassword("bad", "new")).rejects.toThrow("Password could not be changed.");
  });
});

describe("signing out", () => {
  it("tells the server with the token the page holds now, a renewed one included, and empties the context", async () => {
    seed();
    const fetchMock = vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(null, { status: 204 }));
    await mount();
    act(() => { window.dispatchEvent(new CustomEvent("deluno-token-renewed", { detail: "renewed-token" })); });
    await act(async () => { auth.logout(); });

    expect(fetchMock).toHaveBeenCalledExactlyOnceWith("/api/auth/logout", { method: "POST", headers: { Authorization: "Bearer renewed-token" }, keepalive: true });
    expect(screen.getByText("ready / out / token none")).toBeTruthy();
    expect(replace).toHaveBeenCalledWith("/login?return=" + encodeURIComponent("/movies?q=1#top"));
  });

  it("asks the server nothing when there is no session, and from the sign-in page stays on plain /login", async () => {
    const fetchMock = vi.spyOn(globalThis, "fetch");
    Object.defineProperty(window, "location", { configurable: true, value: { pathname: "/login", search: "", hash: "", replace } });
    await mount();
    await act(async () => { auth.logout(); });
    expect(fetchMock).not.toHaveBeenCalled();
    expect(replace).toHaveBeenCalledWith("/login");
  });
});

describe("a page outside the provider", () => {
  it("is signed out and ready, and every action says the provider is missing", async () => {
    render(<Probe />);
    expect(screen.getByText("ready / out / token none")).toBeTruthy();
    expect(auth.user).toBeNull();
    await expect(auth.login("a", "b")).rejects.toThrow("AuthProvider not mounted");
    await expect(auth.bootstrap("a", "b", "c")).rejects.toThrow("AuthProvider not mounted");
    await expect(auth.changePassword("a", "b")).rejects.toThrow("AuthProvider not mounted");
    expect(auth.logout()).toBeUndefined();
  });
});

describe("requests made with the session", () => {
  it("carry the stored token, or the one passed, and none when there is no session", async () => {
    const fetchMock = vi.spyOn(globalThis, "fetch").mockImplementation(async () => new Response("{}", { status: 200 }));
    await authedFetch("/api/a");
    expect(new Headers(fetchMock.mock.calls[0][1]?.headers).get("Authorization")).toBeNull();
    seed();
    await authedFetch("/api/b", { headers: { "X-Kept": "1" } });
    const sent = new Headers(fetchMock.mock.calls[1][1]?.headers);
    expect(sent.get("Authorization")).toBe("Bearer stored-token");
    expect(sent.get("X-Kept")).toBe("1");
    await authedFetch("/api/c", undefined, "passed-token");
    expect(new Headers(fetchMock.mock.calls[2][1]?.headers).get("Authorization")).toBe("Bearer passed-token");
  });

  it("note the realtime position the server sends, so the page's live updates resume from it", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response("{}", { status: 200, headers: { "X-Deluno-Event-Seq": "42", "X-Deluno-Event-Instance": "i1" } }));
    await authedFetch("/api/a");
    expect(firstRealtimePosition()).toBe(42);
  });

  it("a refused request with a session remembers why, clears it and goes to sign-in; any other answer leaves it", async () => {
    seed();
    localStorage.setItem("deluno-auth-expires", "2000-01-01T00:00:00Z");
    localStorage.setItem("deluno-auth-keep", "1");
    vi.spyOn(globalThis, "fetch")
      .mockResolvedValueOnce(new Response("{}", { status: 403 }))
      .mockResolvedValueOnce(new Response("{}", { status: 401 }));
    await authedFetch("/api/a");
    expect(localStorage.getItem("deluno-auth-token")).toBe("stored-token");
    expect(replace).not.toHaveBeenCalled();

    await authedFetch("/api/a");
    expect(localStorage.getItem("deluno-auth-token")).toBeNull();
    expect(readSignOutReason()).toMatch(/30 days away/);
    expect(replace).toHaveBeenCalledWith("/login?return=" + encodeURIComponent("/movies?q=1#top"));
  });

  it("a renewed token in any answer is stored with its new expiry", () => {
    seed();
    applyRenewedToken(new Response(null, { headers: { "X-Deluno-Access-Token": "renewed", "X-Deluno-Access-Token-Expires": "2099-02-02T00:00:00Z" } }));
    expect(localStorage.getItem("deluno-auth-token")).toBe("renewed");
    expect(localStorage.getItem("deluno-auth-expires")).toBe("2099-02-02T00:00:00Z");
  });
});

describe("the edges of a stored session", () => {
  it("a sign-in whose answer names no expiry keeps the one already stored", async () => {
    localStorage.setItem("deluno-auth-expires", "2099-03-03T00:00:00Z");
    vi.spyOn(globalThis, "fetch").mockResolvedValue(json({ accessToken: "fresh-token", user }));
    await mount();
    await act(async () => { await auth.login("owner", "pw"); });
    expect(localStorage.getItem("deluno-auth-expires")).toBe("2099-03-03T00:00:00Z");
  });

  it("a renewed token with no expiry keeps the stored expiry", () => {
    seed();
    localStorage.setItem("deluno-auth-expires", "2099-03-03T00:00:00Z");
    applyRenewedToken(new Response(null, { headers: { "X-Deluno-Access-Token": "renewed" } }));
    expect(localStorage.getItem("deluno-auth-expires")).toBe("2099-03-03T00:00:00Z");
  });

  it("a renewed token on any answer of a request is stored", async () => {
    seed();
    vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response("{}", { status: 200, headers: { "X-Deluno-Access-Token": "renewed-by-request" } }));
    await authedFetch("/api/a");
    expect(localStorage.getItem("deluno-auth-token")).toBe("renewed-by-request");
  });

  it("an ended session that was not kept says 7 days", async () => {
    seed();
    localStorage.setItem("deluno-auth-expires", "2000-01-01T00:00:00Z");
    localStorage.setItem("deluno-auth-keep", "0");
    vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response("{}", { status: 401 }));
    await authedFetch("/api/a");
    expect(readSignOutReason()).toMatch(/7 days away/);
  });

  it("a refused request made with a passed token while nothing is stored leaves no reason to show", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response("{}", { status: 401 }));
    await authedFetch("/api/a", undefined, "passed-token");
    expect(readSignOutReason()).toBeNull();
  });

  it("a password change whose refusal cannot be read throws the plain words", async () => {
    seed();
    const unreadable = new Response("x", { status: 400 });
    vi.spyOn(unreadable, "text").mockRejectedValue(new Error("stream gone"));
    vi.spyOn(globalThis, "fetch").mockResolvedValue(unreadable);
    await mount();
    await expect(auth.changePassword("bad", "new")).rejects.toThrow(/^Password could not be changed\.$/);
  });

  it("a visitor refused on the sign-in page itself stays on plain /login when setup is done", async () => {
    Object.defineProperty(window, "location", { configurable: true, value: { pathname: "/login", search: "", hash: "", replace } });
    vi.spyOn(globalThis, "fetch").mockImplementation(async (input) =>
      String(input).includes("bootstrap-status") ? json({ requiresSetup: false }) : new Response("{}", { status: 401 }));
    await authedFetch("/api/a");
    await vi.waitFor(() => expect(replace).toHaveBeenCalled());
    expect(replace).toHaveBeenCalledWith("/login");
  });

  it("a visitor on a fresh install goes to setup carrying where they were going", async () => {
    vi.spyOn(globalThis, "fetch").mockImplementation(async (input) =>
      String(input).includes("bootstrap-status") ? json({ requiresSetup: true }) : new Response("{}", { status: 401 }));
    await authedFetch("/api/a");
    await vi.waitFor(() => expect(replace).toHaveBeenCalled());
    expect(replace).toHaveBeenCalledWith("/setup?return=" + encodeURIComponent("/movies?q=1#top"));
  });
});
