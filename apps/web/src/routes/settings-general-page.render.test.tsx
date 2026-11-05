import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api/types/settings";
import { authedFetch } from "../lib/use-auth";

/** Settings > General, every card: what it shows, what each control sends, and what it says when that fails. */

vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
const unsaved = vi.hoisted(() => vi.fn());
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: unsaved }));
const liveRefresh = vi.hoisted(() => vi.fn());
const toasts = vi.hoisted(() => ({ error: vi.fn(), success: vi.fn() }));
vi.mock("../components/shell/toaster", () => ({ toast: toasts }));
vi.mock("../lib/live-refresh", () => ({ useLiveRefresh: liveRefresh }));

const loaderData = { libraries: [], qualityProfiles: [], settings: { ...emptyPlatformSettingsSnapshot } as Record<string, unknown> };
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate: vi.fn() })
}));

const { SettingsGeneralPage } = await import("./settings-general-page");
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });

type Responder = (url: string, init: RequestInit | undefined) => Response | Promise<Response> | undefined;
let responder: Responder = () => undefined;
const definitions = { isEnabled: false, intervalHours: 24, lastCheckedUtc: null, lastUpdatedUtc: null, status: "disabled", error: null, updatedTrackerIds: [] as string[] };

beforeEach(() => {
  unsaved.mockReset();
  liveRefresh.mockReset();
  toasts.error.mockReset();
  toasts.success.mockReset();
  loaderData.settings = { ...emptyPlatformSettingsSnapshot, cloudflareHelperUrl: "", cloudflareHelperTimeoutSeconds: 60, cloudflareHelperKind: "auto", keepComputerAwake: false };
  responder = () => undefined;
  vi.mocked(authedFetch).mockReset();
  vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const custom = responder(url, init);
    if (custom) return custom;
    if (url.includes("/api/system/log-level")) return json({ level: "Information" });
    if (url.includes("/api/system/lan-access")) return json({ state: "open", port: 5099, ruleName: null, message: "Reachable from your network", canAllow: false });
    if (url.includes("/api/trackers/definitions/update-check")) return json(definitions);
    return json({});
  });
});
afterEach(cleanup);

const renderPage = () => render(<MemoryRouter><SettingsGeneralPage /></MemoryRouter>);
const calls = (url: string, method: string) => vi.mocked(authedFetch).mock.calls.filter(([target, init]) => target === url && ((init as RequestInit | undefined)?.method ?? "GET") === method);
const result = () => screen.queryAllByRole("status").find((node) => /text-(success|destructive)/.test(node.className));
const footerStatus = () => screen.getAllByRole("status").find((node) => /text-(success|warning|muted-foreground|destructive)/.test(node.className) && node.className.includes("inline-flex"))!;
const body = (url: string, method: string) => JSON.parse(String((calls(url, method).at(-1)![1] as RequestInit).body));

describe("the Cloudflare helper form", () => {
  it("starts from what is saved, and from plain defaults where nothing is", async () => {
    loaderData.settings = { ...emptyPlatformSettingsSnapshot, cloudflareHelperUrl: "http://flare:8191", cloudflareHelperTimeoutSeconds: 90, cloudflareHelperKind: "byparr" };
    renderPage();
    expect(screen.getByPlaceholderText("http://127.0.0.1:8191")).toHaveValue("http://flare:8191");
    expect(screen.getByPlaceholderText("60")).toHaveValue(90);
    expect(screen.getByDisplayValue("Byparr")).toBeInTheDocument();
    cleanup();
    loaderData.settings = { ...emptyPlatformSettingsSnapshot, cloudflareHelperUrl: null, cloudflareHelperTimeoutSeconds: null, cloudflareHelperKind: null };
    renderPage();
    expect(screen.getByPlaceholderText("http://127.0.0.1:8191")).toHaveValue("");
    expect(screen.getByPlaceholderText("60")).toHaveValue(60);
    expect(screen.getByDisplayValue("Auto-detect")).toBeInTheDocument();
  });

  it("refreshes itself when settings change elsewhere", () => {
    renderPage();
    expect(liveRefresh).toHaveBeenCalledWith({ events: ["SettingsChanged"] });
  });

  it("asks for the address without autofill or spell checking, as a code-like field", () => {
    renderPage();
    const url = screen.getByPlaceholderText("http://127.0.0.1:8191");
    expect(url).toHaveAttribute("autocomplete", "off");
    expect(url).toHaveAttribute("spellcheck", "false");
    expect(url.className).toContain("font-mono");
  });

  it("tells the page about unsaved changes while the form differs from what is saved", async () => {
    renderPage();
    expect(unsaved).toHaveBeenLastCalledWith(false);
    fireEvent.change(screen.getByPlaceholderText("http://127.0.0.1:8191"), { target: { value: "http://a" } });
    expect(unsaved).toHaveBeenLastCalledWith(true);
    fireEvent.change(screen.getByPlaceholderText("http://127.0.0.1:8191"), { target: { value: "" } });
    expect(unsaved).toHaveBeenLastCalledWith(false);
  });

  it("is clean again after taking a changed setting from the server", async () => {
    const view = renderPage();
    loaderData.settings = { ...loaderData.settings, cloudflareHelperUrl: "http://new" };
    view.rerender(<MemoryRouter><SettingsGeneralPage /></MemoryRouter>);
    await waitFor(() => expect(screen.getByPlaceholderText("http://127.0.0.1:8191")).toHaveValue("http://new"));
    expect(unsaved).toHaveBeenLastCalledWith(false);
  });

  it("keeps the browser from submitting the form itself", () => {
    const { container } = renderPage();
    expect(fireEvent.submit(container.querySelector("form")!)).toBe(false);
  });

  it("saves the default kind, auto, when none was saved", async () => {
    loaderData.settings = { ...emptyPlatformSettingsSnapshot, cloudflareHelperUrl: null, cloudflareHelperTimeoutSeconds: null, cloudflareHelperKind: null };
    renderPage();
    fireEvent.change(screen.getByPlaceholderText("http://127.0.0.1:8191"), { target: { value: "http://a" } });
    fireEvent.click(screen.getByRole("button", { name: /Save general settings/ }));
    await waitFor(() => expect(calls("/api/settings", "PATCH")).toHaveLength(1));
    expect(body("/api/settings", "PATCH")).toEqual({ cloudflareHelperUrl: "http://a", cloudflareHelperTimeoutSeconds: 60, cloudflareHelperKind: "auto" });
  });

  it("is saved, then clean again, when the person edits and puts it back", async () => {
    const view = renderPage();
    fireEvent.change(screen.getByPlaceholderText("http://127.0.0.1:8191"), { target: { value: "http://a" } });
    fireEvent.click(screen.getByRole("button", { name: /Save general settings/ }));
    await waitFor(() => expect(footerStatus()).toHaveClass("text-success"));
    loaderData.settings = { ...loaderData.settings, cloudflareHelperUrl: "http://a" };
    view.rerender(<MemoryRouter><SettingsGeneralPage /></MemoryRouter>);
    fireEvent.change(screen.getByPlaceholderText("http://127.0.0.1:8191"), { target: { value: "http://b" } });
    await waitFor(() => expect(footerStatus()).toHaveClass("text-warning"));
    fireEvent.change(screen.getByPlaceholderText("http://127.0.0.1:8191"), { target: { value: "http://a" } });
    await waitFor(() => expect(footerStatus()).toHaveClass("text-muted-foreground"));
    expect(footerStatus()).not.toHaveClass("text-success");
  });

  it("offers the three kinds of helper", () => {
    renderPage();
    const select = screen.getByDisplayValue("Auto-detect");
    expect(within(select).getAllByRole("option").map((option) => [option.getAttribute("value"), option.textContent])).toEqual([["auto", "Auto-detect"], ["flaresolverr", "FlareSolverr"], ["byparr", "Byparr"]]);
  });

  it("saves the address trimmed, the timeout as a number and the kind, and says it saved", async () => {
    renderPage();
    fireEvent.change(screen.getByPlaceholderText("http://127.0.0.1:8191"), { target: { value: "  http://flare:8191  " } });
    fireEvent.change(screen.getByPlaceholderText("60"), { target: { value: "120" } });
    fireEvent.change(screen.getByDisplayValue("Auto-detect"), { target: { value: "flaresolverr" } });
    fireEvent.click(screen.getByRole("button", { name: /Save general settings/ }));
    await waitFor(() => expect(calls("/api/settings", "PATCH")).toHaveLength(1));
    expect(body("/api/settings", "PATCH")).toEqual({ cloudflareHelperUrl: "http://flare:8191", cloudflareHelperTimeoutSeconds: 120, cloudflareHelperKind: "flaresolverr" });
    await waitFor(() => expect(screen.getByText("Saved just now")).toBeInTheDocument());
  });

  it("saves a blank timeout as sixty seconds", async () => {
    renderPage();
    fireEvent.change(screen.getByPlaceholderText("60"), { target: { value: "   " } });
    fireEvent.click(screen.getByRole("button", { name: /Save general settings/ }));
    await waitFor(() => expect(calls("/api/settings", "PATCH")).toHaveLength(1));
    expect(body("/api/settings", "PATCH").cloudflareHelperTimeoutSeconds).toBe(60);
  });

  it("forgets that it saved as soon as something changes again", async () => {
    renderPage();
    fireEvent.change(screen.getByPlaceholderText("http://127.0.0.1:8191"), { target: { value: "http://a" } });
    fireEvent.click(screen.getByRole("button", { name: /Save general settings/ }));
    await waitFor(() => expect(screen.getByText("Saved just now")).toBeInTheDocument());
    fireEvent.change(screen.getByPlaceholderText("http://127.0.0.1:8191"), { target: { value: "http://b" } });
    await waitFor(() => expect(screen.queryByText("Saved just now")).toBeNull());
  });

  it("keeps an edit made the instant the save finished, instead of putting back what the page loaded with", async () => {
    // CI failed the test above for this: the edit landed in the moment between the "Saved just now" appearing and
    // the page's effect (which takes the server's settings when they change) running, and the effect put the form
    // back to what was loaded. The edit is made here from a DOM observer, which runs at exactly that moment, so the
    // order no longer depends on how busy the machine is.
    renderPage();
    const input = screen.getByPlaceholderText("http://127.0.0.1:8191");
    fireEvent.change(input, { target: { value: "http://a" } });
    let edited = false;
    const observer = new MutationObserver(() => {
      if (edited || !screen.queryByText("Saved just now")) return;
      edited = true;
      fireEvent.change(input, { target: { value: "http://b" } });
    });
    observer.observe(document.body, { childList: true, subtree: true, characterData: true });
    fireEvent.click(screen.getByRole("button", { name: /Save general settings/ }));
    await waitFor(() => expect(edited).toBe(true));
    observer.disconnect();
    await waitFor(() => expect(footerStatus()).toHaveTextContent("Unsaved changes"));
    expect(input).toHaveValue("http://b");
  });

  it("says why it could not save, or says so plainly when the failure has no words", async () => {
    responder = (url, init) => (url === "/api/settings" && init?.method === "PATCH" ? json({ title: "Nope", errors: { cloudflareHelperUrl: ["Not a web address"] } }, 400) : undefined);
    renderPage();
    fireEvent.change(screen.getByPlaceholderText("http://127.0.0.1:8191"), { target: { value: "nonsense" } });
    fireEvent.click(screen.getByRole("button", { name: /Save general settings/ }));
    await waitFor(() => expect(screen.getAllByText("Not a web address").length).toBeGreaterThan(0));
    cleanup();
    responder = (url, init) => (url === "/api/settings" && init?.method === "PATCH" ? Promise.reject("boom") : undefined);
    renderPage();
    fireEvent.change(screen.getByPlaceholderText("http://127.0.0.1:8191"), { target: { value: "x" } });
    fireEvent.click(screen.getByRole("button", { name: /Save general settings/ }));
    await waitFor(() => expect(screen.getByText("Could not save")).toBeInTheDocument());
  });

  it("sends one save however many times it is submitted while it works", async () => {
    let finish: (value: Response) => void = () => undefined;
    responder = (url, init) => (url === "/api/settings" && init?.method === "PATCH" ? new Promise<Response>((resolve) => { finish = resolve; }) : undefined);
    const { container } = renderPage();
    fireEvent.change(screen.getByPlaceholderText("http://127.0.0.1:8191"), { target: { value: "x" } });
    const form = container.querySelector("form")!;
    fireEvent.submit(form);
    await waitFor(() => expect(calls("/api/settings", "PATCH")).toHaveLength(1));
    fireEvent.submit(form);
    await act(async () => { finish(json({})); });
    expect(calls("/api/settings", "PATCH")).toHaveLength(1);
  });

  it("takes a changed setting from the server while nothing is being edited, and keeps what is being edited", async () => {
    const view = renderPage();
    loaderData.settings = { ...loaderData.settings, cloudflareHelperUrl: "http://new" };
    view.rerender(<MemoryRouter><SettingsGeneralPage /></MemoryRouter>);
    await waitFor(() => expect(screen.getByPlaceholderText("http://127.0.0.1:8191")).toHaveValue("http://new"));
    fireEvent.change(screen.getByPlaceholderText("http://127.0.0.1:8191"), { target: { value: "http://mine" } });
    loaderData.settings = { ...loaderData.settings, cloudflareHelperUrl: "http://other" };
    view.rerender(<MemoryRouter><SettingsGeneralPage /></MemoryRouter>);
    expect(screen.getByPlaceholderText("http://127.0.0.1:8191")).toHaveValue("http://mine");
  });
});

describe("Test helper", () => {
  const testButton = () => screen.getByRole("button", { name: "Test helper" });
  const helperUrl = "/api/trackers/cloudflare-helper/test";

  it("needs an address to test", () => {
    renderPage();
    expect(testButton()).toBeDisabled();
    fireEvent.change(screen.getByPlaceholderText("http://127.0.0.1:8191"), { target: { value: "   " } });
    expect(testButton()).toBeDisabled();
    fireEvent.change(screen.getByPlaceholderText("http://127.0.0.1:8191"), { target: { value: "http://flare" } });
    expect(testButton()).not.toBeDisabled();
  });

  it("asks the helper at the address as typed, trimmed, and shows its good news in green", async () => {
    responder = (url) => (url === helperUrl ? json({ healthy: true, message: "FlareSolverr answered" }) : undefined);
    renderPage();
    fireEvent.change(screen.getByPlaceholderText("http://127.0.0.1:8191"), { target: { value: " http://flare " } });
    fireEvent.click(testButton());
    await waitFor(() => expect(result()).toBeDefined());
    const status = result()!;
    expect(status).toHaveTextContent("FlareSolverr answered");
    expect(status).toHaveClass("text-success");
    expect(body(helperUrl, "POST")).toEqual({ url: "http://flare" });
    expect(calls(helperUrl, "POST")[0][1]).toMatchObject({ headers: { "Content-Type": "application/json" } });
  });

  it("shows bad news in red", async () => {
    responder = (url) => (url === helperUrl ? json({ healthy: false, message: "Nothing there" }) : undefined);
    renderPage();
    fireEvent.change(screen.getByPlaceholderText("http://127.0.0.1:8191"), { target: { value: "http://flare" } });
    fireEvent.click(testButton());
    await waitFor(() => expect(result()).toBeDefined());
    expect(result()).toHaveClass("text-destructive");
  });

  it("says why it could not check, or says so plainly", async () => {
    responder = (url) => (url === helperUrl ? Promise.reject(new Error("Refused")) : undefined);
    renderPage();
    fireEvent.change(screen.getByPlaceholderText("http://127.0.0.1:8191"), { target: { value: "http://flare" } });
    fireEvent.click(testButton());
    await waitFor(() => expect(result()).toHaveTextContent("Refused"));
    expect(result()).toHaveClass("text-destructive");
    cleanup();
    responder = (url) => (url === helperUrl ? Promise.reject("boom") : undefined);
    renderPage();
    fireEvent.change(screen.getByPlaceholderText("http://127.0.0.1:8191"), { target: { value: "http://flare" } });
    fireEvent.click(testButton());
    await waitFor(() => expect(result()).toHaveTextContent("The helper could not be checked."));
  });

  it("holds the button while it checks, and forgets the answer when the address changes", async () => {
    let finish: (value: Response) => void = () => undefined;
    responder = (url) => (url === helperUrl ? new Promise<Response>((resolve) => { finish = resolve; }) : undefined);
    renderPage();
    fireEvent.change(screen.getByPlaceholderText("http://127.0.0.1:8191"), { target: { value: "http://flare" } });
    fireEvent.click(testButton());
    await waitFor(() => expect(testButton()).toBeDisabled());
    expect(testButton().querySelector("svg")).toHaveClass("animate-spin");
    await act(async () => { finish(json({ healthy: true, message: "Fine" })); });
    await waitFor(() => expect(testButton()).not.toBeDisabled());
    expect(testButton().querySelector("svg")).not.toHaveClass("animate-spin");
    expect(result()).toBeDefined();
    fireEvent.change(screen.getByPlaceholderText("http://127.0.0.1:8191"), { target: { value: "http://other" } });
    expect(result()).toBeUndefined();
  });
});

describe("How Deluno is reached", () => {
  const reach = (state: unknown) => { responder = (url) => (url === "/api/system/lan-access" ? json(state) : undefined); };

  it("says yes with the port when other devices can connect, and shows the reason", async () => {
    reach({ state: "open", port: 5099, message: "Reachable" });
    renderPage();
    expect(screen.getAllByDisplayValue("Loading…")).toHaveLength(4);
    expect(screen.queryByDisplayValue("Unavailable")).toBeNull();
    await waitFor(() => expect(screen.getByDisplayValue("5099")).toBeInTheDocument());
    expect(screen.getByDisplayValue("Yes")).toBeInTheDocument();
    expect(screen.getByText("Reachable")).toBeInTheDocument();
  });

  it("says no when they are blocked, unknown otherwise, and a dash for a port it does not know", async () => {
    reach({ state: "blocked", port: null, message: "" });
    renderPage();
    await waitFor(() => expect(screen.getByDisplayValue("No")).toBeInTheDocument());
    expect(screen.getByDisplayValue("—")).toBeInTheDocument();
    cleanup();
    reach({ state: "notChecked", port: 80, message: "" });
    renderPage();
    await waitFor(() => expect(screen.getByDisplayValue("Unknown from here")).toBeInTheDocument());
  });

  it("says unavailable when it cannot ask", async () => {
    responder = (url) => (url === "/api/system/lan-access" ? Promise.reject(new Error("down")) : undefined);
    renderPage();
    await waitFor(() => expect(screen.getAllByDisplayValue("Unavailable").length).toBe(2));
  });
});

describe("Log level", () => {
  const logUrl = "/api/system/log-level";
  const select = () => screen.getByLabelText("Log level") as HTMLSelectElement;

  it("shows the level in force, in plain names, and Loading before it knows", async () => {
    renderPage();
    expect(screen.getAllByDisplayValue("Loading…").length).toBeGreaterThan(0);
    await waitFor(() => expect(select().value).toBe("Information"));
    expect(select()).not.toBeDisabled();
    expect(within(select()).getAllByRole("option").map((option) => option.textContent)).toEqual(["Trace", "Debug", "Info", "Warning", "Error"]);
    expect(within(select()).getAllByRole("option").map((option) => option.getAttribute("value"))).toEqual(["Trace", "Debug", "Information", "Warning", "Error"]);
  });

  it("says unavailable when it cannot read the level", async () => {
    responder = (url) => (url === logUrl ? Promise.reject(new Error("down")) : undefined);
    renderPage();
    await waitFor(() => expect(screen.getAllByDisplayValue("Unavailable").length).toBeGreaterThan(0));
    expect(screen.queryByLabelText("Log level")).toBeNull();
  });

  it("sets the new level, holds the control while it does, and says it took effect", async () => {
    let finish: (value: Response) => void = () => undefined;
    renderPage();
    await waitFor(() => expect(select().value).toBe("Information"));
    responder = (url, init) => (url === logUrl && init?.method === "PUT" ? new Promise<Response>((resolve) => { finish = resolve; }) : undefined);
    fireEvent.change(select(), { target: { value: "Debug" } });
    await waitFor(() => expect(select()).toBeDisabled());
    expect(select().value).toBe("Debug");
    await act(async () => { finish(json({})); });
    await waitFor(() => expect(select()).not.toBeDisabled());
    expect(body(logUrl, "PUT")).toEqual({ level: "Debug" });
    expect(calls(logUrl, "PUT")[0][1]).toMatchObject({ headers: { "Content-Type": "application/json" } });
    expect(toasts.success).toHaveBeenCalledWith("Log level set to Debug — takes effect immediately.");
  });

  it("puts the old level back and says why when the server refuses", async () => {
    renderPage();
    await waitFor(() => expect(select().value).toBe("Information"));
    responder = (url, init) => (url === logUrl && init?.method === "PUT" ? new Response("Not allowed", { status: 403 }) : undefined);
    fireEvent.change(select(), { target: { value: "Trace" } });
    await waitFor(() => expect(toasts.error).toHaveBeenCalled());
    expect(String(toasts.error.mock.calls[0][0])).toMatch(/Not allowed|log level could not be changed/);
    expect(select().value).toBe("Information");
  });

  it("says plainly that the level could not be changed when the server refuses without saying why", async () => {
    renderPage();
    await waitFor(() => expect(select().value).toBe("Information"));
    responder = (url, init) => (url === logUrl && init?.method === "PUT" ? new Response("", { status: 500 }) : undefined);
    fireEvent.change(select(), { target: { value: "Error" } });
    await waitFor(() => expect(toasts.error).toHaveBeenCalledWith("The log level could not be changed."));
  });

  it("puts the old level back and says so plainly when the failure has no words", async () => {
    renderPage();
    await waitFor(() => expect(select().value).toBe("Information"));
    responder = (url, init) => (url === logUrl && init?.method === "PUT" ? Promise.reject("boom") : undefined);
    fireEvent.change(select(), { target: { value: "Error" } });
    await waitFor(() => expect(toasts.error).toHaveBeenCalledWith("The log level could not be changed."));
    expect(select().value).toBe("Information");
  });

  it("says what the server said when it fails with words", async () => {
    renderPage();
    await waitFor(() => expect(select().value).toBe("Information"));
    responder = (url, init) => (url === logUrl && init?.method === "PUT" ? Promise.reject(new Error("Offline")) : undefined);
    fireEvent.change(select(), { target: { value: "Error" } });
    await waitFor(() => expect(toasts.error).toHaveBeenCalledWith("Offline"));
  });
});

describe("Keep awake", () => {
  const toggle = () => screen.getByRole("switch", { name: "Keep this computer awake" });

  it("shows what is saved and follows a change to it", () => {
    loaderData.settings = { ...loaderData.settings, keepComputerAwake: true };
    const view = renderPage();
    expect(toggle()).toBeChecked();
    loaderData.settings = { ...loaderData.settings, keepComputerAwake: undefined };
    view.rerender(<MemoryRouter><SettingsGeneralPage /></MemoryRouter>);
    expect(toggle()).not.toBeChecked();
  });

  it("saves the moment it is switched on, and says what that does", async () => {
    renderPage();
    fireEvent.click(toggle());
    await waitFor(() => expect(toasts.success).toHaveBeenCalledWith("Deluno is keeping this computer awake."));
    expect(body("/api/settings", "PATCH")).toEqual({ keepComputerAwake: true });
    expect(calls("/api/settings", "PATCH")[0][1]).toMatchObject({ headers: { "Content-Type": "application/json" } });
    expect(toggle()).toBeChecked();
  });

  it("says what switching it off does", async () => {
    loaderData.settings = { ...loaderData.settings, keepComputerAwake: true };
    renderPage();
    fireEvent.click(toggle());
    await waitFor(() => expect(toasts.success).toHaveBeenCalledWith("Windows will sleep by its own timers again."));
    expect(body("/api/settings", "PATCH")).toEqual({ keepComputerAwake: false });
  });

  it("holds the switch while it saves", async () => {
    let finish: (value: Response) => void = () => undefined;
    responder = (url, init) => (url === "/api/settings" && init?.method === "PATCH" ? new Promise<Response>((resolve) => { finish = resolve; }) : undefined);
    renderPage();
    fireEvent.click(toggle());
    await waitFor(() => expect(toggle()).toBeDisabled());
    await act(async () => { finish(json({})); });
    await waitFor(() => expect(toggle()).not.toBeDisabled());
  });

  it("says plainly that it could not save when the server refuses without saying why", async () => {
    responder = (url, init) => (url === "/api/settings" && init?.method === "PATCH" ? new Response("", { status: 500 }) : undefined);
    renderPage();
    fireEvent.click(toggle());
    await waitFor(() => expect(toasts.error).toHaveBeenCalledWith("That could not be saved."));
  });

  it("switches back and says why when the server refuses, or plainly when it gives no reason", async () => {
    responder = (url, init) => (url === "/api/settings" && init?.method === "PATCH" ? new Response("Read only", { status: 500 }) : undefined);
    renderPage();
    fireEvent.click(toggle());
    await waitFor(() => expect(toasts.error).toHaveBeenCalled());
    expect(toggle()).not.toBeChecked();
    cleanup();
    toasts.error.mockReset();
    responder = (url, init) => (url === "/api/settings" && init?.method === "PATCH" ? Promise.reject("boom") : undefined);
    renderPage();
    fireEvent.click(toggle());
    await waitFor(() => expect(toasts.error).toHaveBeenCalledWith("That could not be saved."));
    cleanup();
    toasts.error.mockReset();
    responder = (url, init) => (url === "/api/settings" && init?.method === "PATCH" ? Promise.reject(new Error("Disk full")) : undefined);
    renderPage();
    fireEvent.click(toggle());
    await waitFor(() => expect(toasts.error).toHaveBeenCalledWith("Disk full"));
  });
});

describe("Tracker definitions", () => {
  const updateUrl = "/api/trackers/definitions/update-check";
  const state = (overrides: object) => { responder = (url, init) => (url === updateUrl && !init?.method ? json({ ...definitions, ...overrides }) : undefined); };
  const toggle = () => screen.getByRole("switch", { name: "Update tracker definitions automatically" });
  const updateNow = () => screen.getByRole("button", { name: "Update now" });
  const lastUpdated = () => screen.getByText("Definitions last updated").closest("div")!.parentElement!.querySelector("input")!;

  it("waits for the state, with its switch and button held until it arrives", async () => {
    renderPage();
    expect(toggle()).toBeDisabled();
    expect(toggle()).not.toBeChecked();
    expect(updateNow()).toBeDisabled();
    expect(lastUpdated()).toHaveValue("Loading…");
    await waitFor(() => expect(toggle()).not.toBeDisabled());
    expect(updateNow()).not.toBeDisabled();
  });

  it("says unavailable when it cannot read the state", async () => {
    responder = (url) => (url === updateUrl ? Promise.reject(new Error("down")) : undefined);
    renderPage();
    await waitFor(() => expect(lastUpdated()).toHaveValue("Unavailable"));
    expect(toggle()).toBeDisabled();
  });

  it("says when the definitions were last updated, that nothing has changed yet, or never", async () => {
    state({ lastUpdatedUtc: "2026-10-31T10:00:00Z", lastCheckedUtc: "2026-10-31T10:00:00Z" });
    renderPage();
    await waitFor(() => expect(lastUpdated().value).toMatch(/2026|Oct|31/));
    cleanup();
    state({ lastCheckedUtc: "2026-10-31T10:00:00Z" });
    renderPage();
    await waitFor(() => expect(lastUpdated()).toHaveValue("No changes found yet"));
    cleanup();
    state({});
    renderPage();
    await waitFor(() => expect(lastUpdated()).toHaveValue("Never"));
  });

  it("shows the interval only while it is on, starting from the saved hours", async () => {
    state({ isEnabled: true, intervalHours: 6 });
    renderPage();
    await waitFor(() => expect(screen.getByDisplayValue("6")).toBeInTheDocument());
    expect(toggle()).toBeChecked();
    cleanup();
    state({ isEnabled: false });
    renderPage();
    await waitFor(() => expect(toggle()).not.toBeDisabled());
    expect(screen.queryByText("Check every")).toBeNull();
  });

  it("turns the schedule on with the hours shown, or a day when they are not a number", async () => {
    renderPage();
    await waitFor(() => expect(toggle()).not.toBeDisabled());
    responder = (url, init) => (url === updateUrl && init?.method === "PUT" ? json({ ...definitions, isEnabled: true, intervalHours: 24 }) : undefined);
    fireEvent.click(toggle());
    await waitFor(() => expect(calls(updateUrl, "PUT")).toHaveLength(1));
    expect(body(updateUrl, "PUT")).toEqual({ isEnabled: true, intervalHours: 24 });
    expect(calls(updateUrl, "PUT")[0][1]).toMatchObject({ headers: { "Content-Type": "application/json" } });
    await waitFor(() => expect(toggle()).toBeChecked());
  });

  it("saves the hours when the field is left, and a day when they are blank or not a number", async () => {
    state({ isEnabled: true, intervalHours: 6 });
    renderPage();
    const hours = await screen.findByDisplayValue("6");
    responder = (url, init) => (url === updateUrl && init?.method === "PUT" ? json({ ...definitions, isEnabled: true, intervalHours: 12 }) : undefined);
    fireEvent.change(hours, { target: { value: "12" } });
    fireEvent.blur(hours);
    await waitFor(() => expect(calls(updateUrl, "PUT")).toHaveLength(1));
    expect(body(updateUrl, "PUT")).toEqual({ isEnabled: true, intervalHours: 12 });
    await waitFor(() => expect(screen.getByDisplayValue("12")).toBeInTheDocument());
    fireEvent.change(screen.getByDisplayValue("12"), { target: { value: "" } });
    fireEvent.blur(screen.getAllByRole("spinbutton").at(-1)!);
    await waitFor(() => expect(calls(updateUrl, "PUT")).toHaveLength(2));
    expect(body(updateUrl, "PUT").intervalHours).toBe(24);
  });

  it("turns the schedule off with the hours shown, or a day when the field is blank", async () => {
    state({ isEnabled: true, intervalHours: 6 });
    renderPage();
    const hours = await screen.findByDisplayValue("6");
    responder = (url, init) => (url === updateUrl && init?.method === "PUT" ? json({ ...definitions, isEnabled: false, intervalHours: 6 }) : undefined);
    fireEvent.change(hours, { target: { value: "" } });
    fireEvent.click(toggle());
    await waitFor(() => expect(calls(updateUrl, "PUT")).toHaveLength(1));
    expect(body(updateUrl, "PUT")).toEqual({ isEnabled: false, intervalHours: 24 });
  });

  it("shows the hours the server settled on after a save", async () => {
    state({ isEnabled: true, intervalHours: 6 });
    renderPage();
    const hours = await screen.findByDisplayValue("6");
    responder = (url, init) => (url === updateUrl && init?.method === "PUT" ? json({ ...definitions, isEnabled: true, intervalHours: 8 }) : undefined);
    fireEvent.change(hours, { target: { value: "12" } });
    fireEvent.blur(hours);
    await waitFor(() => expect(screen.getByDisplayValue("8")).toBeInTheDocument());
    expect(screen.queryByDisplayValue("12")).toBeNull();
  });

  it("holds the controls while it saves, and tells why when the save fails", async () => {
    renderPage();
    await waitFor(() => expect(toggle()).not.toBeDisabled());
    let finish: (value: Response) => void = () => undefined;
    responder = (url, init) => (url === updateUrl && init?.method === "PUT" ? new Promise<Response>((resolve) => { finish = resolve; }) : undefined);
    fireEvent.click(toggle());
    await waitFor(() => expect(toggle()).toBeDisabled());
    await act(async () => { finish(new Response("Cannot save", { status: 500 })); });
    await waitFor(() => expect(toasts.error).toHaveBeenCalled());
    await waitFor(() => expect(toggle()).not.toBeDisabled());
    toasts.error.mockReset();
    responder = (url, init) => (url === updateUrl && init?.method === "PUT" ? Promise.reject("boom") : undefined);
    fireEvent.click(toggle());
    await waitFor(() => expect(toasts.error).toHaveBeenCalledWith("The tracker-definition update setting could not be saved."));
    toasts.error.mockReset();
    responder = (url, init) => (url === updateUrl && init?.method === "PUT" ? Promise.reject(new Error("Offline")) : undefined);
    fireEvent.click(toggle());
    await waitFor(() => expect(toasts.error).toHaveBeenCalledWith("Offline"));
  });

  const run = (reply: object | Promise<Response>) => {
    responder = (url, init) => (url === `${updateUrl}/run` && init?.method === "POST" ? (reply instanceof Promise ? reply : json(reply)) : undefined);
  };

  it("updates now and says how many definitions changed", async () => {
    renderPage();
    await waitFor(() => expect(updateNow()).not.toBeDisabled());
    run({ ...definitions, status: "updated", updatedTrackerIds: ["a", "b"] });
    fireEvent.click(updateNow());
    await waitFor(() => expect(toasts.success).toHaveBeenCalledWith("Updated 2 tracker definitions."));
    run({ ...definitions, status: "updated", updatedTrackerIds: ["a"] });
    fireEvent.click(updateNow());
    await waitFor(() => expect(toasts.success).toHaveBeenLastCalledWith("Updated 1 tracker definition."));
  });

  it("says they are already up to date when nothing changed", async () => {
    renderPage();
    await waitFor(() => expect(updateNow()).not.toBeDisabled());
    run({ ...definitions, status: "up-to-date" });
    fireEvent.click(updateNow());
    await waitFor(() => expect(toasts.success).toHaveBeenCalledWith("Tracker definitions are already up to date."));
  });

  it("says why an update failed, in the person's time, or plainly when there is no reason", async () => {
    renderPage();
    await waitFor(() => expect(updateNow()).not.toBeDisabled());
    run({ ...definitions, status: "failed", error: "GitHub is busy" });
    fireEvent.click(updateNow());
    await waitFor(() => expect(toasts.error).toHaveBeenCalledWith("GitHub is busy"));
    expect(result()).toHaveTextContent("GitHub is busy");
    expect(result()).toHaveClass("text-destructive");
    run({ ...definitions, status: "failed", error: null });
    fireEvent.click(updateNow());
    await waitFor(() => expect(toasts.error).toHaveBeenLastCalledWith("The tracker definitions could not be updated."));
    expect(result()).toBeUndefined();
  });

  it("holds the button while it updates, and says why it could not", async () => {
    renderPage();
    await waitFor(() => expect(updateNow()).not.toBeDisabled());
    let finish: (value: Response) => void = () => undefined;
    run(new Promise<Response>((resolve) => { finish = resolve; }));
    fireEvent.click(updateNow());
    await waitFor(() => expect(updateNow()).toBeDisabled());
    expect(updateNow().querySelector("svg")).toHaveClass("animate-spin");
    await act(async () => { finish(json({ ...definitions, status: "up-to-date" })); });
    await waitFor(() => expect(updateNow()).not.toBeDisabled());
    expect(updateNow().querySelector("svg")).not.toHaveClass("animate-spin");
    responder = (url, init) => (url === `${updateUrl}/run` && init?.method === "POST" ? Promise.reject("boom") : undefined);
    fireEvent.click(updateNow());
    await waitFor(() => expect(toasts.error).toHaveBeenCalledWith("The tracker definitions could not be updated."));
    responder = (url, init) => (url === `${updateUrl}/run` && init?.method === "POST" ? Promise.reject(new Error("Offline")) : undefined);
    fireEvent.click(updateNow());
    await waitFor(() => expect(toasts.error).toHaveBeenLastCalledWith("Offline"));
  });
});
