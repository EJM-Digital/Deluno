import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

const fetchJson = vi.hoisted(() => vi.fn());
const authedFetch = vi.hoisted(() => vi.fn());
vi.mock("../lib/api", async (importOriginal) => ({ ...(await importOriginal<object>()), fetchJson }));
vi.mock("../lib/api/client", async (importOriginal) => ({ ...(await importOriginal<object>()), fetchJson }));
vi.mock("../lib/use-auth", async (importOriginal) => ({ ...(await importOriginal<object>()), authedFetch }));

const { SetupGuidePage, STEPS, AUTOMATION_QUESTIONS } = await import("./setup-guide-page");

type Over = {
  stepId: string; completed?: boolean; skipped?: boolean; draft?: Record<string, unknown>; guide?: Record<string, unknown>;
  metadataStatus?: unknown; clients?: unknown[]; indexers?: unknown[]; allowedHosts?: Record<string, unknown>; settings?: Record<string, unknown>;
};

const GUIDE = {
  id: "g", name: "g", version: 1, schemaVersion: 1, source: {}, integritySha256: "", qualityTiers: [], qualityProfiles: [],
  customFormats: Array.from({ length: 8 }, (_, index) => ({ trashId: `t${index}`, name: `Rule ${index}`, description: `Describes rule ${index}`, mediaTypes: [], patterns: ["x"], originalScore: 10 })),
  bundles: [{ id: "balanced-1080p", name: "Balanced", description: "Good groups and useful audio", mediaType: "all", includes: Array.from({ length: 8 }, (_, index) => ({ trashId: `t${index}`, score: 10 })) }]
};

function loaderData(over: Over) {
  return {
    settings: { movieRootPath: "D:/Media/Movies", seriesRootPath: "D:/Media/TV", timezone: "UTC", ...over.settings },
    libraries: [], qualityProfiles: [], customFormats: [], indexers: over.indexers ?? [], clients: over.clients ?? [], metadataStatus: over.metadataStatus ?? null,
    progress: { lastCompletedStep: STEPS.findIndex((step) => step.id === over.stepId), isSkipped: over.skipped ?? false, isCompleted: over.completed ?? false, updatedUtc: "2026-10-19T00:00:00Z" },
    draft: {
      mode: "simple", mediaIntent: "both", movieRootPath: "D:/Media/Movies", seriesRootPath: "D:/Media/TV", movieDownloadsPath: "", seriesDownloadsPath: "",
      qualityPreset: "balanced1080p", formatGoal: "balanced", indexerName: "", indexerProtocol: "torznab", indexerUrl: "",
      clientName: "", clientProtocol: "qbittorrent", clientHost: "", clientPort: "8080", backupEnabled: true,
      firstTitleType: "movies", firstTitle: "", firstTitleYear: "", firstTitleMonitored: true, ...over.draft
    },
    guide: { ...GUIDE, ...over.guide },
    allowedHosts: { enabled: false, allowedHosts: [], detectedHosts: [{ host: "192.168.1.10", reason: "This computer" }, { host: "nas.local", reason: "Your NAS" }], currentHost: "localhost", ...over.allowedHosts },
    contentBlocklist: { enabled: false, mode: "block", patterns: [] },
    localNetworkAuth: { enabled: false, additionalLocalNetworks: [], allowList: [], trustedProxies: [], currentAddress: "192.168.1.20", currentAddressIsLocal: true, currentAddressTrusted: true, allowedHostsEnabled: false },
    updatePreferences: { mode: "notify-only", channel: "stable", autoCheck: true }
  };
}

const selected = /(^| )border-primary\/40/;
const okJson = (body: unknown = {}, status = 200) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

function renderStep(over: Over, search = "?return=/movies") {
  fetchJson.mockImplementation(async (url: string) => (url === "/api/download-clients" ? over.clients ?? [] : []));
  authedFetch.mockImplementation(async () => okJson());
  const router = createMemoryRouter([
    { path: "/", element: <SetupGuidePage />, loader: () => loaderData(over) },
    { path: "/movies", element: <p>Movies page</p> },
    { path: "*", element: <p>Elsewhere</p> }
  ], { initialEntries: [`/${search}`] });
  return { router, ...render(<RouterProvider router={router} />) };
}

const progressWrites = () => authedFetch.mock.calls.filter(([url, init]) => url === "/api/setup/progress" && init?.method === "PUT").map(([, init]) => JSON.parse(init.body));
const button = (name: string | RegExp) => screen.findByRole("button", { name }) as Promise<HTMLButtonElement>;
const radio = (question: string, answer: "Yes" | "No") => screen.getByRole("radio", { name: `${question} ${answer}` }) as HTMLButtonElement;

afterEach(() => { cleanup(); fetchJson.mockReset(); authedFetch.mockReset(); });

describe("the guide shell and the step rail", () => {
  it("on the first step the left button reads Skip to advanced, saves that the guide was skipped and leaves to the return address", async () => {
    const { router } = renderStep({ stepId: "account" });
    fireEvent.click(await button("Skip to advanced"));
    await waitFor(() => expect(router.state.location.pathname).toBe("/movies"));
    expect(progressWrites()).toEqual([{ lastCompletedStep: 0, isSkipped: true, isCompleted: false }]);
  });

  it("Continue and Back save the step reached and keep the existing skipped and completed flags", async () => {
    renderStep({ stepId: "folders", skipped: true, completed: true });
    fireEvent.click(await button(/^Continue$/));
    await waitFor(() => expect(progressWrites()).toHaveLength(1));
    expect(progressWrites()[0]).toEqual({ lastCompletedStep: STEPS.findIndex((step) => step.id === "folders") + 1, isSkipped: true, isCompleted: true });
    fireEvent.click(await button(/^Back$/));
    await waitFor(() => expect(progressWrites()).toHaveLength(2));
    expect(progressWrites()[1]).toEqual({ lastCompletedStep: STEPS.findIndex((step) => step.id === "folders"), isSkipped: true, isCompleted: true });
  });

  it("pressing a step in the rail opens that step and saves progress", async () => {
    renderStep({ stepId: "account" });
    const rail = await screen.findByRole("button", { name: /Folders\s*Tell Deluno where your media lives/ });
    fireEvent.click(rail);
    expect(await screen.findByRole("heading", { name: "Folders" })).toBeTruthy();
    expect(progressWrites().at(-1)).toMatchObject({ lastCompletedStep: STEPS.findIndex((step) => step.id === "folders"), isSkipped: false });
  });

  it("the I know what I am doing card sets advanced mode, saves skipped and leaves the guide to the return address", async () => {
    const { router } = renderStep({ stepId: "mode" });
    fireEvent.click(await screen.findByText("I know what I am doing"));
    await waitFor(() => expect(router.state.location.pathname).toBe("/movies"));
    expect(progressWrites()[0]).toMatchObject({ isSkipped: true });
  });

  it("Continue on the Connect-apps step waits for the Weir answer of each library type once Weir is connected, and for the folder on a Yes", async () => {
    fetchJson.mockImplementation(async (url: string) => (url === "/api/system/bundled-components"
      ? [{ kind: "weir", displayName: "Weir", installed: true, connected: true }]
      : []));
    authedFetch.mockImplementation(async () => okJson());
    const router = createMemoryRouter([{ path: "/", element: <SetupGuidePage />, loader: () => loaderData({ stepId: "connectApps" }) }], { initialEntries: ["/"] });
    render(<RouterProvider router={router} />);
    const next = await button(/^Continue$/);
    await waitFor(() => expect(next.disabled).toBe(true));
    const movies = "Movies: Clean files with Weir before they go into your library?";
    const tv = "TV: Clean files with Weir before they go into your library?";
    fireEvent.click(await screen.findByRole("radio", { name: `${movies} No` }));
    expect(next.disabled).toBe(true);
    fireEvent.click(screen.getByRole("radio", { name: `${tv} No` }));
    await waitFor(() => expect(next.disabled).toBe(false));
  });
});

describe("the draft is saved 500 ms after any change", () => {
  it("sends the non-secret fields and the answers given, never the search source key or the client password", async () => {
    renderStep({ stepId: "signIn", draft: { indexerUrl: "https://indexer.example/api" }, indexers: [{ id: "i", name: "Mine", protocol: "torznab", baseUrl: "https://indexer.example/api", apiKey: "SECRET-KEY", isEnabled: true, healthStatus: "healthy" }] });
    await button(/^Continue$/);
    fireEvent.click(radio("Skip sign-in for devices on your home network?", "No"));
    await waitFor(() => {
      const writes = authedFetch.mock.calls.filter(([url, init]) => url === "/api/setup/draft" && init?.method === "PUT");
      expect(writes.length).toBeGreaterThan(0);
      expect(JSON.parse(writes.at(-1)![1].body).skipSignInLocalNetwork).toBe(false);
    }, { timeout: 3000 });
    const body = authedFetch.mock.calls.filter(([url, init]) => url === "/api/setup/draft" && init?.method === "PUT").at(-1)![1].body as string;
    expect(body).not.toContain("SECRET-KEY");
    const draft = JSON.parse(body);
    expect(draft).not.toHaveProperty("indexerApiKey");
    expect(draft).not.toHaveProperty("clientPassword");
    expect(draft).toMatchObject({ mode: "simple", mediaIntent: "both", formatGoal: "balanced", qualityPreset: "balanced1080p" });
  });

  it("an unanswered question stays out of the draft; an answered one is in it", async () => {
    renderStep({ stepId: "automation" });
    await button(/^Continue$/);
    fireEvent.click(radio(AUTOMATION_QUESTIONS[0].question, "Yes"));
    await waitFor(() => {
      const writes = authedFetch.mock.calls.filter(([url, init]) => url === "/api/setup/draft" && init?.method === "PUT");
      expect(JSON.parse(writes.at(-1)![1].body).automation[AUTOMATION_QUESTIONS[0].key]).toBe(true);
    }, { timeout: 3000 });
    const last = JSON.parse(authedFetch.mock.calls.filter(([url, init]) => url === "/api/setup/draft" && init?.method === "PUT").at(-1)![1].body);
    expect(Object.keys(last.automation)).toEqual([AUTOMATION_QUESTIONS[0].key]);
    expect(last.skipSignInLocalNetwork).toBeNull();
    expect(last.updateAutomatically).toBeNull();
  });

  it("answers saved in the draft are restored after a reload", async () => {
    renderStep({ stepId: "automation", draft: { automation: { [AUTOMATION_QUESTIONS[0].key]: true, [AUTOMATION_QUESTIONS[1].key]: false } } });
    await button(/^Continue$/);
    expect(radio(AUTOMATION_QUESTIONS[0].question, "Yes").getAttribute("aria-checked")).toBe("true");
    expect(radio(AUTOMATION_QUESTIONS[1].question, "No").getAttribute("aria-checked")).toBe("true");
    expect(radio(AUTOMATION_QUESTIONS[2].question, "Yes").getAttribute("aria-checked")).toBe("false");
    expect(radio(AUTOMATION_QUESTIONS[2].question, "No").getAttribute("aria-checked")).toBe("false");
  });

  it("on a re-run each Automation question shows its Current setting, read from the live settings rather than the deleted draft", async () => {
    renderStep({ stepId: "automation", completed: true });
    await button(/^Continue$/);
    expect(screen.getAllByText(/^Current setting: /).length).toBe(AUTOMATION_QUESTIONS.length);
  });
});

describe("Folders and Library Profiles", () => {
  it.each([
    ["Movies", ["Movies root", "Movies downloads folder (optional)"], ["TV root", "TV downloads folder (optional)"]],
    ["TV Shows", ["TV root", "TV downloads folder (optional)"], ["Movies root", "Movies downloads folder (optional)"]],
    ["both", ["Movies root", "TV root"], []]
  ])("the %s card shows the matching root inputs", async (card, shown, hidden) => {
    renderStep({ stepId: "folders" });
    fireEvent.click(await screen.findByRole("button", { name: new RegExp(`^${card} Create`, "i") }));
    for (const label of shown) expect(screen.getByText(label), label).toBeTruthy();
    for (const label of hidden) expect(screen.queryByText(label), label).toBeNull();
  });

  it("keeps a typed TV root and the optional downloads folders", async () => {
    renderStep({ stepId: "folders" });
    const tvRoot = (await screen.findByPlaceholderText("D:\\Media\\TV or /media/tv")) as HTMLInputElement;
    fireEvent.change(tvRoot, { target: { value: "E:\\Shows" } });
    const tvDownloads = screen.getByPlaceholderText("D:\\Downloads\\Completed\\shows") as HTMLInputElement;
    fireEvent.change(tvDownloads, { target: { value: "E:\\Done\\TV" } });
    const movieDownloads = screen.getByPlaceholderText("D:\\Downloads\\Completed\\films") as HTMLInputElement;
    fireEvent.change(movieDownloads, { target: { value: "E:\\Done\\Films" } });
    expect(tvRoot.value).toBe("E:\\Shows");
    expect(tvDownloads.value).toBe("E:\\Done\\TV");
    expect(movieDownloads.value).toBe("E:\\Done\\Films");
    await waitFor(() => {
      const writes = authedFetch.mock.calls.filter(([url, init]) => url === "/api/setup/draft" && init?.method === "PUT");
      expect(JSON.parse(writes.at(-1)![1].body)).toMatchObject({ seriesRootPath: "E:\\Shows", seriesDownloadsPath: "E:\\Done\\TV", movieDownloadsPath: "E:\\Done\\Films" });
    }, { timeout: 3000 });
  });

  it("offers Balanced 1080p and Premium 4K with their target, and highlights the chosen one", async () => {
    renderStep({ stepId: "quality" });
    const balanced = (await screen.findByText("Balanced 1080p")).closest("button")!;
    const premium = screen.getByText("Premium 4K").closest("button")!;
    expect(balanced.textContent).toContain("Target: WEB 1080p.");
    expect(premium.textContent).toContain("Target: WEB 2160p.");
    expect(balanced.className).toMatch(selected);
    expect(premium.className).not.toMatch(selected);
    fireEvent.click(premium);
    expect(premium.className).toMatch(selected);
    expect(balanced.className).not.toMatch(selected);
  });

  it("offers six release preferences with their copy and best-for text, and selecting one highlights it", async () => {
    renderStep({ stepId: "quality", draft: { formatGoal: "" } });
    const labels = ["Just make it work", "Balanced library", "Home theatre", "Storage friendly", "Anime", "Archive quality"];
    for (const label of labels) expect(await screen.findByText(label), label).toBeTruthy();
    expect(screen.getByText(/Reliable 1080p releases, obvious junk blocked/)).toBeTruthy();
    expect(screen.getByText(/Everyday movie and TV libraries where quality matters/)).toBeTruthy();
    const home = screen.getByText("Home theatre").closest("button")!;
    expect(home.className).not.toMatch(selected);
    fireEvent.click(home);
    expect(home.className).toMatch(selected);
  });

  it("the preview prompts for a choice first, then describes it with up to six sample rules and an n rules badge", async () => {
    renderStep({ stepId: "quality", draft: { formatGoal: "" } });
    expect(await screen.findByText("Choose a release preference to preview the rules Deluno will create.")).toBeTruthy();
    expect(screen.getByText("0 rules")).toBeTruthy();
    fireEvent.click(screen.getByText("Balanced library").closest("button")!);
    expect(await screen.findByText("Balanced library: Good groups and useful audio")).toBeTruthy();
    expect(screen.getByText("8 rules")).toBeTruthy();
    expect(screen.getAllByText(/^Rule \d$/)).toHaveLength(6);
  });
});

describe("Find & Download", () => {
  const PLACE: Record<string, string> = {
    "Indexer URL": "https://indexer.example/api", "Indexer API key": "API key, if required", "Download client host": "localhost or docker host",
    "Download client port": "8080", "Download client username": "Username, if required"
  };
  const field = (label: string) => screen.getByPlaceholderText(PLACE[label]) as HTMLInputElement;
  const typeInto = (label: string, value: string) => fireEvent.change(field(label), { target: { value } });
  const indexerPasses = () => okJson({ healthStatus: "healthy", message: "Indexer answered." });

  it("a preset fills the URL, and typed URL, host, port, user and key keep their values with the password masked", async () => {
    renderStep({ stepId: "services" });
    await screen.findByPlaceholderText("https://indexer.example/api");
    typeInto("Indexer URL", "https://indexer.example/api");
    typeInto("Indexer API key", "abc123");
    typeInto("Download client host", "nas");
    typeInto("Download client port", "9091");
    typeInto("Download client username", "me");
    expect(field("Indexer URL").value).toBe("https://indexer.example/api");
    expect(field("Indexer API key").value).toBe("abc123");
    expect(field("Download client host").value).toBe("nas");
    expect(field("Download client port").value).toBe("9091");
    expect(field("Download client username").value).toBe("me");
    const password = screen.getByPlaceholderText("Password/API key") as HTMLInputElement;
    expect(password.type).toBe("password");
    fireEvent.change(password, { target: { value: "pw" } });
    expect(password.value).toBe("pw");
  });

  it("Test search source shows Testing..., then Passed with the server's message", async () => {
    renderStep({ stepId: "services" });
    await screen.findByPlaceholderText("https://indexer.example/api");
    let release!: (response: Response) => void;
    authedFetch.mockImplementation((url: string) => (url === "/api/indexers/test" ? new Promise<Response>((resolve) => { release = resolve; }) : Promise.resolve(okJson())));
    typeInto("Indexer URL", "https://indexer.example/api");
    fireEvent.click(await button("Test search source"));
    expect(await screen.findByText("Testing...")).toBeTruthy();
    await act(async () => { release(indexerPasses()); });
    expect(await screen.findByText("Passed")).toBeTruthy();
    expect(screen.getByText("Indexer answered.")).toBeTruthy();
  });

  it("Test search source shows Failed with the server's message, or says the source needs attention", async () => {
    renderStep({ stepId: "services" });
    await screen.findByPlaceholderText("https://indexer.example/api");
    typeInto("Indexer URL", "https://indexer.example/api");
    authedFetch.mockImplementation(async (url: string) => (url === "/api/indexers/test" ? okJson({ message: "Bad API key." }, 400) : okJson()));
    fireEvent.click(await button("Test search source"));
    expect(await screen.findByText("Failed")).toBeTruthy();
    expect(screen.getByText("Bad API key.")).toBeTruthy();

    authedFetch.mockImplementation(async (url: string) => (url === "/api/indexers/test" ? okJson({ healthStatus: "failed" }) : okJson()));
    fireEvent.click(await button("Test search source"));
    expect(await screen.findByText("Indexer test needs attention.")).toBeTruthy();
  });

  it("Test search source with no URL says to enter one and sends nothing", async () => {
    renderStep({ stepId: "services" });
    await screen.findByPlaceholderText("https://indexer.example/api");
    fireEvent.click(await button("Test search source"));
    expect(await screen.findByText("Enter an indexer URL before testing.")).toBeTruthy();
    expect(authedFetch.mock.calls.some(([url]) => url === "/api/indexers/test")).toBe(false);
  });

  it("changing any search source field resets its result to Not tested", async () => {
    renderStep({ stepId: "services" });
    await screen.findByPlaceholderText("https://indexer.example/api");
    typeInto("Indexer URL", "https://indexer.example/api");
    authedFetch.mockImplementation(async (url: string) => (url === "/api/indexers/test" ? indexerPasses() : okJson()));
    fireEvent.click(await button("Test search source"));
    await screen.findByText("Passed");
    typeInto("Indexer API key", "changed");
    expect(await screen.findAllByText("Not tested")).not.toHaveLength(0);
    expect(screen.queryByText("Passed")).toBeNull();
    expect(screen.queryByText("Indexer answered.")).toBeNull();
  });

  it("changing any download client field resets the client test to Not tested", async () => {
    renderStep({ stepId: "services" });
    await screen.findByPlaceholderText("https://indexer.example/api");
    typeInto("Download client host", "nas");
    authedFetch.mockImplementation(async (url: string) => (url === "/api/download-clients/test" ? okJson({ healthStatus: "healthy", message: "Client answered." }) : okJson()));
    fireEvent.click(await button("Test download client"));
    await screen.findByText("Client answered.");
    typeInto("Download client port", "9999");
    await waitFor(() => expect(screen.queryByText("Client answered.")).toBeNull());
    expect(screen.queryByText("Passed")).toBeNull();
  });

  it("clients that are already connected are listed as used as they are, and Add a different download client reveals the form with host, user and password cleared", async () => {
    const clients = [{ id: "c1", name: "qBittorrent", protocol: "qbittorrent", host: "localhost", port: 8081, username: "admin", isEnabled: true, healthStatus: "healthy" }];
    renderStep({ stepId: "services", clients });
    expect(await screen.findByText("Already connected. Deluno will use these.")).toBeTruthy();
    expect(screen.getByText("Connected")).toBeTruthy();
    expect(screen.queryByPlaceholderText("localhost or docker host")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Add a different download client" }));
    await screen.findByPlaceholderText("localhost or docker host");
    expect(field("Download client host").value).toBe("");
    expect(field("Download client username").value).toBe("");
    expect((screen.getByPlaceholderText("Password/API key") as HTMLInputElement).value).toBe("");
  });

  it("with no client connected the panel offers the Companion Tools install", async () => {
    renderStep({ stepId: "services" });
    await screen.findByPlaceholderText("https://indexer.example/api");
    const link = await screen.findByRole("link", { name: /Let Deluno install and connect SABnzbd, qBittorrent or Weir/ });
    expect(link.getAttribute("href")).toBe("/indexers/companion-tools");
  });

  it("the client list is read again when the step changes, so a client connected meanwhile is not missing", async () => {
    renderStep({ stepId: "connectApps" });
    await button(/^Continue$/);
    const before = fetchJson.mock.calls.filter(([url]) => url === "/api/download-clients").length;
    fireEvent.click(await button(/^Continue$/));
    await waitFor(() => expect(fetchJson.mock.calls.filter(([url]) => url === "/api/download-clients").length).toBeGreaterThan(before));
  });
});

describe("the sign-in question and the addresses question", () => {
  const SIGN_IN = "Skip sign-in for devices on your home network?";
  const ADDRESSES = "Only answer on the addresses you use?";
  const allowList = () => screen.queryByText("Narrow this to specific addresses (optional)");
  const add = (value: string) => {
    fireEvent.change(screen.getByRole("textbox", { name: /Add an address or subnet/ }), { target: { value } });
    fireEvent.click(screen.getByRole("button", { name: "Add address to the allow list" }));
  };

  it("Yes reveals the allow-list editor and No hides it", async () => {
    renderStep({ stepId: "signIn" });
    await button(/^Continue$/);
    expect(allowList()).toBeNull();
    fireEvent.click(radio(SIGN_IN, "Yes"));
    expect(allowList()).toBeTruthy();
    fireEvent.click(radio(SIGN_IN, "No"));
    expect(allowList()).toBeNull();
  });

  it.each([
    ["192.168.1.20", true], ["192.168.1.0/28", true], ["fe80::1/64", true], ["300.1.1.1", false], ["10.0.0.0/33", false], ["abc", false], ["fe80::1/129", false]
  ])("the entry %s is %s", async (entry, valid) => {
    renderStep({ stepId: "signIn" });
    await button(/^Continue$/);
    fireEvent.click(radio(SIGN_IN, "Yes"));
    add(entry);
    if (valid) {
      expect(screen.getByText(entry)).toBeTruthy();
      expect(screen.getByRole("button", { name: `Remove ${entry} from the allow list` })).toBeTruthy();
      expect((screen.getByRole("textbox", { name: /Add an address or subnet/ }) as HTMLInputElement).value).toBe("");
    } else {
      expect(screen.getByText(new RegExp(`"${entry.replace(/[./]/g, "\\$&")}" is not a valid address or subnet`))).toBeTruthy();
      expect(screen.queryByRole("button", { name: `Remove ${entry} from the allow list` })).toBeNull();
    }
  });

  it("an entry is added once whatever its case, with the box cleared, and the Enter key adds it too", async () => {
    renderStep({ stepId: "signIn" });
    await button(/^Continue$/);
    fireEvent.click(radio(SIGN_IN, "Yes"));
    add("fe80::AB");
    add("fe80::ab");
    expect(screen.getAllByText(/^fe80::/i)).toHaveLength(1);
    const box = screen.getByRole("textbox", { name: /Add an address or subnet/ });
    fireEvent.change(box, { target: { value: "10.1.2.3" } });
    fireEvent.keyDown(box, { key: "Enter" });
    expect(screen.getByText("10.1.2.3")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Remove 10.1.2.3 from the allow list" }));
    expect(screen.queryByText("10.1.2.3")).toBeNull();
  });

  it("Yes to the addresses question lists the detected addresses with a reason each, and No hides them", async () => {
    renderStep({ stepId: "addresses" });
    await button(/^Continue$/);
    expect(screen.queryByText("192.168.1.10")).toBeNull();
    fireEvent.click(radio(ADDRESSES, "Yes"));
    expect(screen.getByText("192.168.1.10")).toBeTruthy();
    expect(screen.getByText("This computer")).toBeTruthy();
    expect(screen.getByText("nas.local")).toBeTruthy();
    expect(screen.getByText("Your NAS")).toBeTruthy();
    fireEvent.click(radio(ADDRESSES, "No"));
    expect(screen.queryByText("192.168.1.10")).toBeNull();
  });

  it("ticking and unticking an address changes the selection whatever its case, and the warning shows until the address in use is ticked", async () => {
    renderStep({ stepId: "addresses", allowedHosts: { currentHost: "NAS.local", allowedHosts: ["192.168.1.10"] } });
    await button(/^Continue$/);
    fireEvent.click(radio(ADDRESSES, "Yes"));
    const warning = () => screen.queryByText(/finishing setup will leave this turned off rather than lock you out/);
    expect(warning()).toBeTruthy();
    expect(screen.getByText(/Tick NAS\.local/)).toBeTruthy();
    const box = screen.getByRole("checkbox", { name: /nas\.local/ });
    expect(box.getAttribute("aria-checked") ?? (box as HTMLInputElement).checked).not.toBe("true");
    fireEvent.click(box);
    expect(warning()).toBeNull();
    fireEvent.click(screen.getByRole("checkbox", { name: /nas\.local/ }));
    expect(warning()).toBeTruthy();
  });
});

describe("Start: the first title, the banners and the finish", () => {
  const READY_DRAFT = () => ({
    skipSignInLocalNetwork: false, updateAutomatically: false,
    automation: { restrictAllowedHosts: false, ...Object.fromEntries(AUTOMATION_QUESTIONS.map((item) => [item.key, false])) }
  });
  const result = (over: Partial<{ title: string; year: number; provider: string; providerId: string; rating: number; posterUrl: string | null; libraryEntryId: string | null }> = {}) => ({
    title: "Dune", year: 2021, provider: "tmdb", providerId: "1", rating: 8.04, posterUrl: null, libraryEntryId: null, originalTitle: "Dune", overview: "", backdropUrl: null, genres: [], externalUrl: null, imdbId: "tt1", ...over
  });
  const searchBox = () => screen.getByPlaceholderText("Dune: Part Two") as HTMLInputElement;
  const ready = () => screen.findByPlaceholderText("Dune: Part Two");

  it("the Movie and TV toggle switches the title box, and the year takes digits only, four at most", async () => {
    renderStep({ stepId: "finish" });
    expect(await ready()).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "TV" }));
    expect(screen.getByPlaceholderText("Severance")).toBeTruthy();
    const year = screen.getByPlaceholderText("Year") as HTMLInputElement;
    fireEvent.change(year, { target: { value: "20a2b66" } });
    expect(year.value).toBe("2026");
  });

  it("Search metadata is disabled with the amber note when title matching is unavailable", async () => {
    renderStep({ stepId: "finish", metadataStatus: { isConfigured: false } });
    expect(await screen.findByText(/Title matching is not available on this Deluno installation yet\./)).toBeTruthy();
    expect(((await button("Search metadata")) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByText("Manual title entry")).toBeTruthy();
  });

  it("an empty title is refused before any search", async () => {
    renderStep({ stepId: "finish", metadataStatus: { isConfigured: true } });
    fireEvent.click(await button("Search metadata"));
    expect(await screen.findByText("Type a movie or TV title before searching metadata.")).toBeTruthy();
    expect(fetchJson.mock.calls.some(([url]) => String(url).startsWith("/api/metadata/search"))).toBe(false);
  });

  it("a failed search shows the error, and a search with no matches says the title can still be added by hand", async () => {
    renderStep({ stepId: "finish", metadataStatus: { isConfigured: true } });
    await ready();
    fireEvent.change(searchBox(), { target: { value: "Dune" } });
    fetchJson.mockImplementation(async (url: string) => { if (String(url).startsWith("/api/metadata/search")) throw new Error("Title matching is temporarily unavailable. Try again."); return []; });
    fireEvent.click(await button("Search metadata"));
    expect(await screen.findByText("Title matching is temporarily unavailable. Try again.")).toBeTruthy();
    fetchJson.mockImplementation(async () => []);
    fireEvent.click(await button("Search metadata"));
    expect(await screen.findByText("No metadata matches were found. You can still add the title manually.")).toBeTruthy();
  });

  it("shows up to six results with art or No art, year, provider, rating and Already in your library, and picking one fills the title and year", async () => {
    renderStep({ stepId: "finish", metadataStatus: { isConfigured: true } });
    await ready();
    const found = Array.from({ length: 8 }, (_, index) => result({ title: `Dune ${index}`, providerId: String(index), year: index === 0 ? 2021 : 2000 + index, libraryEntryId: index === 1 ? "lib-1" : null }));
    fetchJson.mockImplementation(async (url: string) => (String(url).startsWith("/api/metadata/search") ? found : []));
    fireEvent.change(searchBox(), { target: { value: "Dune" } });
    fireEvent.click(await button("Search metadata"));
    expect(await screen.findByText("Dune 0")).toBeTruthy();
    expect(screen.queryByText("Dune 6")).toBeNull();
    expect(screen.getAllByText("No art")).toHaveLength(6);
    expect(screen.getAllByText("Already in your library")).toHaveLength(1);
    expect(screen.getAllByText("8.0 rating").length).toBeGreaterThan(0);
    fireEvent.click(screen.getByText("Dune 3").closest("button")!);
    await waitFor(() => expect(searchBox().value).toBe("Dune 3"));
    expect((screen.getByPlaceholderText("Year") as HTMLInputElement).value).toBe("2003");
    expect(screen.getByText("TMDB #3")).toBeTruthy();
  });

  it("when Create baseline is refused, the error shows red, what was made is taken back in reverse order and the amber note says none of the answers were applied", async () => {
    renderStep({ stepId: "finish", draft: READY_DRAFT() });
    let libraries = 0;
    fetchJson.mockImplementation(async (url: string, init?: { method?: string; body?: string }) => {
      if (url === "/api/quality-profiles") return { id: "qp1", name: "Profile" };
      if (url === "/api/custom-formats") return { id: "cf1", name: "Rule" };
      if (url === "/api/libraries" && init?.method === "POST") {
        if (++libraries === 2) throw new Error("The TV folder is not writable.");
        return { id: "lib1", name: "Movies" };
      }
      return [];
    });
    fireEvent.click(await button("Create baseline"));
    expect(await screen.findByText("The TV folder is not writable.")).toBeTruthy();
    const amber = await screen.findByText(/None of your answers were applied/);
    expect(amber.textContent).toMatch(/^Setup failed, so Deluno cleaned up \d+ partially created items?\./);
    const deletes = authedFetch.mock.calls.filter(([, init]) => init?.method === "DELETE").map(([url]) => String(url));
    const libraryDelete = deletes.findIndex((url) => url === "/api/libraries/lib1");
    const profileDelete = deletes.findIndex((url) => url === "/api/quality-profiles/qp1");
    const formatDelete = deletes.findIndex((url) => url === "/api/custom-formats/cf1");
    expect(libraryDelete).toBeGreaterThanOrEqual(0);
    expect(libraryDelete).toBeLessThan(profileDelete);
    expect(profileDelete).toBeLessThan(formatDelete);
    expect(fetchJson.mock.calls.some(([url]) => url === "/api/settings")).toBe(false);
  });

  it("while the baseline is being made the Back button is disabled, and afterwards the Setup complete screen shows the summary and four tiles", async () => {
    renderStep({ stepId: "finish", draft: READY_DRAFT() });
    let release!: () => void;
    const gate = new Promise<void>((resolve) => { release = resolve; });
    fetchJson.mockImplementation(async (url: string, init?: { method?: string; body?: string }) => {
      if (url === "/api/quality-profiles") { await gate; return { id: "qp1", name: "Balanced 1080p" }; }
      if (url === "/api/custom-formats") return { id: "cf1", name: "Rule" };
      if (url === "/api/libraries" && init?.method === "POST") return { id: "lib", name: JSON.parse(init.body!).name };
      return [];
    });
    fireEvent.click(await button("Create baseline"));
    await waitFor(async () => expect(((await button(/^Back$/)) as HTMLButtonElement).disabled).toBe(true));
    await act(async () => { release(); });
    expect(await screen.findByText("Setup complete")).toBeTruthy();
    const complete = screen.getByText("Setup complete").closest("div")!.parentElement!;
    expect(within(complete).getByText(/baseline created\./)).toBeTruthy();
    expect(within(complete).getByText("Movies + TV Shows")).toBeTruthy();
    for (const tile of ["Dashboard", "Add titles", "Review routing", "Media Management"]) expect(within(complete).getByText(tile)).toBeTruthy();
    expect(await screen.findByRole("link", { name: /Continue to Deluno/ })).toBeTruthy();
  });
});
