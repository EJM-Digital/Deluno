import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

const fetchJson = vi.hoisted(() => vi.fn());
const authedFetch = vi.hoisted(() => vi.fn());
vi.mock("../lib/api", async (importOriginal) => ({ ...(await importOriginal<object>()), fetchJson }));
vi.mock("../lib/api/client", async (importOriginal) => ({ ...(await importOriginal<object>()), fetchJson }));
vi.mock("../lib/use-auth", async (importOriginal) => ({ ...(await importOriginal<object>()), authedFetch }));

const { SetupGuidePage, STEPS } = await import("./setup-guide-page");

type Over = {
  stepId: string; draft?: Record<string, unknown>; settings?: Record<string, unknown>;
  indexers?: unknown[]; clients?: unknown[]; apps?: unknown[]; completed?: boolean;
};

const GUIDE = { id: "g", name: "g", version: 1, schemaVersion: 1, source: {}, integritySha256: "", qualityTiers: [], qualityProfiles: [], customFormats: [], bundles: [] };

function loaderData(over: Over) {
  return {
    settings: { movieRootPath: "D:/Media/Movies", seriesRootPath: "D:/Media/TV", timezone: "UTC", ...over.settings },
    libraries: [], qualityProfiles: [], customFormats: [], indexers: over.indexers ?? [], clients: over.clients ?? [], metadataStatus: null,
    progress: { lastCompletedStep: STEPS.findIndex((step) => step.id === over.stepId), isSkipped: false, isCompleted: over.completed ?? false, updatedUtc: "2026-10-19T00:00:00Z" },
    draft: {
      mode: "simple", mediaIntent: "both", movieRootPath: "", seriesRootPath: "", movieDownloadsPath: "", seriesDownloadsPath: "",
      qualityPreset: "", formatGoal: "", indexerName: "", indexerProtocol: "", indexerUrl: "",
      clientName: "", clientProtocol: "", clientHost: "", clientPort: "", backupEnabled: true,
      firstTitleType: "movies", firstTitle: "", firstTitleYear: "", firstTitleMonitored: true, ...over.draft
    },
    guide: GUIDE,
    allowedHosts: { enabled: false, allowedHosts: [], detectedHosts: [], currentHost: "localhost" },
    contentBlocklist: { enabled: false, mode: "block", patterns: [] },
    localNetworkAuth: { enabled: false, additionalLocalNetworks: [], allowList: [], trustedProxies: [], currentAddress: "192.168.1.20", currentAddressIsLocal: true, currentAddressTrusted: true, allowedHostsEnabled: false },
    updatePreferences: { mode: "notify-only", channel: "stable", autoCheck: true }
  };
}

const okJson = (body: unknown = {}) => new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } });

function renderStep(over: Over, draftSave: () => Promise<Response> = async () => okJson()) {
  fetchJson.mockImplementation(async (url: string) => (url === "/api/system/bundled-components" ? over.apps ?? [] : url === "/api/download-clients" ? over.clients ?? [] : []));
  authedFetch.mockImplementation(async (url: string, init?: RequestInit) => (url === "/api/setup/draft" && init?.method === "PUT" ? draftSave() : okJson()));
  const router = createMemoryRouter([
    { path: "/", element: <SetupGuidePage />, loader: () => loaderData(over) },
    { path: "*", element: <p>Elsewhere</p> }
  ], { initialEntries: ["/"] });
  return { router, ...render(<RouterProvider router={router} />) };
}

const draftPuts = () => authedFetch.mock.calls.filter(([url, init]) => url === "/api/setup/draft" && init?.method === "PUT");
const railButton = (label: string) => screen.findByRole("button", { name: new RegExp(`${label}\\s*[A-Z]`) }) as Promise<HTMLButtonElement>;

afterEach(() => { cleanup(); fetchJson.mockReset(); authedFetch.mockReset(); });

describe("the guide shell and step rail, low rows (#1329)", () => {
  it("steps before the current one show a tick, later ones show their number, and the active step is highlighted", async () => {
    renderStep({ stepId: "folders" });
    const current = STEPS.findIndex((step) => step.id === "folders");
    const before = await railButton("Your account");
    const later = await railButton("Library Profiles");
    const active = await railButton("Folders");

    expect(before.querySelector("svg")).toBeTruthy();
    expect(before.textContent).not.toMatch(/^1/);
    expect(later.textContent?.startsWith(String(STEPS.findIndex((step) => step.id === "quality") + 1))).toBe(true);
    expect(later.querySelector("svg")).toBeNull();
    expect(active.textContent?.startsWith(String(current + 1))).toBe(true);
    expect(active.className).toMatch(/border-primary\/40/);
    expect(before.className).not.toMatch(/border-primary\/40/);
    expect(later.className).not.toMatch(/border-primary\/40/);
  });

  it("the header badge reads Simple path on the simple mode and Advanced skip on the advanced one", async () => {
    renderStep({ stepId: "account" });
    expect(await screen.findByText("Simple path")).toBeTruthy();
    cleanup();

    renderStep({ stepId: "account", draft: { mode: "advanced" } });
    expect(await screen.findByText("Advanced skip")).toBeTruthy();
    expect(screen.queryByText("Simple path")).toBeNull();
  });

  it("the Open Media Management ghost link opens Settings", async () => {
    renderStep({ stepId: "account" });
    const link = await screen.findByRole("link", { name: "Open Media Management" });
    expect(link.getAttribute("href")).toBe("/settings");
  });

  it("a draft save that fails shows nothing and the next change tries to save again", async () => {
    const save = vi.fn().mockRejectedValue(new Error("offline"));
    renderStep({ stepId: "signIn" }, save);
    fireEvent.click(await screen.findByRole("radio", { name: "Skip sign-in for devices on your home network? No" }));
    await waitFor(() => expect(save).toHaveBeenCalledTimes(1), { timeout: 3000 });
    expect(screen.queryByRole("alert")).toBeNull();

    fireEvent.click(screen.getByRole("radio", { name: "Skip sign-in for devices on your home network? Yes" }));

    await waitFor(() => expect(save).toHaveBeenCalledTimes(2), { timeout: 3000 });
    expect(screen.queryByRole("alert")).toBeNull();
    expect(JSON.parse(draftPuts().at(-1)![1].body).skipSignInLocalNetwork).toBe(true);
  });

  it("the roots fall back to the saved settings, and the search source and download client to the first saved connection, when the draft holds none", async () => {
    renderStep({
      stepId: "folders",
      settings: { movieRootPath: "E:/Saved/Films", seriesRootPath: "E:/Saved/Shows" },
      clients: [{ id: "c1", name: "Box", protocol: "sabnzbd", host: "nas.local", port: 8085, username: "me", isEnabled: false, healthStatus: "unknown" }],
      indexers: [{ id: "i1", name: "First", protocol: "newznab", baseUrl: "https://first.example/api", apiKey: "k", isEnabled: true, healthStatus: "healthy" }, { id: "i2", name: "Second", protocol: "torznab", baseUrl: "https://second.example/api", apiKey: "k2", isEnabled: true, healthStatus: "healthy" }]
    });
    expect((await screen.findByDisplayValue("E:/Saved/Films") as HTMLInputElement).value).toBe("E:/Saved/Films");
    expect(screen.getByDisplayValue("E:/Saved/Shows")).toBeTruthy();
    fireEvent.click(await railButton("Find & Download"));

    expect((await screen.findByDisplayValue("https://first.example/api") as HTMLInputElement).value).toBe("https://first.example/api");
    expect((screen.getByLabelText("Indexer protocol") as HTMLSelectElement).value).toBe("newznab");
    expect(screen.queryByDisplayValue("https://second.example/api")).toBeNull();
    expect((screen.getByLabelText("Download client host") as HTMLInputElement).value).toBe("nas.local");
    expect((screen.getByLabelText("Download client port") as HTMLInputElement).value).toBe("8085");
    expect((screen.getByLabelText("Download client protocol") as HTMLSelectElement).value).toBe("sabnzbd");
    expect((screen.getByLabelText("Download client username") as HTMLInputElement).value).toBe("me");
  });

  it("the Readiness card lists each check with Done, Waiting or Skipped and counts how many are done", async () => {
    renderStep({
      stepId: "connectApps",
      draft: { movieRootPath: "D:/Media/Movies", qualityPreset: "balanced1080p", formatGoal: "balanced" },
      apps: [{ kind: "qbittorrent", displayName: "qBittorrent", category: "download-client", installed: true, reachable: true, connected: false, defaultHost: "localhost", defaultPort: 8080 }]
    });
    const card = (await screen.findByText("Readiness")).parentElement!.parentElement!;
    const row = (label: string) => within(card).getByText(label).parentElement!;

    for (const label of ["Account created", "Media root chosen", "Library Profile chosen", "Release preferences chosen", "Search source ready", "Downloads ready"]) {
      expect(within(card).getByText(label), label).toBeTruthy();
    }
    expect(row("Account created").textContent).toContain("Done");
    expect(row("Media root chosen").textContent).toContain("Done");
    expect(row("Library Profile chosen").textContent).toContain("Done");
    expect(row("Release preferences chosen").textContent).toContain("Done");
    expect(row("Search source ready").textContent).toContain("Waiting");
    expect(row("Downloads ready").textContent).toContain("Waiting");
    await waitFor(() => expect(within(card).getByText("Installed apps connected")).toBeTruthy());
    expect(row("Installed apps connected").textContent).toContain("Waiting");
    expect(card.textContent).toContain("4 of 7 done");

    fireEvent.click(screen.getByRole("button", { name: "Skip connecting for now" }));

    await waitFor(() => expect(row("Installed apps connected").textContent).toContain("Skipped"));
    expect(row("Installed apps connected").textContent).not.toContain("Done");
    expect(screen.getByText("Skipped. You can connect them later from Connections.")).toBeTruthy();
    expect((screen.getByRole("button", { name: "Skip connecting for now" }) as HTMLButtonElement).disabled).toBe(true);
    expect(card.textContent).toContain("4 of 7 done");
  });

  it("the Your library step has Simple configuration selected by default and choosing it sets the simple mode again", async () => {
    renderStep({ stepId: "mode" });
    const simple = await screen.findByRole("button", { name: /^Simple configuration/ });
    expect(simple.className).toMatch(/border-primary\/40/);
    expect(screen.getByRole("button", { name: /^I know what I am doing/ }).className).not.toMatch(/border-primary\/40/);
    cleanup();

    renderStep({ stepId: "mode", draft: { mode: "advanced" } });
    const other = await screen.findByRole("button", { name: /^Simple configuration/ });
    expect(other.className).not.toMatch(/border-primary\/40/);
    expect(await screen.findByText("Advanced skip")).toBeTruthy();
    fireEvent.click(other);
    await waitFor(() => expect(other.className).toMatch(/border-primary\/40/));
    expect(screen.getByText("Simple path")).toBeTruthy();
  });

  it("the Folders step shows the Beginner default naming note and a caption for each library it will create", async () => {
    renderStep({ stepId: "folders" });
    expect(await screen.findByText("Beginner default")).toBeTruthy();
    expect(screen.getByText("{Movie Title} ({Release Year})")).toBeTruthy();
    expect(screen.getByText("{Series Title} ({Series Year})")).toBeTruthy();
    expect(screen.getByText("Create Movies and TV library defaults.")).toBeTruthy();
    expect(screen.getByText("Create Movies library defaults.")).toBeTruthy();
    expect(screen.getByText("Create TV library defaults.")).toBeTruthy();
  });

  it("choosing a search-source preset fills the protocol and the example address, and the protocol list offers Torznab, Newznab and RSS", async () => {
    renderStep({ stepId: "services" });
    const protocol = await screen.findByLabelText("Indexer protocol") as HTMLSelectElement;
    expect(Array.from(protocol.options).map((option) => option.textContent)).toEqual(["Torznab", "Newznab (Usenet indexer)", "RSS feed"]);
    const group = screen.getByRole("radiogroup", { name: "Search source presets" });

    fireEvent.click(within(group).getByRole("radio", { name: /^Newznab/ }));
    expect((screen.getByLabelText("Indexer URL") as HTMLInputElement).value).toBe("https://indexer.example/api");
    expect(protocol.value).toBe("newznab");
    expect(within(group).getByRole("radio", { name: /^Newznab/ }).getAttribute("aria-checked")).toBe("true");

    fireEvent.click(within(group).getByRole("radio", { name: /^RSS feed/ }));
    expect((screen.getByLabelText("Indexer URL") as HTMLInputElement).value).toBe("https://example.com/feed.xml");
    expect(protocol.value).toBe("rss");
    expect(within(group).getByRole("radio", { name: /^Newznab/ }).getAttribute("aria-checked")).toBe("false");
  });

  it("choosing a download-client preset fills the protocol and the vendor's own port, and changing the protocol list resets the port", async () => {
    renderStep({ stepId: "services" });
    const group = await screen.findByRole("radiogroup", { name: "Download client presets" });

    fireEvent.click(within(group).getByRole("radio", { name: /^Transmission/ }));
    expect((screen.getByLabelText("Download client port") as HTMLInputElement).value).toBe("9091");
    expect((screen.getByLabelText("Download client protocol") as HTMLSelectElement).value).toBe("transmission");

    fireEvent.change(screen.getByLabelText("Download client protocol"), { target: { value: "nzbget" } });
    expect((screen.getByLabelText("Download client port") as HTMLInputElement).value).toBe("6789");
    fireEvent.change(screen.getByLabelText("Download client protocol"), { target: { value: "deluge" } });
    expect((screen.getByLabelText("Download client port") as HTMLInputElement).value).toBe("8112");
  });
});
