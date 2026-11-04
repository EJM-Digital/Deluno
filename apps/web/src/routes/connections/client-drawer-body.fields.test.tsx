import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { useState } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { DownloadClientItem } from "../../lib/api";
import { ClientDrawerBody } from "./client-drawer-body";
import { emptyClientForm } from "./forms";
import type { ClientForm } from "./forms";

// #1329: every field of the download client drawer changes the form, the seeding rules load and save, and the health block shows a failure's cause.

const fetchJson = vi.hoisted(() => vi.fn(async (_url: string, _init?: unknown): Promise<unknown> => []));
vi.mock("../../lib/api", async (importOriginal) => ({ ...(await importOriginal<object>()), fetchJson }));
vi.mock("../../lib/api/client", async (importOriginal) => ({ ...(await importOriginal<object>()), fetchJson }));
vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn(async () => new Response("[]", { status: 200 })) }));

afterEach(() => { cleanup(); fetchJson.mockReset(); fetchJson.mockImplementation(async () => []); });

const saved = (over: Record<string, unknown> = {}) => ({
  id: "c1", name: "My qBittorrent", protocol: "qbittorrent", host: "localhost", port: 8080, endpointUrl: "http://localhost:8080", useSsl: false, addPaused: false, moviesCategory: "movies", tvCategory: "tv",
  categoryTemplate: null, priority: 1, isEnabled: true, healthStatus: "healthy", lastHealthMessage: "Reached it.", createdUtc: "2026-10-01T00:00:00Z", updatedUtc: "2026-10-01T00:00:00Z",
  lastHealthTestUtc: new Date(Date.now() - 4 * 60_000).toISOString(), lastHealthLatencyMs: 12, ...over
}) as unknown as DownloadClientItem;

let latest: ClientForm = emptyClientForm();
type Extra = Partial<React.ComponentProps<typeof ClientDrawerBody>>;
function Harness({ initial, editing = null, blocklistSync, extra }: { initial: ClientForm; editing?: DownloadClientItem | null; blocklistSync?: React.ComponentProps<typeof ClientDrawerBody>["blocklistSync"]; extra?: Extra }) {
  const [form, setForm] = useState(initial);
  latest = form;
  return (
    <QueryClientProvider client={new QueryClient()}>
      <ClientDrawerBody
        form={form} setForm={setForm} editing={editing} errors={{}} clearError={() => {}} mappings={[]} newMapping={{ remotePath: "", localPath: "" }} setNewMapping={() => {}}
        busy={null} onAddMapping={() => {}} onRemoveMapping={() => {}} onTestMapping={() => {}} onRemove={() => {}} blocklistSync={blocklistSync} {...extra}
      />
    </QueryClientProvider>
  );
}
const open = (initial: ClientForm, editing?: DownloadClientItem | null, blocklistSync?: React.ComponentProps<typeof ClientDrawerBody>["blocklistSync"], extra?: Extra) => render(<Harness initial={initial} editing={editing} blocklistSync={blocklistSync} extra={extra} />);
const selectWith = (value: string) => screen.getAllByRole("combobox").find((box) => (box as HTMLSelectElement).value === value)!;

describe("the fields of a download client", () => {
  it("choosing a client type fills in its port and categories, and keeps a URL base already typed", () => {
    open({ ...emptyClientForm(), urlBase: "mine" });
    fireEvent.change(screen.getByLabelText("Client"), { target: { value: "sabnzbd" } });
    expect(latest).toMatchObject({ protocol: "sabnzbd", port: "8080", urlBase: "mine" });
    open({ ...emptyClientForm(), urlBase: "  " });
    fireEvent.change(screen.getAllByLabelText("Client")[1], { target: { value: "rtorrent" } });
    expect(latest.protocol).toBe("rtorrent");
    expect(latest.urlBase).toBe("/RPC2");
    expect(latest.moviesCategory).not.toBe("");
  });

  it("choosing something that is not a client type changes nothing", () => {
    open(emptyClientForm());
    const before = latest;
    fireEvent.change(screen.getByLabelText("Client"), { target: { value: "no-such-client" } });
    expect(latest).toEqual(before);
  });

  it("the host, port, categories, secure switch, login, enabled and paused switches each change the form", () => {
    open({ ...emptyClientForm(), protocol: "qbittorrent" });
    fireEvent.change(selectWith("localhost"), { target: { value: "127.0.0.1" } });
    expect(latest.host).toBe("127.0.0.1");
    fireEvent.change(selectWith("8080"), { target: { value: "9091" } });
    expect(latest.port).toBe("9091");
    fireEvent.change(selectWith("deluno-movies"), { target: { value: "radarr" } });
    expect(latest.moviesCategory).toBe("radarr");
    fireEvent.change(selectWith("deluno-tv"), { target: { value: "sonarr" } });
    expect(latest.tvCategory).toBe("sonarr");
    fireEvent.click(screen.getByRole("switch", { name: /Use SSL/ }));
    expect(latest.useSsl).toBe(true);
    fireEvent.click(screen.getByRole("switch", { name: /Enabled/ }));
    expect(latest.isEnabled).toBe(false);
    fireEvent.click(screen.getByRole("switch", { name: /Add downloads paused/ }));
    expect(latest.addPaused).toBe(true);
    fireEvent.change(screen.getByLabelText(/^Username/), { target: { value: "me" } });
    expect(latest.username).toBe("me");
    fireEvent.change(screen.getByLabelText(/^Password/), { target: { value: "secret" } });
    expect(latest.password).toBe("secret");
  });

  it("the completed-download switches and the qBittorrent options each change the form", () => {
    open({ ...emptyClientForm(), protocol: "qbittorrent" });
    fireEvent.click(screen.getByRole("switch", { name: /Remove completed/ }));
    expect(latest.removeCompleted).toBe(true);
    fireEvent.click(screen.getByRole("switch", { name: /Remove failed/ }));
    expect(latest.removeFailed).toBe(true);
    fireEvent.click(screen.getByRole("switch", { name: /Skip hash check/ }));
    expect(latest.skipHashCheck).toBe(true);
    fireEvent.click(screen.getByRole("switch", { name: /Sequential/ }));
    expect(latest.sequentialDownload).toBe(true);
    fireEvent.click(screen.getByRole("switch", { name: /first and last|First and last/ }));
    expect(latest.firstLastPiecePrio).toBe(true);
    fireEvent.change(screen.getByPlaceholderText("e.g. 2.0"), { target: { value: "2.5" } });
    expect(latest.shareRatioLimit).toBe("2.5");
    fireEvent.change(screen.getByPlaceholderText("e.g. 4320"), { target: { value: "100" } });
    expect(latest.shareSeedingTimeLimitMinutes).toBe("100");
    fireEvent.change(screen.getByPlaceholderText("e.g. 60"), { target: { value: "30" } });
    expect(latest.shareInactiveSeedingTimeLimitMinutes).toBe("30");
    fireEvent.change(screen.getByPlaceholderText("e.g. D:\\Downloads\\Movies"), { target: { value: "D:\\Dl" } });
    expect(latest.location).toBe("D:\\Dl");
    fireEvent.change(selectWith("default"), { target: { value: "subfolder" } });
    expect(latest.contentLayout).toBe("subfolder");
  });

  it("the layout help falls back to the plain one for a layout it has no words for", () => {
    open({ ...emptyClientForm(), protocol: "qbittorrent", contentLayout: "something-new" });
    expect(screen.getByText("Deluno does not choose; qBittorrent does what its own setting says.")).toBeTruthy();
  });

  it("the advanced fields change the priority, the tags and, for Usenet and rTorrent, the URL base", () => {
    open({ ...emptyClientForm(), protocol: "sabnzbd" });
    fireEvent.click(screen.getByRole("button", { name: /^Advanced.*Priority/ }));
    fireEvent.change(screen.getByLabelText("Priority"), { target: { value: "7" } });
    expect(latest.priority).toBe("7");
    fireEvent.change(screen.getByLabelText("URL base"), { target: { value: "sab" } });
    expect(latest.urlBase).toBe("sab");
    fireEvent.change(screen.getByLabelText("Download client tags"), { target: { value: "x" } });
    cleanup();
    open({ ...emptyClientForm(), protocol: "rtorrent" });
    fireEvent.click(screen.getByRole("button", { name: /^Advanced.*Priority/ }));
    fireEvent.change(screen.getByLabelText("URL base"), { target: { value: "/rpc" } });
    expect(latest.urlBase).toBe("/rpc");
  });

  it("a tag chosen for the client goes into the form", async () => {
    fetchJson.mockImplementation(async (url: string) => (url === "/api/tags" ? [{ id: "t1", name: "kids", color: "slate", description: "" }] : []));
    open({ ...emptyClientForm(), protocol: "sabnzbd" });
    fireEvent.click(screen.getByRole("button", { name: /^Advanced.*Priority/ }));
    fireEvent.change(screen.getByLabelText("Download client tags"), { target: { value: "kid" } });
    fireEvent.click(await screen.findByRole("button", { name: "Add kids to Download client tags" }));
    expect(latest.tags).toBe("kids");
  });
});

describe("a saved client's page link", () => {
  it("opens from this browser's own address when the client is on this machine, and otherwise says the page is on the computer Deluno runs on", () => {
    open({ ...emptyClientForm(), host: "localhost" }, saved());
    expect(screen.getByRole("link", { name: /Open qBittorrent.s page/ }).getAttribute("href")).toBe("http://localhost:8080/");
    cleanup();
    vi.stubGlobal("location", { ...window.location, hostname: "nas.example" });
    try {
      open({ ...emptyClientForm(), host: "localhost" }, saved());
      expect(screen.queryByRole("link", { name: /Open qBittorrent.s page/ })).toBeNull();
      expect(screen.getByText(/qBittorrent.s own page is on the computer Deluno runs on: http:\/\/localhost:8080\//)).toBeTruthy();
      cleanup();
      open({ ...emptyClientForm(), protocol: "mystery", host: "localhost" }, saved({ protocol: "mystery" }));
      expect(screen.getByText(/^The client.s own page is on the computer Deluno runs on/)).toBeTruthy();
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it("names a client it has no preset for as the client when its page opens", () => {
    open({ ...emptyClientForm(), protocol: "mystery", host: "localhost" }, saved({ protocol: "mystery" }));
    expect(screen.getByRole("link", { name: /Open the client.s page/ })).toBeTruthy();
  });
});

describe("a saved client's health and blocklist sync", () => {
  it("shows why the last test failed, when to expect another attempt, and the action to take", () => {
    open(
      { ...emptyClientForm() },
      saved({ healthStatus: "unreachable", lastHealthFailure: { summary: "The client refused the login.", nextAction: "Check the username and password.", retryAfterUtc: new Date(Date.now() + 10 * 60_000).toISOString() } })
    );
    expect(screen.getByText("The client refused the login.")).toBeTruthy();
    expect(screen.getByText("Check the username and password.")).toBeTruthy();
    expect(screen.getByText(/Next eligible attempt/)).toBeTruthy();
  });

  it("shows a failure with no retry time without one", () => {
    open({ ...emptyClientForm() }, saved({ lastHealthFailure: { summary: "Nope.", nextAction: "Try again." } }));
    expect(screen.queryByText(/Next eligible attempt/)).toBeNull();
  });

  it("says when the content blocklist was last pushed, or why it failed, or that it has not been", () => {
    open({ ...emptyClientForm(), protocol: "qbittorrent" }, saved(), { succeeded: true, message: null, syncedUtc: new Date().toISOString() } as never);
    expect(screen.getByText("Last push succeeded.")).toBeTruthy();
    cleanup();
    open({ ...emptyClientForm(), protocol: "qbittorrent" }, saved(), { succeeded: false, message: null, syncedUtc: new Date().toISOString() } as never);
    expect(screen.getByText("Last push failed: no reason given.")).toBeTruthy();
    cleanup();
    open({ ...emptyClientForm(), protocol: "qbittorrent" }, saved(), { succeeded: false, message: "qBittorrent said no", syncedUtc: new Date().toISOString() } as never);
    expect(screen.getByText("Last push failed: qBittorrent said no.")).toBeTruthy();
    cleanup();
    open({ ...emptyClientForm(), protocol: "qbittorrent" }, saved());
    expect(screen.getByText(/Not synced yet/)).toBeTruthy();
  });
});

describe("the seeding rules", () => {
  const rule = (over: Record<string, unknown> = {}) => ({ id: "r1", priority: 1, categoryPattern: "tv", trackerPattern: null, tagsCsv: null, minSeeders: null, maxInactiveDays: null, isEnabled: true, ...over });
  const openRules = async () => {
    open({ ...emptyClientForm() }, saved());
    fireEvent.click(screen.getByRole("button", { name: /^Seeding rules/ }));
  };

  it("lists each rule in words: its category, tracker, tags and guards, or that it matches nothing", async () => {
    fetchJson.mockImplementation(async (url: string) => (url === "/api/sharing-rules" ? [
      rule({ id: "a", priority: 1, categoryPattern: "tv", trackerPattern: "*.example.com", tagsCsv: "kids", minSeeders: 3, maxInactiveDays: 10 }),
      rule({ id: "b", priority: 2, categoryPattern: null }),
      rule({ id: "c", priority: 3, categoryPattern: null, minSeeders: 2 })
    ] : []));
    await openRules();
    expect(await screen.findByText("category tv, tracker *.example.com, tags kids — hold below 3 seeders · hold past 10 inactive days")).toBeTruthy();
    expect(screen.getByText("Matches nothing yet")).toBeTruthy();
    expect(screen.getByText("hold below 2 seeders")).toBeTruthy();
  });

  it("says when the rules could not be read", async () => {
    fetchJson.mockImplementation(async () => { throw new Error("down"); });
    await openRules();
    expect(await screen.findByText("Deluno could not load the seeding-rule matrix.")).toBeTruthy();
  });

  it("adds a rule after the highest priority, and says so when the server refuses", async () => {
    fetchJson.mockImplementation(async (url: string, init?: unknown) => {
      if (url === "/api/sharing-rules" && (init as RequestInit | undefined)?.method === "POST") return rule({ id: "n", priority: 3, categoryPattern: "movies" });
      return url === "/api/sharing-rules" ? [rule({ priority: 2 })] : [];
    });
    await openRules();
    await screen.findByText("category tv");
    fireEvent.click(screen.getByRole("button", { name: "Add rule" }));
    expect(screen.getByText("Match this rule against a category, a tracker, or a tag.")).toBeTruthy();
    fireEvent.change(screen.getByPlaceholderText("tv-anime"), { target: { value: " movies " } });
    fireEvent.change(screen.getByPlaceholderText("*.example.com"), { target: { value: "tracker.example" } });
    fireEvent.change(screen.getByPlaceholderText("e.g. 5"), { target: { value: "4" } });
    fireEvent.change(screen.getByPlaceholderText("e.g. 10"), { target: { value: "9" } });
    fireEvent.click(screen.getByRole("button", { name: "Add rule" }));
    expect(await screen.findByText("category movies")).toBeTruthy();
    const post = fetchJson.mock.calls.find(([, init]) => (init as RequestInit | undefined)?.method === "POST")!;
    expect(JSON.parse(String((post[1] as RequestInit).body))).toEqual({ priority: 3, categoryPattern: "movies", trackerPattern: "tracker.example", tagsCsv: null, minSeeders: 4, maxInactiveDays: 9, isEnabled: true });
    expect((screen.getByPlaceholderText("tv-anime") as HTMLInputElement).value).toBe("");

    fetchJson.mockImplementation(async () => { throw new Error("no"); });
    fireEvent.change(screen.getByPlaceholderText("tv-anime"), { target: { value: "x" } });
    fireEvent.click(screen.getByRole("button", { name: "Add rule" }));
    expect(await screen.findByText("This rule could not be saved.")).toBeTruthy();
  });

  it("the first rule gets priority 1, and tags alone are a match", async () => {
    fetchJson.mockImplementation(async (url: string, init?: unknown) => ((init as RequestInit | undefined)?.method === "POST" ? rule({ id: "n", categoryPattern: null, tagsCsv: "kids" }) : url === "/api/tags" ? [{ id: "t1", name: "kids", color: "slate", description: "" }] : []));
    await openRules();
    fireEvent.change(await screen.findByLabelText("Seeding rule tags"), { target: { value: "kid" } });
    fireEvent.click(await screen.findByRole("button", { name: "Add kids to Seeding rule tags" }));
    fireEvent.click(screen.getByRole("button", { name: "Add rule" }));
    expect(await screen.findByText("tags kids")).toBeTruthy();
    const post = fetchJson.mock.calls.find(([, init]) => (init as RequestInit | undefined)?.method === "POST")!;
    expect(JSON.parse(String((post[1] as RequestInit).body))).toMatchObject({ priority: 1, tagsCsv: "kids", categoryPattern: null, trackerPattern: null, minSeeders: null, maxInactiveDays: null });
  });

  it("removes a rule, and says so when the server refuses", async () => {
    fetchJson.mockImplementation(async (url: string, init?: unknown) => ((init as RequestInit | undefined)?.method === "DELETE" ? {} : url === "/api/sharing-rules" ? [rule()] : []));
    await openRules();
    fireEvent.click(await screen.findByRole("button", { name: "Remove seeding rule 1" }));
    await waitFor(() => expect(screen.queryByText("category tv")).toBeNull());

    fetchJson.mockImplementation(async (url: string, init?: unknown) => {
      if ((init as RequestInit | undefined)?.method === "DELETE") throw new Error("no");
      return url === "/api/sharing-rules" ? [rule()] : [];
    });
    cleanup();
    await openRules();
    fireEvent.click(await screen.findByRole("button", { name: "Remove seeding rule 1" }));
    expect(await screen.findByText("This rule could not be removed.")).toBeTruthy();
    expect(within(screen.getByRole("button", { name: "Remove seeding rule 1" }).parentElement!).getByText("category tv")).toBeTruthy();
  });
});
