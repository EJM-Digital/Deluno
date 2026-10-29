import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import vm from "node:vm";
import { describe, expect, it, vi } from "vitest";

// #1329: public/sw.js, run as the browser would run it, against fake caches and a fake network.

const source = readFileSync(resolve(__dirname, "../../public/sw.js"), "utf8");

type Handler = (event: Record<string, unknown>) => void;

function startWorker(options: { stored?: Record<string, Map<string, string>>; network?: (url: string) => Promise<Response> } = {}) {
  const handlers = new Map<string, Handler>();
  const stores = new Map<string, Map<string, string>>(Object.entries(options.stored ?? {}));
  const fetched: string[] = [];
  const calls = { skipWaiting: 0, claim: 0, shown: [] as unknown[], opened: [] as string[], focused: [] as string[] };

  const cacheOf = (name: string) => {
    if (!stores.has(name)) stores.set(name, new Map());
    const store = stores.get(name)!;
    return {
      addAll: async (urls: string[]) => { for (const url of urls) store.set(url, `precached ${url}`); },
      put: async (request: { url: string } | string, response: Response) => { store.set(typeof request === "string" ? request : request.url, await response.text()); },
      match: async (request: { url: string } | string) => {
        const hit = store.get(typeof request === "string" ? request : request.url);
        return hit === undefined ? undefined : new Response(hit);
      }
    };
  };

  const scope = {
    addEventListener: (type: string, handler: Handler) => handlers.set(type, handler),
    skipWaiting: () => { calls.skipWaiting += 1; return Promise.resolve(); },
    clients: {
      claim: () => { calls.claim += 1; return Promise.resolve(); },
      matchAll: async () => [{ url: "http://deluno.test/queue", focus: async () => { calls.focused.push("http://deluno.test/queue"); } }],
      openWindow: async (url: string) => { calls.opened.push(url); }
    },
    registration: { showNotification: async (title: string, init: unknown) => { calls.shown.push([title, init]); } }
  };
  const caches = {
    open: async (name: string) => cacheOf(name),
    keys: async () => [...stores.keys()],
    delete: async (name: string) => stores.delete(name),
    match: async (request: { url: string } | string) => {
      for (const store of stores.values()) {
        const hit = store.get(typeof request === "string" ? request : request.url);
        if (hit !== undefined) return new Response(hit);
      }
      return undefined;
    }
  };
  const network = options.network ?? (async (url: string) => new Response(`network ${url}`));
  const context = vm.createContext({
    self: scope, caches, URL, Response, Promise,
    fetch: (request: { url: string }) => { fetched.push(request.url); return network(request.url); }
  });
  vm.runInContext(source, context);

  async function dispatch(type: string, event: Record<string, unknown>) {
    const waits: Promise<unknown>[] = [];
    let answer: Promise<unknown> | undefined;
    handlers.get(type)!({ ...event, waitUntil: (p: Promise<unknown>) => { waits.push(p); }, respondWith: (p: Promise<unknown>) => { answer = p; } });
    await Promise.all(waits);
    return answer === undefined ? undefined : await answer;
  }
  return { dispatch, stores, fetched, calls, handlers };
}

const request = (url: string, extra: Record<string, unknown> = {}) => ({ url, destination: "", mode: "no-cors", ...extra });

describe("the service worker's caches (#1329)", () => {
  it("serves an image from its cache without asking the network, and stores a new image after handing it over", async () => {
    const worker = startWorker({ stored: { "deluno-img-v2": new Map([["http://deluno.test/poster-a.jpg", "cached poster"]]) } });

    const hit = await worker.dispatch("fetch", { request: request("http://deluno.test/poster-a.jpg", { destination: "image" }) }) as Response;
    expect(await hit.text()).toBe("cached poster");
    expect(worker.fetched).toEqual([]);

    const miss = await worker.dispatch("fetch", { request: request("http://deluno.test/poster-b.jpg", { destination: "image" }) }) as Response;
    expect(await miss.text()).toBe("network http://deluno.test/poster-b.jpg");
    expect(worker.fetched).toEqual(["http://deluno.test/poster-b.jpg"]);
    expect(worker.stores.get("deluno-img-v2")!.get("http://deluno.test/poster-b.jpg")).toBe("network http://deluno.test/poster-b.jpg");
  });

  it("on activate it deletes every cache that is not the current shell or image cache, and takes over open pages at once", async () => {
    const worker = startWorker({ stored: {
      "deluno-shell-v1": new Map(), "deluno-img-v1": new Map(), "deluno-shell-v2": new Map(), "deluno-img-v2": new Map(), "someone-elses": new Map()
    } });

    await worker.dispatch("activate", {});

    expect([...worker.stores.keys()].sort()).toEqual(["deluno-img-v2", "deluno-shell-v2"]);
    expect(worker.calls.claim).toBe(1);
  });

  it("on install it caches the shell, the manifest and the offline page, and does not wait for old pages to close", async () => {
    const worker = startWorker();

    await worker.dispatch("install", {});

    expect([...worker.stores.get("deluno-shell-v2")!.keys()].sort()).toEqual(["/", "/manifest.json", "/offline.html"]);
    expect(worker.calls.skipWaiting).toBe(1);
  });

  it("leaves API and live-hub calls to the network, uncached", async () => {
    const worker = startWorker();

    expect(await worker.dispatch("fetch", { request: request("http://deluno.test/api/movies") })).toBeUndefined();
    expect(await worker.dispatch("fetch", { request: request("http://deluno.test/hubs/live") })).toBeUndefined();
    expect(worker.fetched).toEqual([]);
  });

  it("a page opened while offline is the cached shell, or the offline page when there is no shell", async () => {
    const offline = async () => { throw new TypeError("offline"); };
    const withShell = startWorker({ stored: { "deluno-shell-v2": new Map([["/", "the shell"], ["/offline.html", "the offline page"]]) }, network: offline });
    const shell = await withShell.dispatch("fetch", { request: request("http://deluno.test/movies", { mode: "navigate" }) }) as Response;
    expect(await shell.text()).toBe("the shell");

    const bare = startWorker({ stored: { "deluno-shell-v2": new Map([["/offline.html", "the offline page"]]) }, network: offline });
    const page = await bare.dispatch("fetch", { request: request("http://deluno.test/movies", { mode: "navigate" }) }) as Response;
    expect(await page.text()).toBe("the offline page");
  });
});

describe("the service worker's notification stub (#1329)", () => {
  it("a push with data shows a notification with its title, body and the page it opens; a push without data shows nothing", async () => {
    const worker = startWorker();

    await worker.dispatch("push", { data: { json: () => ({ title: "Sintel is ready", body: "Imported", tag: "import", url: "/movies/1" }) } });
    expect(worker.calls.shown).toEqual([["Sintel is ready", expect.objectContaining({ body: "Imported", tag: "import", data: { url: "/movies/1" } })]]);

    await worker.dispatch("push", { data: { json: () => ({}) } });
    expect(worker.calls.shown[1]).toEqual(["Deluno", expect.objectContaining({ body: "", tag: "deluno", data: { url: "/" } })]);

    await worker.dispatch("push", {});
    expect(worker.calls.shown).toHaveLength(2);
  });

  it("clicking a notification closes it, focuses the window already on that page, or opens one", async () => {
    const worker = startWorker();
    const close = vi.fn();

    await worker.dispatch("notificationclick", { notification: { close, data: { url: "http://deluno.test/queue" } } });
    expect(close).toHaveBeenCalled();
    expect(worker.calls.focused).toEqual(["http://deluno.test/queue"]);
    expect(worker.calls.opened).toEqual([]);

    await worker.dispatch("notificationclick", { notification: { close, data: { url: "/somewhere-else" } } });
    expect(worker.calls.opened).toEqual(["/somewhere-else"]);
  });
});
