import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { authedFetch } from "../../lib/use-auth";

/**
 * Find & Download > Request servers (Seerr), the rest (#1329): list sorting and empty cells, leaving the drawer with edits, how long Copied stays,
 * the health lines, what is still to clear, and the answers the server can give that the other files do not reach.
 */

vi.setConfig({ testTimeout: 30_000 });
vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
vi.mock("../../lib/connection-versions", () => ({ useConnectionVersions: () => ({ versions: new Map() }), versionKey: (kind: string, id: string) => `${kind}:${id}` }));
vi.mock("./connection-update-panel", () => ({ ConnectionUpdatePanel: () => null }));
vi.mock("./connection-version", () => ({ ConnectionVersionBeside: () => null }));
vi.mock("./tag-picker", () => ({ TagPicker: ({ label, onChange }: { label?: string; onChange: (value: string) => void }) => <input aria-label={label} onChange={(event) => onChange(event.target.value)} /> }));

const { SeerrServersCard } = await import("./seerr-servers-card");
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });
const server = (over: Record<string, unknown> = {}) => ({
  id: "z1", name: "Seerr on den", baseUrl: "http://den.example.test:5055", hasApiKey: true, isEnabled: true, healthStatus: "healthy", lastHealthMessage: "Reached it.",
  lastHealthLatencyMs: 20, lastHealthTestUtc: new Date().toISOString(), serverName: "Den", version: "2.0.0", movieLibraryId: null, movieQualityProfileId: null,
  seriesLibraryId: null, seriesQualityProfileId: null, hasWebhookSecret: false, tags: "", tags4k: "", ...over
});
const cleanup1 = (over: Record<string, unknown> = {}) => ({ serverId: "z1", entityId: "m1", title: "Sintel", attempts: 2, problem: "Seerr said no", attemptedUtc: new Date().toISOString(), ...over });
const onChanged = vi.fn();
const renderCard = (servers: unknown[] = [], cleanups: unknown[] = []) => render(
  <MemoryRouter><SeerrServersCard openId={null} servers={servers as never} cleanups={cleanups as never} libraries={[]} qualityProfiles={[]} onChanged={onChanged} /></MemoryRouter>
);
const answering = (handler: (url: string, init?: RequestInit) => Response | Promise<Response> | undefined) =>
  vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => handler(String(input), init) ?? (String(input).includes("/acquisitions") ? json([]) : json({})));

beforeEach(() => {
  cleanup();
  vi.mocked(authedFetch).mockReset();
  answering(() => undefined);
  onChanged.mockReset();
});
afterEach(() => vi.useRealTimers());

describe("the list of request servers", () => {
  it("says Not tested yet for a server that has not answered, and what is still to clear on a server with removed titles", () => {
    renderCard([server({ serverName: null, lastHealthTestUtc: null, lastHealthMessage: null }), server({ id: "z2", name: "Second" })], [cleanup1(), cleanup1({ serverId: "z2", problem: null })]);
    expect(screen.getByText("Not tested yet")).toBeInTheDocument();
    expect(screen.getByText("1 removed title still to clear: Seerr said no")).toBeInTheDocument();
    expect(screen.getByText("1 removed title still to clear: waiting")).toBeInTheDocument();
  });

  it("sorts by name, address, what the server calls itself, last test, status and whether it is on", () => {
    renderCard([
      server({ id: "b", name: "Bravo", baseUrl: "http://b.example.test", serverName: null, lastHealthTestUtc: null, isEnabled: false, healthStatus: "unreachable" }),
      server({ id: "a", name: "alpha", baseUrl: "http://a.example.test", serverName: "Zed", lastHealthTestUtc: "2026-10-01T00:00:00Z" }),
      server({ id: "c", name: "Charlie", baseUrl: "http://c.example.test", serverName: "Amy", lastHealthTestUtc: "2026-09-01T00:00:00Z" })
    ]);
    const names = () => screen.getAllByRole("row").slice(1).map((row) => within(row).getAllByRole("cell")[0].textContent?.slice(0, 5));
    for (const heading of [/^Server/, /^Address/, /^Reports itself as/, /^Last test/, /^Status/, /^On/]) {
      fireEvent.click(within(screen.getByRole("columnheader", { name: heading })).getByRole("button"));
      expect(names()).toHaveLength(3);
    }
    fireEvent.click(within(screen.getByRole("columnheader", { name: /^Server/ })).getByRole("button"));
    expect(names()).toEqual(["alpha", "Bravo", "Charl"]);
    fireEvent.click(within(screen.getByRole("columnheader", { name: /^Reports itself as/ })).getByRole("button"));
    expect(names()[0]).toBe("Bravo");
  });
});

describe("leaving the drawer", () => {
  it("closes at once with Escape or Cancel when nothing was changed", async () => {
    renderCard([server()]);
    fireEvent.click(screen.getByText("Seerr on den"));
    fireEvent.keyDown(await screen.findByRole("dialog"), { key: "Escape" });
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());

    fireEvent.click(screen.getByText("Seerr on den"));
    fireEvent.click(await screen.findByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });

  it("asks before it drops edits, whether closed with Escape or Cancel, keeps them on Keep editing and drops them on Discard", async () => {
    renderCard([server()]);
    fireEvent.click(screen.getByText("Seerr on den"));
    fireEvent.change(await screen.findByPlaceholderText(/5055/), { target: { value: "http://other.example.test:5055" } });

    fireEvent.keyDown(screen.getByRole("dialog", { name: /Seerr on den/ }), { key: "Escape" });
    expect(await screen.findByText("Discard unsaved changes?")).toBeInTheDocument();
    const asking = screen.getByText("Discard unsaved changes?").closest("[role=dialog],[role=alertdialog]") as HTMLElement;
    fireEvent.click(within(asking).getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByText("Discard unsaved changes?")).toBeNull());
    expect(screen.getByPlaceholderText(/5055/)).toHaveValue("http://other.example.test:5055");

    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    fireEvent.click(await screen.findByRole("button", { name: "Discard" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    fireEvent.click(screen.getByText("Seerr on den"));
    expect(await screen.findByPlaceholderText(/5055/)).toHaveValue("http://den.example.test:5055");
  });
});

describe("the rest of the drawer's controls", () => {
  const full = (servers: unknown[]) => render(
    <MemoryRouter>
      <SeerrServersCard
        openId={null}
        servers={servers as never}
        cleanups={[]}
        libraries={[{ id: "lib-films", name: "Films", mediaType: "movies" }, { id: "lib-shows", name: "Shows", mediaType: "tv" }] as never}
        qualityProfiles={[{ id: "prof-1", name: "Small 720p" }] as never}
        onChanged={onChanged}
      />
    </MemoryRouter>
  );

  it("saves the quality profiles, the TV library, the tags and the Enabled switch as they were set", async () => {
    answering((url, init) => (url === "/api/seerr-servers/z1" && init?.method === "PUT" ? json(server()) : undefined));
    full([server()]);
    fireEvent.click(screen.getByText("Seerr on den"));
    await screen.findByPlaceholderText(/5055/);
    const [, movieProfile, tvLibrary, tvProfile] = within(screen.getByRole("dialog")).getAllByRole("combobox");
    fireEvent.change(movieProfile, { target: { value: "prof-1" } });
    fireEvent.change(tvLibrary, { target: { value: "lib-shows" } });
    fireEvent.change(tvProfile, { target: { value: "prof-1" } });
    fireEvent.change(screen.getByLabelText("Seerr tags for every title"), { target: { value: "seerr" } });
    fireEvent.change(screen.getByLabelText("Extra Seerr tags for a 4K request"), { target: { value: "4k" } });
    fireEvent.click(within(screen.getByRole("dialog")).getByRole("switch"));
    fireEvent.click(screen.getByRole("button", { name: "Save server" }));
    await waitFor(() => expect(vi.mocked(authedFetch).mock.calls.some(([, init]) => (init as RequestInit | undefined)?.method === "PUT")).toBe(true));
    const put = vi.mocked(authedFetch).mock.calls.find(([, init]) => (init as RequestInit | undefined)?.method === "PUT")!;
    expect(JSON.parse(String((put[1] as RequestInit).body))).toMatchObject({ movieQualityProfileId: "prof-1", seriesLibraryId: "lib-shows", seriesQualityProfileId: "prof-1", tags: "seerr", tags4k: "4k", isEnabled: false });
  });

  it("selects the whole address when the webhook address or the secret box is focused", async () => {
    answering((url, init) => (url === "/api/seerr-servers/z1/webhook-secret" && init?.method === "POST" ? json({ secret: "s3cret" }) : undefined));
    renderCard([server()]);
    fireEvent.click(screen.getByText("Seerr on den"));
    const address = (await screen.findByLabelText("Webhook URL")) as HTMLInputElement;
    const select = vi.spyOn(address, "select");
    fireEvent.focus(address);
    expect(select).toHaveBeenCalled();
    fireEvent.click(screen.getByRole("button", { name: "Generate" }));
    const secret = (await screen.findByDisplayValue("s3cret")) as HTMLInputElement;
    const selectSecret = vi.spyOn(secret, "select");
    fireEvent.focus(secret);
    expect(selectSecret).toHaveBeenCalled();
  });
});

describe("the health lines of a saved server", () => {
  it("leaves out the latency and the message when the server gave none", () => {
    renderCard([server({ lastHealthLatencyMs: null, lastHealthMessage: null })]);
    fireEvent.click(screen.getByText("Seerr on den"));
    expect(screen.queryByText(/ ms$/)).toBeNull();
    expect(screen.queryByText("Message")).toBeNull();
  });

  it("shows the latency and the message when it gave them", () => {
    renderCard([server()]);
    fireEvent.click(screen.getByText("Seerr on den"));
    expect(screen.getByText(/· 20 ms/)).toBeInTheDocument();
    expect(screen.getByText("Message")).toBeInTheDocument();
  });
});

describe("what is still to clear", () => {
  it("lists each removed title with Seerr's reason, or Waiting when it gave none", () => {
    renderCard([server()], [cleanup1(), cleanup1({ entityId: "m2", title: "Arrival", problem: null }), cleanup1({ serverId: "other", entityId: "m3", title: "Elsewhere" })]);
    fireEvent.click(screen.getByText("Seerr on den"));
    const section = screen.getByText("Still to clear").closest("section")!;
    expect(within(section).getByText("Sintel")).toBeInTheDocument();
    expect(within(section).getByText(/Seerr said no/)).toBeInTheDocument();
    expect(within(section).getByText(/^Waiting/)).toBeInTheDocument();
    expect(within(section).queryByText("Elsewhere")).toBeNull();
  });

  it("shows no such section for a server with nothing to clear", () => {
    renderCard([server()]);
    fireEvent.click(screen.getByText("Seerr on den"));
    expect(screen.queryByText("Still to clear")).toBeNull();
  });
});

describe("Copied", () => {
  beforeEach(() => {
    Object.defineProperty(navigator, "clipboard", { value: { writeText: vi.fn().mockResolvedValue(undefined) }, configurable: true });
  });

  it("goes back to Copy after two seconds", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    renderCard([server()]);
    fireEvent.click(screen.getByText("Seerr on den"));
    fireEvent.click(screen.getByRole("button", { name: "Copy" }));
    expect(await screen.findByRole("button", { name: "Copied" })).toBeInTheDocument();
    act(() => { vi.advanceTimersByTime(1500); });
    expect(screen.getByRole("button", { name: "Copied" })).toBeInTheDocument();
    act(() => { vi.advanceTimersByTime(600); });
    expect(screen.getByRole("button", { name: "Copy" })).toBeInTheDocument();
  });

  it("keeps saying Copied for the secret copied after the address, when the address's two seconds run out", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    answering((url, init) => (url === "/api/seerr-servers/z1/webhook-secret" && init?.method === "POST" ? json({ secret: "s3cret" }) : undefined));
    renderCard([server()]);
    fireEvent.click(screen.getByText("Seerr on den"));
    fireEvent.click(screen.getByRole("button", { name: "Generate" }));
    await screen.findByDisplayValue("s3cret");
    const copies = screen.getAllByRole("button", { name: "Copy" });
    fireEvent.click(copies[0]);
    await screen.findAllByRole("button", { name: "Copied" });
    act(() => { vi.advanceTimersByTime(1000); });
    fireEvent.click(screen.getAllByRole("button", { name: "Copy" })[0]);
    await waitFor(() => expect(screen.getAllByRole("button", { name: "Copied" })).toHaveLength(1));
    act(() => { vi.advanceTimersByTime(1000); });
    expect(screen.getAllByRole("button", { name: "Copied" })).toHaveLength(1);
    act(() => { vi.advanceTimersByTime(1000); });
    expect(screen.queryByRole("button", { name: "Copied" })).toBeNull();
  });
});

describe("what the server can answer", () => {
  it("keeps a server's tags when the list sent none at all", () => {
    renderCard([server({ tags: null, tags4k: null })]);
    fireEvent.click(screen.getByText("Seerr on den"));
    expect(screen.getByLabelText("Seerr tags for every title")).toBeInTheDocument();
  });

  it("puts a blank under a field the server named but gave no words for", async () => {
    answering((url, init) => (url === "/api/seerr-servers" && init?.method === "POST" ? json({ errors: { baseUrl: [] } }, 400) : undefined));
    renderCard();
    fireEvent.click(screen.getByRole("button", { name: "Add Seerr" }));
    fireEvent.change(screen.getByPlaceholderText(/5055/), { target: { value: "x" } });
    fireEvent.click(screen.getByRole("button", { name: "Add server" }));
    expect(await screen.findByText("Check the highlighted fields.")).toBeInTheDocument();
  });

  it("says Saved just now after saving a server that already exists", async () => {
    answering((url, init) => (url === "/api/seerr-servers/z1" && init?.method === "PUT" ? json(server({ baseUrl: "http://moved.example.test:5055" })) : undefined));
    renderCard([server()]);
    fireEvent.click(screen.getByText("Seerr on den"));
    fireEvent.change(await screen.findByPlaceholderText(/5055/), { target: { value: "http://moved.example.test:5055" } });
    fireEvent.click(screen.getByRole("button", { name: "Save server" }));
    expect(await screen.findByText("Saved just now")).toBeInTheDocument();
  });

  it("says Could not save when the request fails without a reason", async () => {
    answering((url, init) => { if (url === "/api/seerr-servers/z1" && init?.method === "PUT") throw "offline"; return undefined; });
    renderCard([server()]);
    fireEvent.click(screen.getByText("Seerr on den"));
    fireEvent.change(await screen.findByPlaceholderText(/5055/), { target: { value: "http://moved.example.test:5055" } });
    fireEvent.click(screen.getByRole("button", { name: "Save server" }));
    expect(await screen.findByText("Could not save")).toBeInTheDocument();
  });

  it("says the test could not run when Deluno cannot be reached", async () => {
    answering((url) => { if (url === "/api/seerr-servers/test") throw new Error("offline"); return undefined; });
    renderCard();
    fireEvent.click(screen.getByRole("button", { name: "Add Seerr" }));
    fireEvent.click(screen.getByRole("button", { name: /Test connection/ }));
    expect(await screen.findByText("The test could not run. Check that Deluno is still reachable.")).toBeInTheDocument();
  });

  it("says the test could not run, with the code, when the answer is a plain failure", async () => {
    answering((url) => (url === "/api/seerr-servers/test" ? new Response("nope", { status: 503 }) : undefined));
    renderCard();
    fireEvent.click(screen.getByRole("button", { name: "Add Seerr" }));
    fireEvent.click(screen.getByRole("button", { name: /Test connection/ }));
    expect(await screen.findByText("The test could not run (HTTP 503).")).toBeInTheDocument();
  });

  it("does nothing when Remove is confirmed for a server that has since gone from the list", async () => {
    const view = renderCard([server()]);
    fireEvent.click(screen.getByText("Seerr on den"));
    fireEvent.click(await screen.findByRole("button", { name: "Remove…" }));
    view.rerender(<MemoryRouter><SeerrServersCard openId={null} servers={[]} cleanups={[]} libraries={[]} qualityProfiles={[]} onChanged={onChanged} /></MemoryRouter>);
    fireEvent.click(await screen.findByRole("button", { name: "Remove server" }));
    expect(vi.mocked(authedFetch).mock.calls.some(([, init]) => (init as RequestInit | undefined)?.method === "DELETE")).toBe(false);
  });

  it("lists an acquisition with a status it does not know by that status, and shows nothing when the list cannot be read", async () => {
    answering((url) => (url.includes("/acquisitions") ? json([{ id: "a9", connectionId: "z1", connectionName: "x", mediaType: "movies", entityId: "m", title: "Odd", seerrMediaId: 1, status: "mystery", message: null, createdUtc: new Date().toISOString(), updatedUtc: new Date().toISOString(), requestedBy: null, ruleId: null, ruleName: null }]) : undefined));
    renderCard([server()]);
    fireEvent.click(screen.getByText("Seerr on den"));
    expect(await screen.findByText("mystery")).toBeInTheDocument();
    cleanup();

    let fail: (reason: Error) => void = () => undefined;
    vi.mocked(authedFetch).mockImplementation((input: RequestInfo | URL) => String(input).includes("/acquisitions") ? new Promise<Response>((_, reject) => { fail = reject; }) : Promise.resolve(json({})));
    const gone = renderCard([server()]);
    fireEvent.click(screen.getByText("Seerr on den"));
    gone.unmount();
    await act(async () => { fail(new Error("late")); });
    cleanup();

    answering((url) => { if (url.includes("/acquisitions")) throw new Error("offline"); return undefined; });
    renderCard([server()]);
    fireEvent.click(screen.getByText("Seerr on den"));
    await waitFor(() => expect(screen.queryByText("Loading…")).toBeNull());
    expect(screen.queryByText("Recent acquisitions")).toBeNull();
  });
});
