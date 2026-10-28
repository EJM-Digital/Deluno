// @vitest-environment node
import { afterEach, describe, expect, it, vi } from "vitest";
import { applyRenewedToken, authedFetch } from "./use-auth";

/**
 * The auth helpers also run where there is no window and no storage (a server render, a worker): there they must do
 * nothing rather than throw. This file runs in Node's own environment, where `window` really does not exist.
 */
afterEach(() => { vi.restoreAllMocks(); });

describe("with no window", () => {
  it("a renewed token is let go without an error", () => {
    expect(() => applyRenewedToken(new Response(null, { headers: { "X-Deluno-Access-Token": "renewed" } }))).not.toThrow();
  });

  it("a refused request with a session answers as it was, without trying to move a page", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response("{}", { status: 401 }));
    const response = await authedFetch("/api/a", undefined, "a-token");
    expect(response.status).toBe(401);
  });

  it("a refused request with no session answers as it was, and nothing goes wrong after it", async () => {
    const fetchMock = vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response("{}", { status: 401 }));
    const failures: unknown[] = [];
    const onRejection = (reason: unknown) => { failures.push(reason); };
    process.on("unhandledRejection", onRejection);
    try {
      const response = await authedFetch("/api/a");
      expect(response.status).toBe(401);
      // The visitor redirect runs on its own; give it its turn before reading what it did.
      await new Promise((resolve) => setImmediate(resolve));
      await new Promise((resolve) => setImmediate(resolve));
      expect(failures).toEqual([]);
      expect(fetchMock).toHaveBeenCalledTimes(1);
    } finally {
      process.off("unhandledRejection", onRejection);
    }
  });
});
