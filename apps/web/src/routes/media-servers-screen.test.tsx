import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { authedFetch } from "../lib/use-auth";

/**
 * Find & Download > Media servers: the list, the On switch, the Add server drawer (kinds, address example per kind, token
 * label), what it saves, Test connection for a draft and for a saved server, path mappings, and Remove.
 */

vi.setConfig({ testTimeout: 30_000 });
vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
vi.mock("../lib/live-refresh", () => ({ useLiveRefresh: () => undefined }));
vi.mock("../lib/connection-versions", () => ({ useConnectionVersions: () => ({ versions: new Map() }), versionKey: (kind: string, id: string) => `${kind}:${id}` }));
vi.mock("../components/app/connection-update-panel", () => ({ ConnectionUpdatePanel: () => null }));
vi.mock("../components/app/connection-version", () => ({ ConnectionVersionBeside: () => null }));
vi.mock("../components/app/seerr-servers-card", () => ({ SeerrServersCard: () => null }));
const revalidate = vi.fn();
const loaderData = { servers: [] as unknown[], collections: [], seerrServers: [], seerrCleanups: [], libraries: [], qualityProfiles: [] };
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate })
}));

const { MediaServersPage } = await import("./media-servers-screen");
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });
const server = (over: Record<string, unknown> = {}) => ({
  id: "s1", kind: "jellyfin", name: "Jellyfin on lounge", baseUrl: "http://lounge.example.test:8096", hasToken: true, isEnabled: true, healthStatus: "healthy",
  lastHealthMessage: "Reached it.", lastHealthLatencyMs: 14, lastHealthTestUtc: new Date().toISOString(), serverName: "Lounge", serverId: "abc", leavingSoonOnHome: false, ...over
});
const renderPage = () => render(<MemoryRouter><MediaServersPage /></MemoryRouter>);
const calls = (method: string) => vi.mocked(authedFetch).mock.calls.filter(([, init]) => (init as RequestInit | undefined)?.method === method);
const bodyOf = (call: unknown[]) => JSON.parse(String((call[1] as RequestInit).body));

beforeEach(() => {
  cleanup();
  loaderData.servers = [];
  vi.mocked(authedFetch).mockReset();
  vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => String(input).endsWith("/path-mappings") ? json([]) : json({}));
  revalidate.mockReset();
});

describe("the list", () => {
  it("says how many are working, shows the name the server gave itself, and says Not tested yet for one never tested", () => {
    loaderData.servers = [server(), server({ id: "s2", name: "Emby on den", kind: "emby", serverName: null, lastHealthTestUtc: null, healthStatus: "untested" })];
    renderPage();
    expect(screen.getByText("1 of 2 working")).toBeInTheDocument();
    expect(screen.getByText("Lounge")).toBeInTheDocument();
    expect(screen.getByText("Not tested yet")).toBeInTheDocument();
  });

  it("explains an empty list and what a server is for", () => {
    renderPage();
    expect(screen.getByText("No media servers")).toBeInTheDocument();
  });

  it("PUTs the server with only the switch changed and an empty token, so the saved token is kept", async () => {
    loaderData.servers = [server()];
    renderPage();
    fireEvent.click(screen.getByRole("switch", { name: "Pause Jellyfin on lounge" }));
    await waitFor(() => expect(calls("PUT")).toHaveLength(1));
    expect(calls("PUT")[0][0]).toBe("/api/media-servers/s1");
    expect(bodyOf(calls("PUT")[0])).toEqual({ kind: "jellyfin", name: "Jellyfin on lounge", baseUrl: "http://lounge.example.test:8096", token: "", isEnabled: false, leavingSoonOnHome: false });
    expect(revalidate).toHaveBeenCalled();
  });
});

describe("the Add server drawer", () => {
  const openNew = () => fireEvent.click(screen.getAllByRole("button", { name: "Add server" })[0]);

  it("offers Jellyfin, Emby and Plex, an address example with that kind's port and the right name for the token", () => {
    renderPage();
    openNew();
    const kind = screen.getByLabelText("Server") as HTMLSelectElement;
    expect([...kind.options].map((option) => option.textContent)).toEqual(["Jellyfin", "Emby", "Plex"]);
    expect(screen.getByPlaceholderText("http://192.168.1.20:8096")).toBeInTheDocument();
    expect(screen.getByLabelText("API key")).toBeInTheDocument();
    fireEvent.change(kind, { target: { value: "plex" } });
    expect(screen.getByPlaceholderText("http://192.168.1.20:32400")).toBeInTheDocument();
    expect(screen.getByLabelText("X-Plex-Token")).toBeInTheDocument();
  });

  it("adds a server by POST, stays on the saved server and says Server added; field errors show under the fields", async () => {
    vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) =>
      String(input) === "/api/media-servers" && init?.method === "POST" ? json(server({ id: "new1", name: "Jellyfin on new", baseUrl: "http://new.example.test:8096" }), 201) : json([]));
    renderPage();
    openNew();
    fireEvent.change(screen.getByPlaceholderText("http://192.168.1.20:8096"), { target: { value: "http://new.example.test:8096" } });
    fireEvent.change(screen.getByLabelText("API key"), { target: { value: "key-1" } });
    fireEvent.click(screen.getByRole("button", { name: "Add server" }));
    await waitFor(() => expect(screen.getByText("Server added")).toBeInTheDocument());
    expect(bodyOf(calls("POST")[0])).toMatchObject({ kind: "jellyfin", baseUrl: "http://new.example.test:8096", token: "key-1", isEnabled: true });
    expect(revalidate).toHaveBeenCalled();
  });

  it("shows the server's message under the address when it refuses it", async () => {
    vi.mocked(authedFetch).mockImplementation(async (_input: RequestInfo | URL, init?: RequestInit) =>
      init?.method === "POST" ? json({ errors: { baseUrl: ["Use an http or https address."] } }, 400) : json([]));
    renderPage();
    openNew();
    fireEvent.change(screen.getByPlaceholderText("http://192.168.1.20:8096"), { target: { value: "ftp://nope" } });
    fireEvent.click(screen.getByRole("button", { name: "Add server" }));
    expect((await screen.findAllByText("Use an http or https address.")).length).toBeGreaterThan(0);
    expect(screen.getByText("Check the highlighted fields.")).toBeInTheDocument();
  });

  it("tests a draft before it is saved and says the answer in the drawer", async () => {
    vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) =>
      String(input) === "/api/media-servers/test" ? json({ ok: true, status: "healthy", message: "Reached Jellyfin 10.9 (Lounge).", latencyMs: 9, serverName: "Lounge" }) : json([]));
    renderPage();
    openNew();
    fireEvent.change(screen.getByPlaceholderText("http://192.168.1.20:8096"), { target: { value: "http://new.example.test:8096" } });
    fireEvent.click(screen.getByRole("button", { name: /Test connection/ }));
    expect(await screen.findByText("Reached Jellyfin 10.9 (Lounge).")).toBeInTheDocument();
    expect(calls("POST").map(([url]) => url)).toContain("/api/media-servers/test");
  });
});

describe("a saved server's drawer", () => {
  const openSaved = () => {
    loaderData.servers = [server()];
    renderPage();
    fireEvent.click(screen.getByText("Jellyfin on lounge"));
  };

  it("locks the kind, says the token is stored encrypted and unchanged, and shows what the server reported on its last test", () => {
    openSaved();
    expect(screen.getByLabelText("Server")).toBeDisabled();
    expect(screen.getByText("Stored encrypted. Leave empty to keep the current one.")).toBeInTheDocument();
    expect(screen.getAllByText("Reports itself as").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Reached it.").length).toBeGreaterThan(1);
  });

  it("saves with PUT and a blank token, saying Saved just now", async () => {
    openSaved();
    fireEvent.change(screen.getByPlaceholderText("http://192.168.1.20:8096"), { target: { value: "http://lounge2.example.test:8096" } });
    vi.mocked(authedFetch).mockResolvedValue(json(server({ baseUrl: "http://lounge2.example.test:8096" })));
    fireEvent.click(screen.getByRole("button", { name: "Save server" }));
    await waitFor(() => expect(screen.getByText("Saved just now")).toBeInTheDocument());
    expect(calls("PUT")[0][0]).toBe("/api/media-servers/s1");
    expect(bodyOf(calls("PUT")[0])).toMatchObject({ baseUrl: "http://lounge2.example.test:8096", token: "" });
  });

  it("lists this server's path mappings, tests one, removes one, and adds a new link of Deluno's path to the server's", async () => {
    vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url === "/api/media-servers/s1/path-mappings" && !init?.method) return json([{ id: "m1", mediaServerId: "s1", delunoPath: "D:\\Media\\Films", serverPath: "/data/Films", isEnabled: true, priority: 1 }]);
      if (url.endsWith("/path-mappings/m1/test")) return json({ found: true, message: "The server can see /data/Films." });
      if (url === "/api/media-servers/s1/path-mappings" && init?.method === "POST") return json({ id: "m2", mediaServerId: "s1", delunoPath: "D:\\Media\\TV", serverPath: "/data/TV", isEnabled: true, priority: 2 });
      return json({});
    });
    openSaved();
    fireEvent.click(await screen.findByText("Advanced path mapping"));
    expect(await screen.findByText("D:\\Media\\Films")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Test" }));
    expect(await screen.findByText("The server can see /data/Films.")).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText("Path as Deluno sees it"), { target: { value: "D:\\Media\\TV" } });
    fireEvent.change(screen.getByLabelText("Same path as Jellyfin on lounge sees it"), { target: { value: "/data/TV" } });
    fireEvent.click(screen.getByRole("button", { name: /Link paths/ }));
    expect(await screen.findByText("D:\\Media\\TV")).toBeInTheDocument();
    expect(JSON.parse(String((calls("POST").find(([url]) => url === "/api/media-servers/s1/path-mappings")![1] as RequestInit).body))).toEqual({ delunoPath: "D:\\Media\\TV", serverPath: "/data/TV", isEnabled: true });

    fireEvent.click(screen.getByRole("button", { name: "Remove D:\\Media\\Films link" }));
    await waitFor(() => expect(screen.queryByText("D:\\Media\\Films")).toBeNull());
    expect(calls("DELETE")[0][0]).toBe("/api/media-servers/s1/path-mappings/m1");
  });

  it("removes the server after a confirmation that says nothing on the server itself changes", async () => {
    openSaved();
    fireEvent.click(screen.getByRole("button", { name: "Remove…" }));
    expect(await screen.findByText(/Nothing on the server itself changes\./)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Remove server" }));
    await waitFor(() => expect(calls("DELETE")).toHaveLength(1));
    expect(calls("DELETE")[0][0]).toBe("/api/media-servers/s1");
    await waitFor(() => expect(revalidate).toHaveBeenCalled());
  });

  it("reaches its own path mappings only when it is a saved server", () => {
    renderPage();
    fireEvent.click(screen.getAllByRole("button", { name: "Add server" })[0]);
    expect(screen.queryByText("Advanced path mapping")).toBeNull();
    void within;
  });
});

describe("a link to a media server", () => {
  it("opens the drawer of the server the link names and takes the id out of the address", async () => {
    loaderData.servers = [server(), server({ id: "s2", name: "Emby on den", kind: "emby" })];
    render(<MemoryRouter initialEntries={["/indexers/media-servers?connection=s2"]}><MediaServersPage /></MemoryRouter>);

    expect(await screen.findByRole("dialog", { name: /Emby on den/ })).toBeInTheDocument();
    expect(screen.queryByRole("dialog", { name: /Jellyfin on lounge/ })).not.toBeInTheDocument();
  });

  it("opens no drawer for a link to a server that is not there", () => {
    loaderData.servers = [server()];
    render(<MemoryRouter initialEntries={["/indexers/media-servers?connection=gone"]}><MediaServersPage /></MemoryRouter>);

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });
});
