import { beforeEach, describe, expect, it, vi } from "vitest";
import { applyRenewedToken, authedFetch, describeSessionEnd, readSignOutReason } from "./use-auth";

function seedSession(expiresUtc: string, keep = false) {
  localStorage.setItem("deluno-auth-token", "old-token");
  localStorage.setItem("deluno-auth-user", JSON.stringify({ id: "1" }));
  localStorage.setItem("deluno-auth-expires", expiresUtc);
  localStorage.setItem("deluno-auth-keep", keep ? "1" : "0");
}

describe("session renewal and sign-out reasons", () => {
  beforeEach(() => {
    localStorage.clear();
    vi.restoreAllMocks();
  });

  it("stores the renewed token a normal request brings back", () => {
    seedSession("2026-10-10T00:00:00Z");
    const seen: string[] = [];
    window.addEventListener("deluno-token-renewed", (e) => seen.push((e as CustomEvent<string>).detail), { once: true });

    applyRenewedToken(
      new Response(null, {
        headers: { "X-Deluno-Access-Token": "new-token", "X-Deluno-Access-Token-Expires": "2026-11-09T00:00:00Z" }
      })
    );

    expect(localStorage.getItem("deluno-auth-token")).toBe("new-token");
    expect(localStorage.getItem("deluno-auth-expires")).toBe("2026-11-09T00:00:00Z");
    expect(seen).toEqual(["new-token"]);
  });

  it("leaves the token alone when the server sends no renewal", () => {
    seedSession("2026-10-10T00:00:00Z");
    applyRenewedToken(new Response(null));
    expect(localStorage.getItem("deluno-auth-token")).toBe("old-token");
  });

  it("says why the session ended", () => {
    const now = new Date("2026-10-20T00:00:00Z");
    expect(describeSessionEnd("2026-10-10T00:00:00Z", true, now)).toContain("30 days");
    expect(describeSessionEnd("2026-10-10T00:00:00Z", false, now)).toContain("7 days");
    expect(describeSessionEnd("2026-11-10T00:00:00Z", false, now)).toContain("password changed");
    expect(describeSessionEnd(null, false, now)).toContain("password changed");
  });

  it("an expired session returns to the original page after sign-in", async () => {
    seedSession("2020-01-01T00:00:00Z", true);
    const replace = vi.fn();
    Object.defineProperty(window, "location", {
      configurable: true,
      value: { pathname: "/movies/42", search: "?tab=files", hash: "", replace }
    });
    vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(null, { status: 401 }));

    await authedFetch("/api/anything");

    expect(replace).toHaveBeenCalledWith("/login?return=" + encodeURIComponent("/movies/42?tab=files"));
    expect(readSignOutReason()).toContain("30 days");
    expect(localStorage.getItem("deluno-auth-token")).toBeNull();
  });

  describe("a request with no session at all (#1339)", () => {
    function visitorAt(pathname: string, search = "") {
      const replace = vi.fn();
      Object.defineProperty(window, "location", { configurable: true, value: { pathname, search, hash: "", replace } });
      return replace;
    }
    const answer = (requiresSetup: boolean) => (url: RequestInfo | URL) =>
      Promise.resolve(String(url).includes("bootstrap-status")
        ? new Response(JSON.stringify({ requiresSetup }), { status: 200, headers: { "Content-Type": "application/json" } })
        : new Response(null, { status: 401 }));

    it("on a fresh install goes to setup carrying where the person was going, never to sign-in", async () => {
      const replace = visitorAt("/movies", "?filter=missing");
      vi.spyOn(globalThis, "fetch").mockImplementation(answer(true));

      await authedFetch("/api/movies");

      await vi.waitFor(() => expect(replace).toHaveBeenCalledWith("/setup?return=" + encodeURIComponent("/movies?filter=missing")));
      expect(replace).toHaveBeenCalledTimes(1);
    });

    it("on a set-up install goes to sign-in carrying where the person was going", async () => {
      const replace = visitorAt("/movies", "?filter=missing");
      vi.spyOn(globalThis, "fetch").mockImplementation(answer(false));

      await authedFetch("/api/movies");

      await vi.waitFor(() => expect(replace).toHaveBeenCalledWith("/login?return=" + encodeURIComponent("/movies?filter=missing")));
    });

    it("sends several refused requests to one place, once", async () => {
      const replace = visitorAt("/movies");
      vi.spyOn(globalThis, "fetch").mockImplementation(answer(true));

      await Promise.all([authedFetch("/api/a"), authedFetch("/api/b"), authedFetch("/api/c")]);

      await vi.waitFor(() => expect(replace).toHaveBeenCalledTimes(1));
    });

    it("when the setup check cannot be answered goes to sign-in, which checks again and says so", async () => {
      const replace = visitorAt("/movies");
      vi.spyOn(globalThis, "fetch").mockImplementation((url) =>
        String(url).includes("bootstrap-status") ? Promise.reject(new TypeError("Failed to fetch")) : Promise.resolve(new Response(null, { status: 401 })));

      await authedFetch("/api/movies");

      await vi.waitFor(() => expect(replace).toHaveBeenCalledWith("/login?return=" + encodeURIComponent("/movies")), { timeout: 5000 });
    });
  });
});
