import { beforeEach, describe, expect, it, vi } from "vitest";

const fetchJsonMock = vi.hoisted(() => vi.fn());
vi.mock("./api", async (importOriginal) => ({ ...(await importOriginal<typeof import("./api")>()), fetchJson: fetchJsonMock }));

const { sharedSources } = await import("./shared-sources");
const { ApiRequestError } = await import("./api");

describe("the processor hand-offs list shared by the screens", () => {
  beforeEach(() => { fetchJsonMock.mockReset(); });

  it("reads the newest thirty hand-offs", async () => {
    fetchJsonMock.mockResolvedValue([{ id: "h1" }]);
    expect(await sharedSources.handoffs.fetch()).toEqual([{ id: "h1" }]);
    expect(fetchJsonMock).toHaveBeenCalledWith("/api/integrations/processors/handoffs?take=30");
  });

  it("shows none, without an error, from an older local API that has no such endpoint", async () => {
    fetchJsonMock.mockRejectedValue(new ApiRequestError("Not found", 404, "/api/integrations/processors/handoffs", ""));
    expect(await sharedSources.handoffs.fetch()).toEqual([]);
  });

  it("lets every other failure through so the screen can say so", async () => {
    const failure = new ApiRequestError("Server error", 500, "/api/integrations/processors/handoffs", "");
    fetchJsonMock.mockRejectedValue(failure);
    await expect(sharedSources.handoffs.fetch()).rejects.toBe(failure);
    const offline = new Error("offline");
    fetchJsonMock.mockRejectedValue(offline);
    await expect(sharedSources.handoffs.fetch()).rejects.toBe(offline);
  });
});
