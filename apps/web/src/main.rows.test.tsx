import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// #1329: what main.tsx does when the page loads: the service worker, and the theme a first visit gets.

vi.mock("./router", () => ({ router: { state: { navigation: { location: null } } } }));
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  RouterProvider: () => <p>the app</p>
}));

const register = vi.fn();

function installServiceWorkerApi(result: Promise<unknown>) {
  register.mockReset();
  register.mockReturnValue(result);
  Object.defineProperty(navigator, "serviceWorker", { configurable: true, value: { register } });
}

async function startApp() {
  vi.resetModules();
  document.body.innerHTML = '<div id="root"></div>';
  await import("./main");
}

function pretendOsPrefers(scheme: "light" | "dark") {
  window.matchMedia = ((query: string) => ({
    matches: query.includes("dark") ? scheme === "dark" : scheme === "light",
    media: query, onchange: null, addEventListener: () => undefined, removeEventListener: () => undefined, addListener: () => undefined, removeListener: () => undefined, dispatchEvent: () => false
  })) as unknown as typeof window.matchMedia;
}


const added: Array<[string, EventListenerOrEventListenerObject]> = [];

beforeEach(() => {
  localStorage.clear();
  pretendOsPrefers("dark");
  const original = window.addEventListener.bind(window);
  vi.spyOn(window, "addEventListener").mockImplementation((type: string, listener: EventListenerOrEventListenerObject, options?: boolean | AddEventListenerOptions) => {
    if (type === "load") added.push([type, listener]);
    original(type, listener, options);
  });
  vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response("{}", { status: 200 }));
});
afterEach(() => {
  for (const [type, listener] of added.splice(0)) window.removeEventListener(type, listener);
  vi.unstubAllEnvs();
  vi.restoreAllMocks();
  // @ts-expect-error the test installed it
  delete navigator.serviceWorker;
  document.documentElement.className = "";
});

describe("the service worker registers only in a production build (#1329)", () => {
  it("in production it registers /sw.js for the whole site once the page has loaded", async () => {
    vi.stubEnv("PROD", true);
    installServiceWorkerApi(Promise.resolve({}));
    await startApp();

    expect(register).not.toHaveBeenCalled();
    window.dispatchEvent(new Event("load"));
    expect(register).toHaveBeenCalledWith("/sw.js", { scope: "/" });
  });

  it("a registration that fails is ignored: no error reaches the page", async () => {
    vi.stubEnv("PROD", true);
    const unhandled = vi.fn();
    window.addEventListener("unhandledrejection", unhandled);
    installServiceWorkerApi(Promise.reject(new Error("blocked")));
    await startApp();

    window.dispatchEvent(new Event("load"));
    await new Promise((resolve) => setTimeout(resolve, 20)); // reason: let a rejection that is not handled reach the page's handler
    expect(register).toHaveBeenCalled();
    expect(unhandled).not.toHaveBeenCalled();
    window.removeEventListener("unhandledrejection", unhandled);
  });

  it("in a development build it does not register", async () => {
    vi.stubEnv("PROD", false);
    installServiceWorkerApi(Promise.resolve({}));
    await startApp();

    window.dispatchEvent(new Event("load"));
    expect(register).not.toHaveBeenCalled();
  });

  it("the install manifest names the app", () => {
    const manifest = JSON.parse(readFileSync(resolve(__dirname, "../public/manifest.json"), "utf8")) as { name: string; short_name: string; start_url: string };
    expect(manifest.name).toContain("Deluno");
    expect(manifest.short_name).toBe("Deluno");
    expect(manifest.start_url).toBe("/");
  });
});

describe("the theme a visit starts with (#1329)", () => {
  it("a first visit with nothing stored starts dark, whatever the OS says, because the provider's default is dark", async () => {
    pretendOsPrefers("light");
    await startApp();
    await vi.waitFor(() => expect(document.documentElement.classList.contains("dark")).toBe(true));
    expect(document.documentElement.classList.contains("light")).toBe(false);
  });

  it("a stored choice of 'system' follows the OS: light when the OS is light, dark when it is dark", async () => {
    localStorage.setItem("deluno-theme", "system");
    pretendOsPrefers("light");
    await startApp();
    await vi.waitFor(() => expect(document.documentElement.classList.contains("light")).toBe(true));

    document.documentElement.className = "";
    pretendOsPrefers("dark");
    await startApp();
    await vi.waitFor(() => expect(document.documentElement.classList.contains("dark")).toBe(true));
  });
});
