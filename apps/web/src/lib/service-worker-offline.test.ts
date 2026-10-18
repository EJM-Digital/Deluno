import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";

/**
 * Opening a page with no network: the page worker answers with the page it cached, else the cached shell (the app's own index
 * page, which then draws the screen), else the offline page. Its source is run as the browser runs it, over a cache that keeps
 * what is put into it and a network that is down or up.
 */

type Listener = (event: any) => void;

function loadWorker(network: { up: boolean }, stored: Record<string, string> = {}) {
  const source = readFileSync(resolve(__dirname, "../../public/sw.js"), "utf8");
  const listeners: Record<string, Listener> = {};
  const key = (request: unknown) => (typeof request === "string" ? new URL(request, "http://localhost").pathname : new URL((request as Request).url).pathname);
  const entries = new Map<string, string>(Object.entries(stored));
  const cache = {
    put: async (request: unknown, response: Response) => { entries.set(key(request), await response.text()); },
    match: async (request: unknown) => (entries.has(key(request)) ? new Response(entries.get(key(request))!, { status: 200 }) : undefined),
    addAll: async () => undefined
  };
  const caches = {
    open: async () => cache,
    match: (request: unknown) => cache.match(request),
    keys: async () => []
  };
  const fetchOverNetwork = async (request: { url: string }) => {
    if (!network.up) throw new TypeError("Failed to fetch");
    return new Response(`<html>network:${new URL(request.url).pathname}</html>`, { status: 200 });
  };
  const self = { addEventListener: (name: string, fn: Listener) => { listeners[name] = fn; } };
  new Function("self", "caches", "fetch", "Response", source)(self, caches, fetchOverNetwork, Response);
  return { listeners, entries };
}

function navigation(path: string) {
  const waits: Promise<unknown>[] = [];
  let responded: Promise<Response> | null = null;
  return {
    request: { url: `http://localhost${path}`, method: "GET", destination: "document", mode: "navigate" },
    respondWith: (p: Promise<Response>) => { responded = p; },
    waitUntil: (p: Promise<unknown>) => { waits.push(p); },
    answered: () => responded!,
    settled: () => Promise.all(waits)
  };
}

const bodyOf = async (event: ReturnType<typeof navigation>) => (await event.answered()).text();

describe("the page worker with no network", () => {
  it("online, a page is fetched from the network and a copy is kept for later", async () => {
    const { listeners, entries } = loadWorker({ up: true });
    const event = navigation("/queue");
    listeners.fetch(event);

    expect(await bodyOf(event)).toBe("<html>network:/queue</html>");
    await event.settled();
    expect(entries.get("/queue")).toBe("<html>network:/queue</html>");
  });

  it("offline, a page that was cached comes back as it was cached", async () => {
    const { listeners } = loadWorker({ up: false }, { "/queue": "<html>cached queue</html>", "/": "<html>shell</html>", "/offline.html": "<html>offline</html>" });
    const event = navigation("/queue");
    listeners.fetch(event);
    expect(await bodyOf(event)).toBe("<html>cached queue</html>");
  });

  it("offline, a page never visited gets the cached shell, which draws the screen itself", async () => {
    const { listeners } = loadWorker({ up: false }, { "/": "<html>shell</html>", "/offline.html": "<html>offline</html>" });
    const event = navigation("/settings/tags");
    listeners.fetch(event);
    expect(await bodyOf(event)).toBe("<html>shell</html>");
  });

  it("offline with no shell either, the offline page answers", async () => {
    const { listeners } = loadWorker({ up: false }, { "/offline.html": "<html>offline</html>" });
    const event = navigation("/queue");
    listeners.fetch(event);
    expect(await bodyOf(event)).toBe("<html>offline</html>");
  });

  it("offline with nothing cached at all, the browser's own error is let through rather than an empty page", async () => {
    const { listeners } = loadWorker({ up: false });
    const event = navigation("/queue");
    listeners.fetch(event);
    const response = await event.answered();
    expect(response.type).toBe("error");
  });
});
