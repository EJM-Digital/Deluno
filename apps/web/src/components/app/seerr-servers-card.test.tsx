import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { authedFetch } from "../../lib/use-auth";

/**
 * Find & Download > Request servers (Seerr): the Add Seerr drawer, the connection test, the webhook URL and the secret that is
 * shown once, the On switch, and Remove.
 */

vi.setConfig({ testTimeout: 30_000 });
vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
vi.mock("../../lib/connection-versions", () => ({ useConnectionVersions: () => ({ versions: new Map() }), versionKey: (kind: string, id: string) => `${kind}:${id}` }));
vi.mock("./connection-update-panel", () => ({ ConnectionUpdatePanel: () => null }));
vi.mock("./connection-version", () => ({ ConnectionVersionBeside: () => null }));
vi.mock("./tag-picker", () => ({ TagPicker: ({ label }: { label?: string }) => <input aria-label={label} /> }));

const { SeerrServersCard } = await import("./seerr-servers-card");
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });
const server = (over: Record<string, unknown> = {}) => ({
  id: "z1", name: "Seerr on den", baseUrl: "http://den.example.test:5055", hasApiKey: true, isEnabled: true, healthStatus: "healthy", lastHealthMessage: "Reached it.",
  lastHealthLatencyMs: 20, lastHealthTestUtc: new Date().toISOString(), serverName: "Den", version: "2.0.0", movieLibraryId: null, movieQualityProfileId: null,
  seriesLibraryId: null, seriesQualityProfileId: null, hasWebhookSecret: false, tags: "", tags4k: "", ...over
});
const onChanged = vi.fn();
const renderCard = (servers: unknown[] = []) => render(
  <MemoryRouter><SeerrServersCard openId={null} servers={servers as never} cleanups={[]} libraries={[]} qualityProfiles={[]} onChanged={onChanged} /></MemoryRouter>
);
const calls = (method: string) => vi.mocked(authedFetch).mock.calls.filter(([, init]) => (init as RequestInit | undefined)?.method === method);
const bodyOf = (call: unknown[]) => JSON.parse(String((call[1] as RequestInit).body));

beforeEach(() => {
  cleanup();
  vi.mocked(authedFetch).mockReset();
  vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => String(input).includes("/acquisitions") ? json([]) : json({}));
  onChanged.mockReset();
});

describe("Add Seerr", () => {
  it("asks for the address and the key with where to find it in Seerr, adds by POST, stays on the saved server and says Server added", async () => {
    vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) =>
      String(input) === "/api/seerr-servers" && init?.method === "POST" ? json(server({ id: "new", name: "Seerr on new" }), 201) : json([]));
    renderCard();
    fireEvent.click(screen.getByRole("button", { name: "Add Seerr" }));
    expect(screen.getByText("Settings → General → API Key in Seerr.")).toBeInTheDocument();
    fireEvent.change(screen.getByPlaceholderText(/5055/), { target: { value: "http://new.example.test:5055" } });
    fireEvent.change(screen.getByLabelText("API key"), { target: { value: "key-9" } });
    fireEvent.click(screen.getByRole("button", { name: "Add server" }));
    await waitFor(() => expect(screen.getByText("Server added")).toBeInTheDocument());
    expect(bodyOf(calls("POST")[0])).toMatchObject({ baseUrl: "http://new.example.test:5055", apiKey: "key-9", isEnabled: true });
    expect(onChanged).toHaveBeenCalled();
  });

  it("tests a draft and says which server and version answered, or why it did not", async () => {
    vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => String(input) === "/api/seerr-servers/test" ? json({ ok: true, message: "Reached Seerr 2.0.0 (Den)." }) : json([]));
    renderCard();
    fireEvent.click(screen.getByRole("button", { name: "Add Seerr" }));
    fireEvent.change(screen.getByPlaceholderText(/5055/), { target: { value: "http://new.example.test:5055" } });
    fireEvent.click(screen.getByRole("button", { name: /Test connection/ }));
    expect(await screen.findByText("Reached Seerr 2.0.0 (Den).")).toBeInTheDocument();

    vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => String(input) === "/api/seerr-servers/test" ? json({ ok: false, message: "Seerr refused the key." }) : json([]));
    fireEvent.click(screen.getByRole("button", { name: /Test connection/ }));
    const refused = await screen.findByText("Seerr refused the key.");
    expect(refused).toHaveClass("text-destructive");
  });
});

describe("the webhook that turns approvals into downloads", () => {
  it("shows the webhook address of the saved server and asks to save first for a new one", () => {
    renderCard([server()]);
    fireEvent.click(screen.getByText("Seerr on den"));
    expect((screen.getByLabelText("Webhook URL") as HTMLInputElement).value).toBe(`${window.location.origin}/api/seerr-servers/z1/webhook`);
    expect(screen.getByText("Generate a secret so Deluno can tell this is really Seerr calling.")).toBeInTheDocument();
    cleanup();
    renderCard();
    fireEvent.click(screen.getByRole("button", { name: "Add Seerr" }));
    expect(screen.queryByLabelText("Webhook URL")).toBeNull();
  });

  it("generates the secret, shows it once with a Copy button, and offers Regenerate afterwards with a note that it must be pasted again", async () => {
    vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => String(input).endsWith("/webhook-secret") ? json({ secret: "s3cret-value-123" }) : json([]));
    renderCard([server()]);
    fireEvent.click(screen.getByText("Seerr on den"));
    fireEvent.click(screen.getByRole("button", { name: "Generate" }));
    await waitFor(() => expect((screen.getByLabelText("Authorization header value") as HTMLInputElement).value).toBe("s3cret-value-123"));
    expect(screen.getByText("Shown once. Paste it into Seerr's Authorization Header field now.")).toBeInTheDocument();
    expect(calls("POST")[0][0]).toBe("/api/seerr-servers/z1/webhook-secret");
    cleanup();
    renderCard([server({ hasWebhookSecret: true })]);
    fireEvent.click(screen.getByText("Seerr on den"));
    expect((screen.getByLabelText("Authorization header value") as HTMLInputElement).value).toBe("••••••••••••••••");
    expect(screen.getByRole("button", { name: "Regenerate" })).toBeInTheDocument();
    expect(screen.getByText(/Regenerating replaces it, so the webhook needs re-pasting into Seerr\./)).toBeInTheDocument();
  });
});

describe("a saved Seerr server", () => {
  it("pauses with its switch by PUT with a blank key, so the saved key is kept", async () => {
    renderCard([server()]);
    fireEvent.click(screen.getByRole("switch", { name: "Pause Seerr on den" }));
    await waitFor(() => expect(calls("PUT")).toHaveLength(1));
    expect(calls("PUT")[0][0]).toBe("/api/seerr-servers/z1");
    expect(bodyOf(calls("PUT")[0])).toMatchObject({ name: "Seerr on den", apiKey: "", isEnabled: false });
    expect(onChanged).toHaveBeenCalled();
  });

  it("removes after a confirmation that says nothing in Seerr changes and rules stop reading it", async () => {
    renderCard([server()]);
    fireEvent.click(screen.getByText("Seerr on den"));
    fireEvent.click(screen.getByRole("button", { name: "Remove…" }));
    expect(await screen.findByText(/Nothing in Seerr changes/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Remove server" }));
    await waitFor(() => expect(calls("DELETE")).toHaveLength(1));
    expect(calls("DELETE")[0][0]).toBe("/api/seerr-servers/z1");
    await waitFor(() => expect(onChanged).toHaveBeenCalled());
  });
});
