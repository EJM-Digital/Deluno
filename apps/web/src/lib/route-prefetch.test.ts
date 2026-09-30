import { beforeEach, describe, expect, it, vi } from "vitest";

/**
 * #1081: the code for a screen is fetched when the pointer or focus reaches
 * its sidebar link, and on hover the loader of that screen starts its queries.
 */
const queueLoader = vi.hoisted(() => vi.fn());
const activityLoader = vi.hoisted(() => vi.fn());
vi.mock("../routes/queue-page", () => ({ queueLoader, QueuePage: () => null }));
vi.mock("../routes/activity-page", () => ({ activityLoader, ActivityPage: () => null }));

const { prefetchRoute, prefetchHandlers } = await import("./route-prefetch");

async function flush() {
  await new Promise((resolve) => setTimeout(resolve, 0));
}

describe("route prefetch", () => {
  beforeEach(() => { queueLoader.mockReset(); activityLoader.mockReset(); });

  it("starts the queries of the screen when asked for data", async () => {
    prefetchRoute("/queue", { data: true });
    await flush();
    expect(queueLoader).toHaveBeenCalledTimes(1);
  });

  it("fetches only the code, and starts no queries, when not asked for data", async () => {
    prefetchRoute("/activity");
    await flush();
    expect(activityLoader).not.toHaveBeenCalled();
  });

  it("ignores an address no screen owns", () => {
    expect(() => prefetchRoute("/nowhere/at/all", { data: true })).not.toThrow();
  });

  it("hover, focus and touch all prefetch the screen of the link with its data", async () => {
    const handlers = prefetchHandlers("/queue?filter=all");
    handlers.onFocus();
    await vi.waitFor(() => expect(queueLoader).toHaveBeenCalledTimes(1));
    handlers.onPointerEnter();
    await vi.waitFor(() => expect(queueLoader).toHaveBeenCalledTimes(2));
    handlers.onTouchStart();
    await vi.waitFor(() => expect(queueLoader).toHaveBeenCalledTimes(3));
  });
});
