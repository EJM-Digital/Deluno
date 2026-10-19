import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { authedFetch } from "../../lib/use-auth";

/**
 * Find & Download > Request servers (Seerr): what the drawer does with the server's answers (field errors, a key that is never shown, a failed
 * save, test, switch, copy, secret and remove), the fallback pickers, and the list of recent acquisitions. The Add, Test, webhook and Remove
 * basics are in seerr-servers-card.test.tsx.
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
const libraries = [
  { id: "lib-films", name: "Films", mediaType: "movies" },
  { id: "lib-kids", name: "Kids films", mediaType: "movies" },
  { id: "lib-shows", name: "Shows", mediaType: "tv" }
];
const profiles = [{ id: "prof-1", name: "Small 720p" }];
const onChanged = vi.fn();
const renderCard = (servers: unknown[] = [], extra: { libraries?: unknown[]; qualityProfiles?: unknown[] } = {}) => render(
  <MemoryRouter><SeerrServersCard openId={null} servers={servers as never} cleanups={[]} libraries={(extra.libraries ?? []) as never} qualityProfiles={(extra.qualityProfiles ?? []) as never} onChanged={onChanged} /></MemoryRouter>
);
const calls = (method: string) => vi.mocked(authedFetch).mock.calls.filter(([, init]) => (init as RequestInit | undefined)?.method === method);
const bodyOf = (call: unknown[]) => JSON.parse(String((call[1] as RequestInit).body));
const answering = (handler: (url: string, init?: RequestInit) => Response | Promise<Response> | undefined) =>
  vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => handler(String(input), init) ?? (String(input).includes("/acquisitions") ? json([]) : json({})));

beforeEach(() => {
  cleanup();
  vi.mocked(authedFetch).mockReset();
  vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => String(input).includes("/acquisitions") ? json([]) : json({}));
  onChanged.mockReset();
});

describe("what the server says about the form", () => {
  it("puts the address and key problems the server found under their fields and says 'Check the highlighted fields.'", async () => {
    answering((url, init) => url === "/api/seerr-servers" && init?.method === "POST"
      ? json({ errors: { baseUrl: ["Use the server's http or https address, for example http://192.168.1.20:5055."], apiKey: ["Paste the API key from Seerr's Settings → General."] } }, 400)
      : undefined);
    renderCard();
    fireEvent.click(screen.getByRole("button", { name: "Add Seerr" }));
    fireEvent.change(screen.getByPlaceholderText(/5055/), { target: { value: "ftp://nope" } });

    fireEvent.click(screen.getByRole("button", { name: "Add server" }));

    expect(await screen.findByText("Check the highlighted fields.")).toBeInTheDocument();
    expect(screen.getByText("Use the server's http or https address, for example http://192.168.1.20:5055.")).toBeInTheDocument();
    expect(screen.getByText("Paste the API key from Seerr's Settings → General.")).toBeInTheDocument();
    expect(onChanged).not.toHaveBeenCalled();
  });

  it("says 'The server could not be saved.' when the save fails without a field to blame", async () => {
    answering((url, init) => url === "/api/seerr-servers" && init?.method === "POST" ? json({}, 500) : undefined);
    renderCard();
    fireEvent.click(screen.getByRole("button", { name: "Add Seerr" }));
    fireEvent.change(screen.getByPlaceholderText(/5055/), { target: { value: "http://new.example.test:5055" } });
    fireEvent.change(screen.getByLabelText("API key"), { target: { value: "key-9" } });

    fireEvent.click(screen.getByRole("button", { name: "Add server" }));

    expect(await screen.findByText("The server could not be saved.")).toBeInTheDocument();
  });

  it("never shows a saved key: the field is empty with 'unchanged' in it, says it is stored encrypted, and saving with it empty sends a blank key", async () => {
    answering((url, init) => url === "/api/seerr-servers/z1" && init?.method === "PUT" ? json(server()) : undefined);
    renderCard([server()]);
    fireEvent.click(screen.getByText("Seerr on den"));

    const key = screen.getByLabelText("API key") as HTMLInputElement;
    expect(key.value).toBe("");
    expect(key.placeholder).toContain("unchanged");
    expect(key.type).toBe("password");
    expect(screen.getByText("Stored encrypted. Leave empty to keep the current one.")).toBeInTheDocument();
    expect(document.body.innerHTML).not.toContain("0123456789abcdef");

    fireEvent.change(screen.getByPlaceholderText(/5055/), { target: { value: "http://den.example.test:5056" } });
    fireEvent.click(screen.getByRole("button", { name: "Save server" }));
    await waitFor(() => expect(calls("PUT")).toHaveLength(1));
    expect(bodyOf(calls("PUT")[0])).toMatchObject({ baseUrl: "http://den.example.test:5056", apiKey: "" });
    expect(await screen.findByText("Saved just now")).toBeInTheDocument();
  });
});

describe("Test connection", () => {
  it("tests a saved server with the form as it stands and shows the answer, and asks for the highlighted fields first when the form is not valid", async () => {
    answering((url) => url === "/api/seerr-servers/z1/test" ? json({ ok: true, message: "Reached Seerr 2.0.0 (Den)." }) : undefined);
    renderCard([server({ lastHealthTestUtc: null })]);
    fireEvent.click(screen.getByText("Seerr on den"));
    fireEvent.click(screen.getByRole("button", { name: /Test connection/ }));
    expect(await screen.findByText("Reached Seerr 2.0.0 (Den).")).toBeInTheDocument();
    expect(calls("POST").some(([url]) => url === "/api/seerr-servers/z1/test")).toBe(true);
    expect(onChanged).toHaveBeenCalled();

    cleanup();
    answering((url) => url === "/api/seerr-servers/test" ? json({ errors: { baseUrl: ["Use the server's http or https address."] } }, 400) : undefined);
    renderCard();
    fireEvent.click(screen.getByRole("button", { name: "Add Seerr" }));
    fireEvent.click(screen.getByRole("button", { name: /Test connection/ }));
    expect(await screen.findByText("Fill in the highlighted fields first.")).toBeInTheDocument();
    expect(screen.getByText("Use the server's http or https address.")).toBeInTheDocument();
  });
});

describe("the On switch", () => {
  it("keeps the fallback libraries and profiles it was saved with, and stays silent when the answer is a refusal", async () => {
    answering((url, init) => url === "/api/seerr-servers/z1" && init?.method === "PUT" ? json({}, 500) : undefined);
    renderCard([server({ movieLibraryId: "lib-films", movieQualityProfileId: "prof-1", seriesLibraryId: "lib-shows", seriesQualityProfileId: "prof-1" })]);

    fireEvent.click(screen.getByRole("switch", { name: "Pause Seerr on den" }));

    await waitFor(() => expect(calls("PUT")).toHaveLength(1));
    expect(bodyOf(calls("PUT")[0])).toMatchObject({ isEnabled: false, movieLibraryId: "lib-films", movieQualityProfileId: "prof-1", seriesLibraryId: "lib-shows", seriesQualityProfileId: "prof-1" });
    await waitFor(() => expect(onChanged).toHaveBeenCalled());
    expect(screen.queryByRole("alert")).toBeNull();
    expect(screen.queryByText(/could not/i)).toBeNull();
  });
});

describe("the webhook address and secret", () => {
  it("copies the webhook address and says Copied, and a browser that refuses the clipboard changes nothing", async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, "clipboard", { value: { writeText }, configurable: true });
    renderCard([server()]);
    fireEvent.click(screen.getByText("Seerr on den"));

    fireEvent.click(screen.getByRole("button", { name: "Copy" }));

    await waitFor(() => expect(writeText).toHaveBeenCalledWith(`${window.location.origin}/api/seerr-servers/z1/webhook`));
    expect(await screen.findByRole("button", { name: "Copied" })).toBeInTheDocument();

    cleanup();
    Object.defineProperty(navigator, "clipboard", { value: { writeText: vi.fn().mockRejectedValue(new Error("denied")) }, configurable: true });
    renderCard([server()]);
    fireEvent.click(screen.getByText("Seerr on den"));
    fireEvent.click(screen.getByRole("button", { name: "Copy" }));
    await waitFor(() => expect(navigator.clipboard.writeText).toHaveBeenCalled());
    expect(screen.getByRole("button", { name: "Copy" })).toBeInTheDocument();
  });

  it("shows the secret masked when there is one and 'Not generated yet' when there is none, and never the secret itself", () => {
    renderCard([server({ hasWebhookSecret: true })]);
    fireEvent.click(screen.getByText("Seerr on den"));
    const masked = screen.getByLabelText("Authorization header value") as HTMLInputElement;
    expect(masked.value).toBe("••••••••••••••••");
    expect(masked.type).toBe("password");
    cleanup();

    renderCard([server({ hasWebhookSecret: false })]);
    fireEvent.click(screen.getByText("Seerr on den"));
    const none = screen.getByLabelText("Authorization header value") as HTMLInputElement;
    expect(none.value).toBe("");
    expect(none.placeholder).toBe("Not generated yet");
    expect(screen.getByRole("button", { name: "Generate" })).toBeInTheDocument();
  });

  it("says 'The webhook secret could not be generated.' when it cannot be made and shows no secret", async () => {
    answering((url) => url.endsWith("/webhook-secret") ? json({}, 500) : undefined);
    renderCard([server()]);
    fireEvent.click(screen.getByText("Seerr on den"));

    fireEvent.click(screen.getByRole("button", { name: "Generate" }));

    expect(await screen.findByText("The webhook secret could not be generated.")).toBeInTheDocument();
    expect((screen.getByLabelText("Authorization header value") as HTMLInputElement).value).toBe("");
  });
});

describe("the fallback pickers", () => {
  it("offers the movie libraries for the movie picker and the TV libraries for the TV one, saves the choice and clears it again", async () => {
    answering((url, init) => url === "/api/seerr-servers/z1" && init?.method === "PUT" ? json(server({ movieLibraryId: "lib-kids", seriesQualityProfileId: "prof-1" })) : undefined);
    renderCard([server({ movieLibraryId: "lib-films" })], { libraries, qualityProfiles: profiles });
    fireEvent.click(screen.getByText("Seerr on den"));
    // The four pickers, in the order the drawer lays them out: movie library, movie profile, TV library, TV profile.
    const [movies, , shows, showProfile] = screen.getAllByRole("combobox") as HTMLSelectElement[];
    expect(within(movies).getAllByRole("option").map((option) => option.textContent)).toEqual(["First movie library", "Films", "Kids films"]);
    expect(within(shows).getAllByRole("option").map((option) => option.textContent)).toEqual(["First TV library", "Shows"]);
    expect(movies.value).toBe("lib-films");

    fireEvent.change(movies, { target: { value: "lib-kids" } });
    fireEvent.change(showProfile, { target: { value: "prof-1" } });
    fireEvent.click(screen.getByRole("button", { name: "Save server" }));
    await waitFor(() => expect(calls("PUT")).toHaveLength(1));
    expect(bodyOf(calls("PUT")[0])).toMatchObject({ movieLibraryId: "lib-kids", seriesQualityProfileId: "prof-1" });

    fireEvent.change(movies, { target: { value: "" } });
    fireEvent.click(await screen.findByRole("button", { name: "Save server" }));
    await waitFor(() => expect(calls("PUT")).toHaveLength(2));
    expect(bodyOf(calls("PUT")[1])).toMatchObject({ movieLibraryId: "" });
  });

  it.skip("Defect in the text: a blank fallback library says it 'uses the first movie library' (and 'the first TV library'), but an approval with no destination rule and no fallback is refused (SeerrAcquisitionService: 'No destination rule matched ... has no fallback'). Found 19 Oct 2026.", () => {
    renderCard([server()], { libraries, qualityProfiles: profiles });
    fireEvent.click(screen.getByText("Seerr on den"));

    expect(screen.queryByText(/Blank uses the first movie library/)).toBeNull();
    expect(screen.queryByText(/Blank uses the first TV library/)).toBeNull();
  });
});

describe("recent acquisitions", () => {
  const acquisition = (over: Record<string, unknown>) => ({
    id: "a1", connectionId: "z1", connectionName: "Seerr on den", mediaType: "movies", entityId: "m1", title: "Sintel", seerrMediaId: 1, status: "added", message: null,
    createdUtc: new Date().toISOString(), updatedUtc: new Date().toISOString(), requestedBy: "Ben", ruleId: null, ruleName: null, ...over
  });

  it("says Loading first, then lists each with when, who asked, the rule or the fallback that placed it, the message and one of the four status chips", async () => {
    let finish: (value: Response) => void = () => undefined;
    vi.mocked(authedFetch).mockImplementation((input: RequestInfo | URL) => String(input).includes("/acquisitions") ? new Promise<Response>((resolve) => { finish = resolve; }) : Promise.resolve(json({})));
    renderCard([server()]);
    fireEvent.click(screen.getByText("Seerr on den"));
    expect(screen.getByText("Loading…")).toBeInTheDocument();

    finish(json([
      acquisition({ id: "a1", title: "Sintel", status: "added", ruleName: "Animation" }),
      acquisition({ id: "a2", title: "Arrival", status: "failed", message: "No destination rule matched", requestedBy: null }),
      acquisition({ id: "a3", title: "Dune", status: "available" }),
      acquisition({ id: "a4", title: "Heat", status: "available-failed", message: "Seerr is unreachable" })
    ]));

    expect(await screen.findByText("Sintel")).toBeInTheDocument();
    expect(screen.getByText(/requested by Ben · placed by the “Animation” rule/)).toBeInTheDocument();
    expect(screen.getByText("Added, waiting to import")).toBeInTheDocument();
    expect(screen.getByText("Could not add")).toBeInTheDocument();
    expect(screen.getByText(/No destination rule matched/)).toBeInTheDocument();
    expect(screen.getByText("Imported and reported available")).toBeInTheDocument();
    expect(screen.getByText(/placed by the fallback library/)).toBeInTheDocument();
    expect(screen.getByText("Imported, but Seerr could not be told")).toBeInTheDocument();
    expect(screen.getByText(/Seerr is unreachable/)).toBeInTheDocument();
  });

  it("shows no section at all when there is nothing to list", async () => {
    renderCard([server()]);
    fireEvent.click(screen.getByText("Seerr on den"));

    await waitFor(() => expect(screen.queryByText("Loading…")).toBeNull());
    expect(screen.queryByText("Recent acquisitions")).toBeNull();
  });
});

describe("Remove", () => {
  it("says 'The server could not be removed.' and keeps the server when the answer is a refusal", async () => {
    answering((url, init) => url === "/api/seerr-servers/z1" && init?.method === "DELETE" ? json({}, 500) : undefined);
    renderCard([server()]);
    fireEvent.click(screen.getByText("Seerr on den"));
    fireEvent.click(screen.getByRole("button", { name: "Remove…" }));
    fireEvent.click(await screen.findByRole("button", { name: "Remove server" }));

    expect(await screen.findByText("The server could not be removed.")).toBeInTheDocument();
    expect(screen.getByRole("dialog", { name: "Seerr on den" })).toBeInTheDocument();
    expect(onChanged).not.toHaveBeenCalled();
  });
});
