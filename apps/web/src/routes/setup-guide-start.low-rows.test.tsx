import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

const fetchJson = vi.hoisted(() => vi.fn());
const authedFetch = vi.hoisted(() => vi.fn());
vi.mock("../lib/api", async (importOriginal) => ({ ...(await importOriginal<object>()), fetchJson }));
vi.mock("../lib/api/client", async (importOriginal) => ({ ...(await importOriginal<object>()), fetchJson }));
vi.mock("../lib/use-auth", async (importOriginal) => ({ ...(await importOriginal<object>()), authedFetch }));

const { SetupGuidePage, STEPS, AUTOMATION_QUESTIONS } = await import("./setup-guide-page");

type Over = { draft?: Record<string, unknown>; settings?: Record<string, unknown>; clients?: unknown[]; hangWrites?: boolean };

const GUIDE = { id: "g", name: "g", version: 1, schemaVersion: 1, source: {}, integritySha256: "", qualityTiers: [], qualityProfiles: [], customFormats: [], bundles: [] };
const everyAnswer = Object.fromEntries(AUTOMATION_QUESTIONS.map((question) => [question.key, false]));
const ANSWERED = {
  qualityPreset: "balanced1080p", formatGoal: "balanced", skipSignInLocalNetwork: false, checkForUpdates: false,
  automation: { ...everyAnswer, restrictAllowedHosts: false }
};

function loaderData(over: Over) {
  return {
    settings: { movieRootPath: "D:/Media/Movies", seriesRootPath: "D:/Media/TV", timezone: "UTC", ...over.settings },
    libraries: [], qualityProfiles: [], customFormats: [], indexers: [], clients: over.clients ?? [], metadataStatus: null,
    progress: { lastCompletedStep: STEPS.findIndex((step) => step.id === "finish"), isSkipped: false, isCompleted: false, updatedUtc: "2026-10-19T00:00:00Z" },
    draft: {
      mode: "simple", mediaIntent: "both", movieRootPath: "D:/Media/Movies", seriesRootPath: "D:/Media/TV", movieDownloadsPath: "", seriesDownloadsPath: "",
      qualityPreset: "", formatGoal: "", indexerName: "", indexerProtocol: "torznab", indexerUrl: "",
      clientName: "", clientProtocol: "qbittorrent", clientHost: "", clientPort: "8080", backupEnabled: true,
      firstTitleType: "movies", firstTitle: "", firstTitleYear: "", firstTitleMonitored: true, ...over.draft
    },
    guide: GUIDE,
    allowedHosts: { enabled: false, allowedHosts: [], detectedHosts: [], currentHost: "localhost" },
    contentBlocklist: { enabled: false, mode: "block", patterns: [] },
    localNetworkAuth: { enabled: false, additionalLocalNetworks: [], allowList: [], trustedProxies: [], currentAddress: "192.168.1.20", currentAddressIsLocal: true, currentAddressTrusted: true, allowedHostsEnabled: false },
    updatePreferences: { mode: "notify-only", channel: "stable", autoCheck: true }
  };
}

function renderFinish(over: Over = {}) {
  fetchJson.mockImplementation(async (url: string, init?: RequestInit) => {
    if (over.hangWrites && init?.method === "POST") return new Promise(() => undefined);
    return url === "/api/download-clients" ? over.clients ?? [] : [];
  });
  authedFetch.mockImplementation(async () => new Response("{}", { status: 200, headers: { "Content-Type": "application/json" } }));
  const router = createMemoryRouter([{ path: "/", element: <SetupGuidePage />, loader: () => loaderData(over) }, { path: "*", element: <p>Elsewhere</p> }], { initialEntries: ["/"] });
  return render(<RouterProvider router={router} />);
}

const createButton = () => screen.findByRole("button", { name: /Create baseline/ }) as Promise<HTMLButtonElement>;
const cell = (label: string) => within(document.querySelector("form")!).getByText(label).closest("div")!;

afterEach(() => { cleanup(); fetchJson.mockReset(); authedFetch.mockReset(); });

describe("the Start step, low rows (#1329)", () => {
  it("the summary strip names the libraries and says Not chosen or Later for what is missing, then the chosen names", async () => {
    renderFinish();
    await createButton();
    expect(cell("Libraries").textContent).toContain("Movies + TV");
    expect(cell("Quality").textContent).toContain("Not chosen");
    expect(cell("Release preferences").textContent).toContain("Not chosen");
    expect(cell("Search source").textContent).toContain("Later");
    expect(cell("Download client").textContent).toContain("Later");
    cleanup();

    renderFinish({
      draft: { ...ANSWERED, mediaIntent: "tv", indexerName: "My indexer", indexerUrl: "https://indexer.example/api" },
      clients: [{ id: "c1", name: "My qBittorrent", protocol: "qbittorrent", host: "localhost", port: 8080, isEnabled: true, healthStatus: "healthy" }]
    });
    await createButton();
    expect(cell("Libraries").textContent).toContain("TV");
    expect(cell("Libraries").textContent).not.toContain("Movies");
    expect(cell("Quality").textContent).not.toContain("Not chosen");
    expect(cell("Release preferences").textContent).not.toContain("Not chosen");
    expect(cell("Search source").textContent).toContain("My indexer");
    expect(cell("Download client").textContent).toContain("My qBittorrent");
  });

  it("the Import decision preview shows the Search, Download, Destination and Transfer tiles and says whether a client route is ready", async () => {
    renderFinish();
    await createButton();
    const preview = screen.getByText("Import decision preview").closest("div")!.parentElement!.parentElement!;
    expect(within(preview).getByText("Search source required")).toBeTruthy();
    expect(within(preview).getByText("Download client required")).toBeTruthy();
    expect(within(preview).getByText("Movies -> D:/Media/Movies / TV -> D:/Media/TV")).toBeTruthy();
    expect(within(preview).getByText("Single-copy link first + backups")).toBeTruthy();
    for (const label of ["Search", "Download", "Destination", "Transfer"]) expect(within(preview).getByText(label), label).toBeTruthy();
    expect(within(preview).getByText("Client can be added later")).toBeTruthy();
    cleanup();

    renderFinish({ clients: [{ id: "c1", name: "My qBittorrent", protocol: "qbittorrent", host: "localhost", port: 8080, isEnabled: true, healthStatus: "healthy" }], draft: { backupEnabled: false } });
    await createButton();
    const ready = screen.getByText("Import decision preview").closest("div")!.parentElement!.parentElement!;
    expect(within(ready).getByText("Client route ready")).toBeTruthy();
    expect(within(ready).getByText("Single-copy link first")).toBeTruthy();
  });

  it("leaving out the root folder and the quality keeps the person on Start and says each one there, beside what it is about (#1422)", async () => {
    renderFinish({ settings: { movieRootPath: "", seriesRootPath: "" }, draft: { ...ANSWERED, qualityPreset: "", movieRootPath: "", seriesRootPath: "" } });
    const create = await createButton();

    // Enter in a field of the Start step submits the form: it must not carry the person to another step.
    fireEvent.submit(create.closest("form")!);

    await waitFor(() => expect(screen.getByText(/Still needed before Deluno can create the baseline:.*a library root folder.*a picture quality/)).toBeTruthy());
    expect(await createButton()).toBeTruthy();
    expect(cell("Libraries").textContent).toContain("No root folder yet");
    expect(cell("Quality").textContent).toContain("Not chosen");
    expect(create.disabled).toBe(true);
    expect(fetchJson.mock.calls.some(([, init]) => (init as RequestInit | undefined)?.method === "POST")).toBe(false);
  });

  it("Create baseline is disabled until everything is chosen, including Updates, and its tooltip says what is missing", async () => {
    renderFinish();
    expect((await createButton()).title).toBe("Choose a picture quality and release preference first");
    expect((await createButton()).disabled).toBe(true);
    cleanup();

    renderFinish({ draft: { qualityPreset: "balanced1080p", formatGoal: "balanced" } });
    const noAnswers = await createButton();
    expect(noAnswers.disabled).toBe(true);
    expect(noAnswers.title).toBe("Answer every automation question first");
    cleanup();

    renderFinish({ draft: { qualityPreset: "balanced1080p", formatGoal: "balanced", movieRootPath: "", seriesRootPath: "" }, settings: { movieRootPath: "", seriesRootPath: "" } });
    expect((await createButton()).title).toBe("Choose a Movies or TV root folder first");
    cleanup();

    renderFinish({ draft: { ...ANSWERED, checkForUpdates: null } });
    const noUpdates = await createButton();
    expect(noUpdates.disabled).toBe(true);
    expect(screen.getByText(/Still needed before Deluno can create the baseline:.*Check for updates by itself/)).toBeTruthy();
    cleanup();

    renderFinish({ draft: ANSWERED });
    const ready = await createButton();
    expect(ready.disabled).toBe(false);
    expect(ready.title).toBe("Create the libraries, profiles and rules you chose");
  });

  it("with everything chosen but no tested search source or client, a grey note says the baseline can be created now", async () => {
    renderFinish({ draft: ANSWERED });
    await createButton();
    expect(screen.getByText(/You can create the baseline now\. Deluno will not be operationally ready until a search source and a download client are connected and tested/)).toBeTruthy();
    expect(screen.queryByText(/Still needed before Deluno can create the baseline/)).toBeNull();
  });

  it("while the baseline is being made the button shows a spinner and cannot be pressed again, and Back is disabled", async () => {
    renderFinish({ draft: ANSWERED, hangWrites: true });
    const button = await createButton();
    expect(button.querySelector("svg")!.getAttribute("class")).not.toMatch(/animate-spin/);

    fireEvent.click(button);

    await waitFor(() => expect(button.disabled).toBe(true));
    expect(button.querySelector("svg")!.getAttribute("class")).toMatch(/animate-spin/);
    expect((screen.getByRole("button", { name: /Back/ }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("the hand-off tiles lead to routing, quality and automation, and the note says nothing there is permanent", async () => {
    renderFinish();
    await createButton();

    expect(screen.getByRole("link", { name: /Review routing/ }).getAttribute("href")).toBe("/indexers");
    expect(screen.getByRole("link", { name: /Tune quality/ }).getAttribute("href")).toBe("/settings/profiles");
    expect(screen.getByRole("link", { name: /Watch automation/ }).getAttribute("href")).toBe("/system");
    expect(screen.getByText(/^Nothing here is permanent\./)).toBeTruthy();
  });
});
