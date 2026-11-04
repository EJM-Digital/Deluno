import { beforeEach, describe, expect, it, vi } from "vitest";
import { submitQueueRemoval } from "./remove-download";
import { authedFetch } from "./use-auth";

vi.mock("./use-auth", () => ({ authedFetch: vi.fn() }));

const reply = (body: unknown, status = 200) => new Response(typeof body === "string" ? body : JSON.stringify(body), { status });

beforeEach(() => { vi.mocked(authedFetch).mockReset(); });

describe("removing a download from its client", () => {
  it("posts the reason to the client's queue item, with the item's name made safe for an address", async () => {
    vi.mocked(authedFetch).mockResolvedValue(reply({ succeeded: true, message: "Removed" }));
    const result = await submitQueueRemoval("qb-1", "a b/c", "failed");
    expect(result).toEqual({ succeeded: true, message: "Removed" });
    expect(authedFetch).toHaveBeenCalledWith("/api/download-clients/qb-1/queue/a%20b%2Fc/remove", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ reason: "failed" })
    });
  });

  it("sends each reason as it was chosen", async () => {
    vi.mocked(authedFetch).mockImplementation(async () => reply({ succeeded: true }));
    for (const reason of ["failed", "unwanted", "forget", "reclaim"] as const) {
      await submitQueueRemoval("c", "i", reason);
      expect(JSON.parse(String((vi.mocked(authedFetch).mock.calls.at(-1)![1] as RequestInit).body))).toEqual({ reason });
    }
  });

  it("throws the server's own words when it says the removal did not succeed", async () => {
    vi.mocked(authedFetch).mockResolvedValue(reply({ succeeded: false, message: "The client refused" }));
    await expect(submitQueueRemoval("c", "i", "forget")).rejects.toThrow("The client refused");
  });

  it("throws when the request failed even though the reply says it succeeded", async () => {
    vi.mocked(authedFetch).mockResolvedValue(reply({ succeeded: true, message: "Odd" }, 500));
    await expect(submitQueueRemoval("c", "i", "forget")).rejects.toThrow("Odd");
  });

  it("throws what the server said in plain text when the reply is not a result", async () => {
    vi.mocked(authedFetch).mockResolvedValue(reply("Client is offline", 502));
    await expect(submitQueueRemoval("c", "i", "forget")).rejects.toThrow(/Client is offline|could not be completed/);
  });

  it("says the removal could not be completed when the server says nothing useful", async () => {
    vi.mocked(authedFetch).mockResolvedValue(reply("", 500));
    await expect(submitQueueRemoval("c", "i", "forget")).rejects.toThrow("That removal could not be completed.");
    vi.mocked(authedFetch).mockResolvedValue(reply({ succeeded: false }, 200));
    await expect(submitQueueRemoval("c", "i", "forget")).rejects.toThrow("That removal could not be completed.");
  });
});
