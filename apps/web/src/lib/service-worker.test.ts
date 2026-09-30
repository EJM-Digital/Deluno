import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it, vi } from "vitest";

type Listener = (event: unknown) => void;

function loadWorker() {
  const source = readFileSync(resolve(__dirname, "../../public/sw.js"), "utf8");
  const listeners: Record<string, Listener> = {};
  const put = vi.fn(() => new Promise<void>(() => {})); // never resolves: a slow cache
  const caches = {
    open: vi.fn(async () => ({ put, match: vi.fn(async () => undefined) })),
    match: vi.fn(async () => undefined),
    keys: vi.fn(async () => []),
  };
  const self = {
    addEventListener: (name: string, fn: Listener) => { listeners[name] = fn; },
  };
  new Function("self", "caches", "fetch", "Response", source)(
    self,
    caches,
    vi.fn(async () => new Response("{}", { status: 200 })),
    Response,
  );
  return { listeners, put };
}

function fetchEvent(path: string, destination = "") {
  const waits: Promise<unknown>[] = [];
  let responded: Promise<Response> | null = null;
  return {
    request: { url: `http://localhost${path}`, method: "GET", destination, mode: "cors" },
    respondWith: (p: Promise<Response>) => { responded = p; },
    waitUntil: (p: Promise<unknown>) => { waits.push(p); },
    get responded() { return responded; },
  };
}

describe("service worker", () => {
  it("does not intercept API calls, so nothing waits on Cache Storage", () => {
    const { listeners } = loadWorker();
    const event = fetchEvent("/api/movies");
    listeners.fetch(event);
    expect(event.responded).toBeNull();
  });

  it("returns a shell asset before the cache copy finishes", async () => {
    const { listeners, put } = loadWorker();
    const event = fetchEvent("/assets/app-abc123.js", "script");
    listeners.fetch(event);
    const response = await event.responded!;
    expect(response.status).toBe(200);
    await Promise.resolve();
    await Promise.resolve();
    expect(put.mock.calls.length).toBeLessThanOrEqual(1);
  });
});
