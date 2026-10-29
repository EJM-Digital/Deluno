import { beforeEach, describe, expect, it, vi } from "vitest";

/**
 * Transfers' own data sources, read against an older API. An API that does not have an endpoint answers 404, and the page
 * must show that source as empty rather than as an error screen. Any other failure still reaches the page.
 */

const authedFetchMock = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));

const { queueSources } = await import("./queue-screen");

beforeEach(() => {
  authedFetchMock.mockReset();
});

describe("Transfers sources against an older API", () => {
  it("the processor connections source lists no connectors when the API answers 404", async () => {
    authedFetchMock.mockResolvedValue(new Response("not found", { status: 404 }));

    await expect(queueSources.processorConnections.fetch()).resolves.toEqual([]);
  });

  it("the held releases source lists none when the API answers 404, instead of an error", async () => {
    authedFetchMock.mockResolvedValue(new Response("not found", { status: 404 }));

    await expect(queueSources.heldReleases.fetch()).resolves.toEqual([]);
  });

  it("a server error on the processor connections source is not hidden as an empty list", async () => {
    authedFetchMock.mockResolvedValue(new Response("boom", { status: 500 }));

    await expect(queueSources.processorConnections.fetch()).rejects.toThrow();
  });
});
