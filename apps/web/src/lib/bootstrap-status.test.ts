import { describe, expect, it, vi } from "vitest";
import { BootstrapStatusError, readRequiresSetup } from "./bootstrap-status";

const noWait = () => Promise.resolve();
const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

describe("readRequiresSetup never takes a failed read as 'already set up' (#1339)", () => {
  it("reads true and false straight from the answer", async () => {
    expect(await readRequiresSetup(vi.fn().mockResolvedValue(json({ requiresSetup: true })), noWait)).toBe(true);
    expect(await readRequiresSetup(vi.fn().mockResolvedValue(json({ requiresSetup: false })), noWait)).toBe(false);
  });

  it("retries a read the router cut off (a rejected fetch) and then uses the real answer", async () => {
    const fetcher = vi.fn()
      .mockRejectedValueOnce(new TypeError("Failed to fetch"))
      .mockResolvedValueOnce(json({ requiresSetup: true }));
    expect(await readRequiresSetup(fetcher, noWait)).toBe(true);
    expect(fetcher).toHaveBeenCalledTimes(2);
  });

  it("retries while the host answers 503 during its start-up", async () => {
    const fetcher = vi.fn()
      .mockResolvedValueOnce(new Response("", { status: 503 }))
      .mockResolvedValueOnce(new Response("", { status: 503 }))
      .mockResolvedValueOnce(json({ requiresSetup: true }));
    expect(await readRequiresSetup(fetcher, noWait)).toBe(true);
    expect(fetcher).toHaveBeenCalledTimes(3);
  });

  it("retries an answer that is not the expected shape", async () => {
    const fetcher = vi.fn()
      .mockResolvedValueOnce(new Response("<html>", { status: 200 }))
      .mockResolvedValueOnce(json({}))
      .mockResolvedValueOnce(json({ requiresSetup: false }));
    expect(await readRequiresSetup(fetcher, noWait)).toBe(false);
  });

  it("waits longer between each try", async () => {
    const waits: number[] = [];
    const fetcher = vi.fn().mockRejectedValue(new TypeError("Failed to fetch"));
    await expect(readRequiresSetup(fetcher, async (ms) => { waits.push(ms); })).rejects.toBeInstanceOf(BootstrapStatusError);
    expect(waits).toEqual([250, 600, 1200]);
  });

  it("reports the failure in plain words when every try fails, for a refusal and for a lost connection alike", async () => {
    for (const failing of [
      vi.fn().mockResolvedValue(new Response("", { status: 500 })),
      vi.fn().mockRejectedValue(new DOMException("aborted", "AbortError")),
    ]) {
      const error = await readRequiresSetup(failing, noWait).catch((e: unknown) => e);
      expect(error).toBeInstanceOf(BootstrapStatusError);
      expect((error as Error).message).toMatch(/could not check whether it has been set up/i);
      expect(failing).toHaveBeenCalledTimes(4);
    }
  });
});
