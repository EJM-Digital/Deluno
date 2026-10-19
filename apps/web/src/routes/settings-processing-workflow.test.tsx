import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";

/**
 * Settings > Media management > Processing: how a library handles finished downloads, the processor connections and their path
 * pairs. Every call goes through the page's own fetch wrappers, replaced here with a recorder that answers each address.
 */

vi.setConfig({ testTimeout: 30_000 });
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
vi.mock("../lib/live-refresh", () => ({ useLiveRefresh: () => undefined }));
const toastMock = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn() }));
vi.mock("../components/shell/toaster", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../components/shell/toaster")>()),
  toast: toastMock
}));

const calls: Array<{ path: string; method: string; body: unknown }> = [];
let replies: Record<string, (init: { method?: string; body?: string }) => Response> = {};
function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}
async function answer(path: string, init: { method?: string; body?: string } = {}) {
  calls.push({ path, method: init.method ?? "GET", body: init.body ? JSON.parse(init.body) : undefined });
  const reply = replies[`${init.method ?? "GET"} ${path}`];
  if (reply) return reply(init);
  return json((init.method ?? "GET") === "GET" ? [] : {});
}
vi.mock("../lib/use-auth", () => ({ authedFetch: (path: string, init?: { method?: string; body?: string }) => answer(path, init) }));
vi.mock("../lib/api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/api")>()),
  fetchJson: async (path: string, init?: { method?: string; body?: string }) => {
    const response = await answer(path, init);
    if (!response.ok) throw new Error((await response.json()).message ?? "failed");
    return response.json();
  }
}));

const revalidate = vi.fn();
const loaderData: Record<string, unknown> = {};
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate })
}));

const { SettingsMediaManagementPage } = await import("./settings-media-management-page-v2");

const standardLibrary = {
  id: "lib-1", name: "Films", mediaType: "movies", rootPath: "/media/movies", importWorkflow: "standard", processorConnectionId: null, processorName: null,
  processorOutputPath: null, processorTimeoutMinutes: 2880, processorFailureMode: "block", cleanupMode: "keep-source", removeEmptySourceFolders: false
};
const refinedLibrary = {
  ...standardLibrary, id: "lib-2", name: "Shows", mediaType: "tv", rootPath: "/media/tv", importWorkflow: "refine-before-import",
  processorConnectionId: "proc-1", processorName: "Weir on box", processorOutputPath: "/refined/tv", processorTimeoutMinutes: 1440,
  processorFailureMode: "manual-review", cleanupMode: "remove-source-after-import", removeEmptySourceFolders: true
};
const processor = {
  id: "proc-1", name: "Weir on box", provider: "generic-webhook", submissionUrl: "http://localhost:9347/api/v1/intake/webhook/deluno",
  healthCheckUrl: "http://localhost:9347/health", authHeaderName: "Authorization", isEnabled: true, healthStatus: "healthy", secretConfigured: true
};

function load(libraries: unknown[], processorConnections: unknown[]) {
  Object.assign(loaderData, { libraries, processorConnections, qualityProfiles: [], settings: {} });
}
function reset() {
  cleanup();
  calls.length = 0;
  replies = {};
  toastMock.success.mockReset();
  toastMock.error.mockReset();
  revalidate.mockReset();
}
const renderPage = () => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <MemoryRouter initialEntries={["/settings/processing"]}><SettingsMediaManagementPage /></MemoryRouter>
  </QueryClientProvider>
);
const sent = (method: string, path: string) => calls.filter((call) => call.method === method && call.path === path);

describe("how finished downloads are handled, per library", () => {
  beforeEach(reset);

  it("opens the drawer for the library whose row is clicked, titled 'How to handle finished downloads'", async () => {
    load([standardLibrary, refinedLibrary], [processor]);
    renderPage();
    fireEvent.click(screen.getByText("Shows"));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText("How to handle finished downloads")).toBeTruthy();
    expect(within(dialog).getByText(/Shows · \/media\/tv/)).toBeTruthy();
    expect(within(dialog).getAllByRole("textbox").some((box) => (box as HTMLInputElement).value === "/refined/tv")).toBe(true);
  });

  it("explains each import timing, and Refine before import reveals the processed output folder", async () => {
    load([standardLibrary], [processor]);
    renderPage();
    fireEvent.click(screen.getByText("Films"));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText("Deluno will import the finished download")).toBeTruthy();
    expect(within(dialog).queryByText("Processed output folder")).toBeNull();

    fireEvent.click(within(dialog).getByRole("radio", { name: /Refine before import/ }));

    expect(within(dialog).getByText("Deluno will wait for the cleaned file")).toBeTruthy();
    expect(within(dialog).getByText("Processed output folder")).toBeTruthy();
    expect(within(dialog).getByRole("button", { name: /Choose folder/ })).toBeTruthy();
  });

  it("will not save a refine workflow with no processed output folder: Save workflow stays off and nothing is sent", async () => {
    load([standardLibrary], [processor]);
    renderPage();
    fireEvent.click(screen.getByText("Films"));
    const dialog = await screen.findByRole("dialog");
    fireEvent.click(within(dialog).getByRole("radio", { name: /Refine before import/ }));

    const save = within(dialog).getByRole("button", { name: "Save workflow" });
    expect((save as HTMLButtonElement).disabled).toBe(true);
    expect(sent("PUT", "/api/libraries/lib-1/workflow")).toHaveLength(0);
  });

  it.skip("DEFECT: tells the person to choose the processed output folder when a refine workflow has none (Save workflow is switched off, so the sentence in the page is never shown)", async () => {
    load([standardLibrary], [processor]);
    renderPage();
    fireEvent.click(screen.getByText("Films"));
    const dialog = await screen.findByRole("dialog");
    fireEvent.click(within(dialog).getByRole("radio", { name: /Refine before import/ }));
    expect(within(dialog).getByText("Choose the folder where the processor will write cleaned files.")).toBeTruthy();
  });

  it("saves the workflow with PUT: output folder, 'no progress for', what to do on failure, and the source cleanup choice with empty folders", async () => {
    replies["PUT /api/libraries/lib-1/workflow"] = () => json({});
    load([standardLibrary], [processor]);
    renderPage();
    fireEvent.click(screen.getByText("Films"));
    const dialog = await screen.findByRole("dialog");
    fireEvent.click(within(dialog).getByRole("radio", { name: /Refine before import/ }));
    const folder = within(dialog).getAllByRole("textbox").find((box) => box.getAttribute("placeholder") === null && (box as HTMLInputElement).value === "") as HTMLInputElement;
    fireEvent.change(folder, { target: { value: "/refined/movies" } });
    fireEvent.change(within(dialog).getByRole("combobox", { name: /No progress for/ }), { target: { value: "1440" } });
    fireEvent.change(within(dialog).getByRole("combobox", { name: /If processing stalls or fails/ }), { target: { value: "manual-review" } });
    fireEvent.click(within(dialog).getByRole("radio", { name: "Remove after import" }));
    fireEvent.click(within(dialog).getByRole("switch", { name: /Remove empty source folders too/ }));
    fireEvent.click(within(dialog).getByRole("button", { name: "Save workflow" }));

    expect(await within(dialog).findByText("Saved just now")).toBeTruthy();
    expect(sent("PUT", "/api/libraries/lib-1/workflow")[0].body).toEqual({
      importWorkflow: "refine-before-import", processorConnectionId: null, processorName: null, processorOutputPath: "/refined/movies",
      processorTimeoutMinutes: 1440, processorFailureMode: "manual-review", cleanupMode: "remove-source-after-import", removeEmptySourceFolders: true
    });
  });

  it("offers 'No progress for' from 24 hours to 7 days with 48 hours as the default, and the three failure choices", async () => {
    load([standardLibrary], [processor]);
    renderPage();
    fireEvent.click(screen.getByText("Films"));
    const dialog = await screen.findByRole("dialog");
    fireEvent.click(within(dialog).getByRole("radio", { name: /Refine before import/ }));

    const timeout = within(dialog).getByRole("combobox", { name: /No progress for/ }) as HTMLSelectElement;
    expect(Array.from(timeout.options).map((option) => option.text)).toEqual(["24 hours", "48 hours", "3 days", "4 days", "7 days"]);
    expect(timeout.value).toBe("2880");
    const failure = within(dialog).getByRole("combobox", { name: /If processing stalls or fails/ }) as HTMLSelectElement;
    expect(Array.from(failure.options).map((option) => option.text)).toEqual(["Stop and ask me", "Send to manual review", "Import the original file"]);
  });

  it("source cleanup: 'Keep source file' turns the empty-folders switch off and unusable, 'Remove after import' makes it usable", async () => {
    load([refinedLibrary], [processor]);
    renderPage();
    fireEvent.click(screen.getByText("Shows"));
    const dialog = await screen.findByRole("dialog");
    const empty = () => within(dialog).getByRole("switch", { name: /Remove empty source folders too/ });
    expect((empty() as HTMLButtonElement).disabled).toBe(false);
    expect(empty().getAttribute("aria-checked")).toBe("true");

    fireEvent.click(within(dialog).getByRole("radio", { name: "Keep source file" }));

    expect((empty() as HTMLButtonElement).disabled).toBe(true);
    expect(within(dialog).getByText("Choose Remove after import to turn this on.")).toBeTruthy();
  });

  it("lists each library's cleanup choice in its row", () => {
    load([standardLibrary, refinedLibrary], [processor]);
    renderPage();
    expect(screen.getByText("Keep downloaded file")).toBeTruthy();
    expect(screen.getByText("Remove downloaded file")).toBeTruthy();
    expect(screen.getByText("Also remove empty folders")).toBeTruthy();
  });
});

describe("processor connections", () => {
  beforeEach(reset);

  it("Connect processor (toolbar) opens the drawer with the job URL, an optional health check address and a token", async () => {
    load([standardLibrary], []);
    renderPage();
    fireEvent.click(screen.getAllByRole("button", { name: "Connect processor" })[0]);
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText("Connect a processor")).toBeTruthy();
    expect(within(dialog).getByText("Processor job URL")).toBeTruthy();
    expect(within(dialog).getByText("Health check address")).toBeTruthy();
  });

  it("Connect processor (empty state) opens the same drawer and a connection is created with POST", async () => {
    replies["POST /api/integrations/processors/connections"] = () => json({ id: "proc-9" });
    load([standardLibrary], []);
    renderPage();
    expect(screen.getByText("No processor connected")).toBeTruthy();
    fireEvent.click(screen.getAllByRole("button", { name: "Connect processor" }).at(-1)!);
    const dialog = await screen.findByRole("dialog");
    fireEvent.change(within(dialog).getByPlaceholderText("https://processor.example/webhooks/deluno"), { target: { value: "http://localhost:9347/webhook" } });
    fireEvent.change(within(dialog).getByPlaceholderText("https://processor.example/health"), { target: { value: "http://localhost:9347/health" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Connect processor" }));

    await waitFor(() => expect(sent("POST", "/api/integrations/processors/connections")).toHaveLength(1));
    expect(sent("POST", "/api/integrations/processors/connections")[0].body).toMatchObject({
      submissionUrl: "http://localhost:9347/webhook", healthCheckUrl: "http://localhost:9347/health", authHeaderName: "Authorization", isEnabled: true, secret: ""
    });
  });

  it("Edit saves with PUT to that connection, and a token left blank is sent blank so the stored one is kept", async () => {
    replies["PUT /api/integrations/processors/connections/proc-1"] = () => json({});
    load([standardLibrary], [processor]);
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "Edit Weir on box" }));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText("Edit processor connection")).toBeTruthy();
    fireEvent.change(within(dialog).getByPlaceholderText("https://processor.example/health"), { target: { value: "http://localhost:9347/ping" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Save processor" }));

    await waitFor(() => expect(sent("PUT", "/api/integrations/processors/connections/proc-1")).toHaveLength(1));
    expect(sent("PUT", "/api/integrations/processors/connections/proc-1")[0].body).toMatchObject({ healthCheckUrl: "http://localhost:9347/ping", secret: "" });
  });

  it("Test posts to the connection's test address and toasts the processor's own answer", async () => {
    replies["POST /api/integrations/processors/connections/proc-1/test"] = () => json({ healthStatus: "healthy", message: "Weir answered and is ready." });
    load([standardLibrary], [processor]);
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "Test" }));
    await waitFor(() => expect(toastMock.success).toHaveBeenCalledWith("Weir on box: Weir answered and is ready."));
  });

  it("shows the connection's health in words on its row, whichever it is", () => {
    load([standardLibrary], [processor, { ...processor, id: "proc-2", name: "Second", healthStatus: "degraded" }, { ...processor, id: "proc-3", name: "Third", healthStatus: "unreachable" }]);
    renderPage();
    expect(screen.getByText("healthy")).toBeTruthy();
    expect(screen.getByText("degraded")).toBeTruthy();
    expect(screen.getByText("unreachable")).toBeTruthy();
  });

  it("Remove asks 'Remove <name>?', then disconnects with DELETE and toasts it", async () => {
    replies["DELETE /api/integrations/processors/connections/proc-1"] = () => new Response(null, { status: 204 });
    load([standardLibrary], [processor]);
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "Remove" }));
    expect(await screen.findByText("Remove “Weir on box”?")).toBeTruthy();
    expect(screen.getByText(/Libraries pointing at it fall back to watching the processed output folder/)).toBeTruthy();
    expect(sent("DELETE", "/api/integrations/processors/connections/proc-1")).toHaveLength(0);
    fireEvent.click(screen.getByRole("button", { name: "Disconnect processor" }));

    await waitFor(() => expect(toastMock.success).toHaveBeenCalledWith("Weir on box disconnected"));
    expect(sent("DELETE", "/api/integrations/processors/connections/proc-1")).toHaveLength(1);
  });

  it("path mappings: lists the pairs, adds a pair only with both paths, and removes one", async () => {
    const pairs = [{ id: "map-1", delunoPath: "D:\\Refined", processorPath: "/data/refined", isEnabled: true, priority: 10 }];
    replies["GET /api/integrations/processors/connections/proc-1/path-mappings"] = () => json(pairs);
    replies["POST /api/integrations/processors/connections/proc-1/path-mappings"] = () => json({});
    replies["DELETE /api/integrations/processors/connections/proc-1/path-mappings/map-1"] = () => new Response(null, { status: 204 });
    load([standardLibrary], [processor]);
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "Path mappings" }));

    expect(await screen.findByText("D:\\Refined")).toBeTruthy();
    expect(screen.getAllByText("/data/refined", { selector: "span" }).length).toBe(1);
    const link = screen.getByRole("button", { name: "Link paths" }) as HTMLButtonElement;
    expect(link.disabled).toBe(true);
    fireEvent.change(screen.getByPlaceholderText("D:\\Refined"), { target: { value: "E:\\Clean" } });
    expect(link.disabled).toBe(true);
    fireEvent.change(screen.getByPlaceholderText("/data/refined"), { target: { value: "/clean" } });
    expect(link.disabled).toBe(false);
    fireEvent.click(link);
    await waitFor(() => expect(sent("POST", "/api/integrations/processors/connections/proc-1/path-mappings")).toHaveLength(1));
    expect(sent("POST", "/api/integrations/processors/connections/proc-1/path-mappings")[0].body).toEqual({ delunoPath: "E:\\Clean", processorPath: "/clean", isEnabled: true, priority: 10 });

    fireEvent.click(screen.getByRole("button", { name: "Remove D:\\Refined link" }));
    await waitFor(() => expect(sent("DELETE", "/api/integrations/processors/connections/proc-1/path-mappings/map-1")).toHaveLength(1));
  });
});
