import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { CompanionTool, CompanionToolsResponse } from "./companion-tools-merge";

const fetchJson = vi.hoisted(() => vi.fn());
const authedFetch = vi.hoisted(() => vi.fn());
vi.mock("../lib/api", async (importOriginal) => ({ ...(await importOriginal<object>()), fetchJson }));
vi.mock("../lib/use-auth", async (importOriginal) => ({ ...(await importOriginal<object>()), authedFetch }));
vi.mock("../lib/use-signalr", async (importOriginal) => ({ ...(await importOriginal<object>()), useSignalREvent: vi.fn() }));

const { CompanionToolsPage } = await import("./companion-tools-screen");

const tool = (over: Partial<CompanionTool> & { id: string; name: string }): CompanionTool => ({
  description: `${over.name} description`, state: "not-installed", connectionName: null, url: null, job: null, ...over
});
const job = (status: string, message: string) => ({ status, step: "x", message, updatedUtc: "2026-10-19T00:00:00Z" });

function response(tools: CompanionTool[], availability: CompanionToolsResponse["availability"] = { available: true, reason: null }): CompanionToolsResponse {
  return { availability, tools, uninstallNote: "To remove a tool, uninstall it in Windows." };
}

function renderPage(data: CompanionToolsResponse, installed: unknown[] = []) {
  fetchJson.mockImplementation(async (url: string) => (url === "/api/companion-tools" ? data : installed));
  const router = createMemoryRouter([{ path: "/", element: <CompanionToolsPage />, loader: () => data }], { initialEntries: ["/"] });
  return render(<RouterProvider router={router} />);
}

const box = (name: string) => screen.findByRole("checkbox", { name: `Set up ${name}` }) as Promise<HTMLInputElement>;
const toolbar = () => screen.findByRole("button", { name: /^Set up (tools|\d+ tools?)$/ }) as Promise<HTMLButtonElement>;
const ok = () => new Response("{}", { status: 202 });
const refusal = (status: number, message?: string) => new Response(message ? JSON.stringify({ message }) : "{}", { status });
const isChecked = (element: HTMLElement) => element.getAttribute("aria-checked") === "true" || (element as HTMLInputElement).checked === true;
const isDisabled = (element: HTMLElement) => element.hasAttribute("disabled") || element.getAttribute("aria-disabled") === "true" || element.hasAttribute("data-disabled");

afterEach(() => { cleanup(); fetchJson.mockReset(); authedFetch.mockReset(); });

const SIX = [
  tool({ id: "sabnzbd", name: "SABnzbd" }),
  tool({ id: "qbittorrent", name: "qBittorrent", state: "installed" }),
  tool({ id: "weir", name: "Weir", state: "connected", connectionName: "Weir" })
];

describe("the Companion Tools list", () => {
  it("shows each tool with the five columns, its name, description and state sentence", async () => {
    renderPage(response(SIX));
    for (const label of ["Tool", "On this computer", "Last step", "Status", "Set up"]) {
      expect((await screen.findAllByText(label)).length, label).toBeGreaterThan(0);
    }
    expect(screen.getByText("SABnzbd description")).toBeTruthy();
    expect(screen.getAllByText("Not installed").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Installed").length).toBeGreaterThan(0);
    expect(screen.getByText("Connected as “Weir”")).toBeTruthy();
    expect(screen.getByText("1 of 3 connected")).toBeTruthy();
  });

  it.each([
    ["running", "Setting up", tool({ id: "sabnzbd", name: "SABnzbd", job: job("running", "Downloading") })],
    ["queued", "Waiting its turn", tool({ id: "sabnzbd", name: "SABnzbd", job: job("queued", "Waiting") })],
    ["failed", "Needs you", tool({ id: "sabnzbd", name: "SABnzbd", job: job("failed", "It did not install") })],
    ["installed", "Installed, not connected", tool({ id: "sabnzbd", name: "SABnzbd", state: "installed" })],
    ["connected", "Connected", tool({ id: "sabnzbd", name: "SABnzbd", state: "connected" })],
    ["not installed", "Not installed", tool({ id: "sabnzbd", name: "SABnzbd" })]
  ])("status chip for a %s tool reads %s", async (_name, label, row) => {
    renderPage(response([row]));
    await screen.findByText("SABnzbd");
    expect(screen.getAllByText(label).length).toBeGreaterThan(0);
  });

  it("shows the last step message of a tool that has a job", async () => {
    renderPage(response([tool({ id: "sabnzbd", name: "SABnzbd", job: job("failed", "The download was the wrong build") })]));
    expect(await screen.findByText("The download was the wrong build")).toBeTruthy();
  });

  it("says why when this Deluno cannot install, and disables every tick box and the button", async () => {
    renderPage(response(SIX, { available: false, reason: "Installing tools works on Windows only." }));
    expect(await screen.findByText("Installing tools works on Windows only.")).toBeTruthy();
    expect(isDisabled(await box("SABnzbd"))).toBe(true);
    expect(isDisabled(await box("qBittorrent"))).toBe(true);
    expect(isDisabled(await toolbar())).toBe(true);
  });
});

describe("tick boxes and the toolbar button", () => {
  it("is disabled for a connected tool, enabled for the others", async () => {
    renderPage(response(SIX));
    expect(isDisabled(await box("SABnzbd"))).toBe(false);
    expect(isDisabled(await box("qBittorrent"))).toBe(false);
    expect(isDisabled(await box("Weir"))).toBe(true);
  });

  it("is disabled for every tool while one is queued or running", async () => {
    renderPage(response([tool({ id: "sabnzbd", name: "SABnzbd", job: job("running", "Downloading") }), tool({ id: "qbittorrent", name: "qBittorrent" })]));
    expect(isDisabled(await box("qBittorrent"))).toBe(true);
    expect(isDisabled(await toolbar())).toBe(true);
  });

  it("is disabled for an app the installer already put on this computer", async () => {
    renderPage(response([tool({ id: "deluge", name: "Deluge", state: "installed", installedByInstaller: true })]));
    expect(isDisabled(await box("Deluge"))).toBe(true);
  });

  it("reads Set up tools with nothing ticked, Set up 1 tool for one and Set up 2 tools for two", async () => {
    renderPage(response(SIX));
    const button = await toolbar();
    expect(button.textContent).toBe("Set up tools");
    expect(isDisabled(button)).toBe(true);
    fireEvent.click(await box("SABnzbd"));
    expect((await toolbar()).textContent).toBe("Set up 1 tool");
    expect(isDisabled(await toolbar())).toBe(false);
    fireEvent.click(await box("qBittorrent"));
    expect((await toolbar()).textContent).toBe("Set up 2 tools");
    fireEvent.click(await box("SABnzbd"));
    expect((await toolbar()).textContent).toBe("Set up 1 tool");
  });
});

describe("pressing Set up", () => {
  it("posts the ticked own tools to the install route, clears the ticks and reloads the list", async () => {
    authedFetch.mockResolvedValue(ok());
    renderPage(response(SIX));
    fireEvent.click(await box("SABnzbd"));
    fireEvent.click(await box("qBittorrent"));
    fireEvent.click(await toolbar());
    await waitFor(() => expect(authedFetch).toHaveBeenCalledTimes(1));
    const [url, init] = authedFetch.mock.calls[0];
    expect(url).toBe("/api/companion-tools/install");
    expect(init.method).toBe("POST");
    expect(JSON.parse(init.body)).toEqual({ toolIds: ["sabnzbd", "qbittorrent"] });
    await waitFor(async () => expect(isChecked(await box("SABnzbd"))).toBe(false));
    expect((await toolbar()).textContent).toBe("Set up tools");
    expect(fetchJson).toHaveBeenCalledWith("/api/companion-tools");
  });

  it("posts a mixed selection to both routes in turn: own tools first, tray-only apps second, with the tray notice", async () => {
    authedFetch.mockResolvedValue(ok());
    renderPage(response([tool({ id: "sabnzbd", name: "SABnzbd" }), tool({ id: "deluge", name: "Deluge", installedByTray: true })]));
    fireEvent.click(await box("SABnzbd"));
    fireEvent.click(await box("Deluge"));
    fireEvent.click(await toolbar());
    await waitFor(() => expect(authedFetch).toHaveBeenCalledTimes(2));
    expect(authedFetch.mock.calls.map((call) => [call[0], JSON.parse(call[1].body)])).toEqual([
      ["/api/companion-tools/install", { toolIds: ["sabnzbd"] }],
      ["/api/companion-tools/tray-install", { toolIds: ["deluge"] }]
    ]);
    expect(await screen.findByText(/Asked the Deluno icon in your taskbar to install Deluge\./)).toBeTruthy();
  });

  it("posts a tray-only app to the tray route alone", async () => {
    authedFetch.mockResolvedValue(ok());
    renderPage(response([tool({ id: "nzbget", name: "NZBGet", installedByTray: true })]));
    fireEvent.click(await box("NZBGet"));
    fireEvent.click(await toolbar());
    await waitFor(() => expect(authedFetch).toHaveBeenCalledTimes(1));
    expect(authedFetch.mock.calls[0][0]).toBe("/api/companion-tools/tray-install");
    expect(JSON.parse(authedFetch.mock.calls[0][1].body)).toEqual({ toolIds: ["nzbget"] });
  });

  it("stops at the first refusal and shows the server's words", async () => {
    authedFetch.mockResolvedValueOnce(refusal(409, "Tools are already being set up. Wait for that to finish."));
    renderPage(response([tool({ id: "sabnzbd", name: "SABnzbd" }), tool({ id: "deluge", name: "Deluge", installedByTray: true })]));
    fireEvent.click(await box("SABnzbd"));
    fireEvent.click(await box("Deluge"));
    fireEvent.click(await toolbar());
    expect((await screen.findByRole("alert")).textContent).toBe("Tools are already being set up. Wait for that to finish.");
    expect(authedFetch).toHaveBeenCalledTimes(1);
  });

  it("shows the status code when a refusal carries no message, and a plain sentence when the request fails", async () => {
    authedFetch.mockResolvedValueOnce(refusal(400));
    renderPage(response([tool({ id: "sabnzbd", name: "SABnzbd" })]));
    fireEvent.click(await box("SABnzbd"));
    fireEvent.click(await toolbar());
    expect((await screen.findByRole("alert")).textContent).toBe("Setting up could not start (HTTP 400).");

    authedFetch.mockRejectedValueOnce(new Error("offline"));
    fireEvent.click(await toolbar());
    await waitFor(() => expect(screen.getByRole("alert").textContent).toBe("Setting up could not start. Deluno could not be reached."));
  });
});

describe("opening a row", () => {
  it("shows a drawer with the job message as the status", async () => {
    renderPage(response([tool({ id: "sabnzbd", name: "SABnzbd", job: job("running", "Downloading the installer") })]));
    fireEvent.click(await screen.findByText("SABnzbd"));
    const drawer = await screen.findByRole("dialog");
    expect(within(drawer).getByText("Status")).toBeTruthy();
    expect(within(drawer).getByText("Downloading the installer")).toBeTruthy();
    expect(within(drawer).getByLabelText("Working")).toBeTruthy();
  });

  it.each([
    [tool({ id: "sabnzbd", name: "SABnzbd" }), "Not installed. Tick it and set it up."],
    [tool({ id: "sabnzbd", name: "SABnzbd", state: "installed" }), "Installed on this computer but not connected to Deluno yet. Tick it and set it up to connect it."],
    [tool({ id: "sabnzbd", name: "SABnzbd", state: "connected", connectionName: "My SAB" }), "Connected to Deluno as “My SAB”."]
  ])("with no job it states the state in a sentence", async (row, sentence) => {
    renderPage(response([row]));
    fireEvent.click(await screen.findByText("SABnzbd"));
    expect(within(await screen.findByRole("dialog")).getByText(sentence)).toBeTruthy();
  });

  it("offers Open Weir for Weir only", async () => {
    renderPage(response([tool({ id: "weir", name: "Weir", state: "connected", url: "http://127.0.0.1:7878" })]));
    fireEvent.click(await screen.findByText("Weir"));
    const link = within(await screen.findByRole("dialog")).getByRole("link", { name: /Open Weir/ }) as HTMLAnchorElement;
    expect(link.getAttribute("href")).toBe("http://127.0.0.1:7878");
  });
});
