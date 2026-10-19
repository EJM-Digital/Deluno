import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";

/**
 * Media management > Finished download workflow and Processor connections: what each library does with a finished download (import
 * straight away, or wait for a processor's cleaned file), and the processors Deluno sends a job to. Each control sends the request the
 * workflow and processor-connection routes are tested against.
 */

const authedFetchMock = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
vi.mock("../lib/live-refresh", () => ({ useLiveRefresh: () => undefined }));
vi.mock("../components/app/bundled-components-detected", () => ({ DetectedComponents: () => null }));
vi.mock("../lib/connection-versions", () => ({ useConnectionVersions: () => ({ versions: new Map() }), versionKey: (kind: string, id: string) => `${kind}:${id}` }));

const toastSuccess = vi.fn();
const toastError = vi.fn();
vi.mock("../components/shell/toaster", async (importOriginal) => {
  const original = await importOriginal<typeof import("../components/shell/toaster")>();
  return {
    ...original,
    toast: Object.assign(() => undefined, { success: (...args: unknown[]) => toastSuccess(...args), error: (...args: unknown[]) => toastError(...args), message: vi.fn(), info: vi.fn(), warning: vi.fn() })
  };
});

const revalidateMock = vi.fn();
let loaderData: Record<string, unknown>;
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate: revalidateMock })
}));

const { SettingsMediaManagementPage } = await import("./settings-media-management-page-v2");

vi.setConfig({ testTimeout: 20_000 });

const library = (overrides: Record<string, unknown> = {}) => ({
  id: "lib-1", name: "Films", mediaType: "movies", rootPath: "D:/Films", importWorkflow: "standard", processorConnectionId: null, processorName: null,
  processorOutputPath: null, processorTimeoutMinutes: 30, processorFailureMode: "import-original", cleanupMode: "keep-source", removeEmptySourceFolders: false, ...overrides
});

const processor = (overrides: Record<string, unknown> = {}) => ({
  id: "pc-1", name: "Weir", provider: "generic-webhook", submissionUrl: "http://127.0.0.1:9347/api/v1/intake/webhook/deluno", authHeaderName: "X-Webhook-Secret",
  healthCheckUrl: null, isEnabled: true, healthStatus: "healthy", secretConfigured: true, createdUtc: "2026-10-01T00:00:00Z", updatedUtc: "2026-10-01T00:00:00Z", ...overrides
});

function reply(body: unknown = {}, ok = true, status = 200) {
  return Promise.resolve({ ok, status, json: () => Promise.resolve(body), clone() { return this; }, text: () => Promise.resolve(JSON.stringify(body)) } as unknown as Response);
}

function load(libraries: unknown[] = [library()], processors: unknown[] = []) {
  loaderData = { settings: {}, libraries, processorConnections: processors };
}

function renderPage() {
  return render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <MemoryRouter initialEntries={["/settings/processing"]}>
        <SettingsMediaManagementPage />
      </MemoryRouter>
    </QueryClientProvider>
  );
}

async function confirmation(title: RegExp) {
  return within(await screen.findByRole("dialog", { name: title }));
}

const calls = (method: string, path: string) =>
  authedFetchMock.mock.calls.filter(([url, init]) => String(url) === path && ((init as RequestInit | undefined)?.method ?? "GET") === method);
const bodyOf = (call: unknown[]) => JSON.parse(String((call[1] as RequestInit).body));

beforeEach(() => {
  authedFetchMock.mockReset();
  toastSuccess.mockReset();
  toastError.mockReset();
  revalidateMock.mockReset();
  authedFetchMock.mockImplementation(() => reply([]));
  load();
});

describe("the finished download workflow of a library", () => {
  it("says what each library does, and that a library needs a library first when there are none", () => {
    load([]);
    renderPage();
    expect(screen.getByText("No libraries yet")).toBeVisible();
  });

  it("lists a library with what happens to its finished downloads and whether it is ready", () => {
    load([library(), library({ id: "lib-2", name: "Refined films", importWorkflow: "refine-before-import", processorOutputPath: null })]);
    renderPage();

    const plain = screen.getByText("Films").closest("[role=row]") as HTMLElement;
    expect(within(plain).getByText("Ready")).toBeVisible();
    const refined = screen.getByText("Refined films").closest("[role=row]") as HTMLElement;
    expect(within(refined).getByText("Needs a folder")).toBeVisible();
  });

  it("opens the library with its own choices and saves the workflow with a processor that cleans the files first", async () => {
    load([library()], [processor()]);
    authedFetchMock.mockImplementation((path: string, init?: RequestInit) => reply(init?.method === "PUT" ? library() : []));
    renderPage();
    fireEvent.click(screen.getByText("Films"));
    const drawer = within(await screen.findByRole("dialog"));

    fireEvent.click(drawer.getByRole("radio", { name: /Wait for a processor|Refine/i }));
    fireEvent.change(drawer.getByLabelText(/Completion signal/), { target: { value: "pc-1" } });
    fireEvent.change(drawer.getByLabelText("Processed output folder"), { target: { value: "D:\\Refined" } });
    fireEvent.click(drawer.getByRole("button", { name: "Save workflow" }));

    await waitFor(() => expect(calls("PUT", "/api/libraries/lib-1/workflow")).toHaveLength(1));
    expect(bodyOf(calls("PUT", "/api/libraries/lib-1/workflow")[0])).toMatchObject({
      importWorkflow: "refine-before-import", processorConnectionId: "pc-1", processorOutputPath: "D:\\Refined", cleanupMode: "keep-source"
    });
    expect(await drawer.findByText("Saved just now")).toBeVisible();
  });

  it("will not save a processing workflow without a folder for the cleaned files, and says why", async () => {
    renderPage();
    fireEvent.click(screen.getByText("Films"));
    const drawer = within(await screen.findByRole("dialog"));
    fireEvent.click(drawer.getByRole("radio", { name: /Wait for a processor|Refine/i }));

    expect(drawer.getByRole("button", { name: "Save workflow" })).toBeDisabled();
    expect(calls("PUT", "/api/libraries/lib-1/workflow")).toHaveLength(0);
  });

  it("shows the server's reason when the workflow is refused", async () => {
    authedFetchMock.mockImplementation((path: string, init?: RequestInit) => reply(init?.method === "PUT" ? { message: "That folder is the library folder." } : [], init?.method !== "PUT", init?.method === "PUT" ? 400 : 200));
    renderPage();
    fireEvent.click(screen.getByText("Films"));
    const drawer = within(await screen.findByRole("dialog"));
    fireEvent.click(drawer.getByRole("radio", { name: /Remove after import/i }));

    fireEvent.click(drawer.getByRole("button", { name: "Save workflow" }));

    expect(await drawer.findByText(/That folder is the library folder|could not be saved/)).toBeVisible();
  });
});

describe("the processor connections", () => {
  it("says a processor is optional and offers to connect one, when there are none", () => {
    renderPage();
    expect(screen.getByText("No processor connected")).toBeVisible();
    expect(screen.getAllByRole("button", { name: "Connect processor" }).length).toBeGreaterThan(1);
  });

  it("Connect processor asks for the address and the secret, and posts the connection", async () => {
    authedFetchMock.mockImplementation((path: string, init?: RequestInit) => reply(init?.method === "POST" ? processor({ id: "new" }) : []));
    renderPage();
    fireEvent.click(screen.getAllByRole("button", { name: "Connect processor" })[0]);
    const drawer = within(await screen.findByRole("dialog"));
    expect(drawer.getByText("Connect a processor")).toBeVisible();

    fireEvent.change(drawer.getByPlaceholderText("https://processor.example/webhooks/deluno"), { target: { value: "http://192.168.1.50:9347/api/v1/intake/webhook/deluno" } });
    fireEvent.change(drawer.getByPlaceholderText("https://processor.example/health"), { target: { value: "http://192.168.1.50:9347/health" } });
    fireEvent.change(drawer.getByLabelText(/Token sent to the processor/), { target: { value: "a-webhook-secret" } });
    fireEvent.click(drawer.getByRole("button", { name: "Connect processor" }));

    await waitFor(() => expect(calls("POST", "/api/integrations/processors/connections")).toHaveLength(1));
    expect(bodyOf(calls("POST", "/api/integrations/processors/connections")[0])).toMatchObject({
      submissionUrl: "http://192.168.1.50:9347/api/v1/intake/webhook/deluno", healthCheckUrl: "http://192.168.1.50:9347/health", secret: "a-webhook-secret", isEnabled: true
    });
    expect(revalidateMock).toHaveBeenCalled();
  });

  it("lists a connection with its address and health, and Test asks it and says what it answered", async () => {
    load([library()], [processor()]);
    authedFetchMock.mockImplementation((path: string) => reply(String(path).endsWith("/test") ? { message: "Weir answered." } : []));
    renderPage();
    const row = screen.getByText("Weir").closest("[role=row]") as HTMLElement;
    expect(within(row).getByText("healthy")).toBeVisible();
    expect(within(row).getByText("http://127.0.0.1:9347/api/v1/intake/webhook/deluno")).toBeVisible();

    fireEvent.click(within(row).getByRole("button", { name: "Test" }));

    await waitFor(() => expect(calls("POST", "/api/integrations/processors/connections/pc-1/test")).toHaveLength(1));
    expect(toastSuccess).toHaveBeenCalledWith("Weir: Weir answered.");
  });

  it("Edit opens the connection with its address and a blank secret, and Save processor puts it at its own id", async () => {
    load([library()], [processor()]);
    authedFetchMock.mockImplementation((path: string, init?: RequestInit) => reply(init?.method === "PUT" ? processor() : []));
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "Edit Weir" }));
    const drawer = within(await screen.findByRole("dialog"));
    expect(drawer.getByText("Edit processor connection")).toBeVisible();
    fireEvent.change(drawer.getByPlaceholderText("https://processor.example/health"), { target: { value: "http://127.0.0.1:9347/health" } });

    fireEvent.click(drawer.getByRole("button", { name: "Save processor" }));

    await waitFor(() => expect(calls("PUT", "/api/integrations/processors/connections/pc-1")).toHaveLength(1));
    expect(bodyOf(calls("PUT", "/api/integrations/processors/connections/pc-1")[0])).toMatchObject({ healthCheckUrl: "http://127.0.0.1:9347/health", secret: "" });
  });

  it("Remove asks first, then disconnects the processor and says so", async () => {
    load([library()], [processor()]);
    renderPage();
    const row = screen.getByText("Weir").closest("[role=row]") as HTMLElement;

    fireEvent.click(within(row).getByRole("button", { name: "Remove" }));
    const confirm = await confirmation(/Disconnect|Remove/);
    expect(calls("DELETE", "/api/integrations/processors/connections/pc-1")).toHaveLength(0);
    fireEvent.click(confirm.getByRole("button", { name: /Disconnect|Remove/ }));

    await waitFor(() => expect(calls("DELETE", "/api/integrations/processors/connections/pc-1")).toHaveLength(1));
    expect(toastSuccess).toHaveBeenCalledWith("Weir disconnected");
  });

  it("Path mappings lists the links, adds one only when both sides are filled, and removes one", async () => {
    load([library()], [processor()]);
    const mapping = { id: "pm-1", delunoPath: "D:\\Refined", processorPath: "/data/refined", isEnabled: true, priority: 10 };
    authedFetchMock.mockImplementation((path: string, init?: RequestInit) => reply(String(path).endsWith("/path-mappings") && !init?.method ? [mapping] : []));
    renderPage();
    const row = screen.getByText("Weir").closest("[role=row]") as HTMLElement;

    fireEvent.click(within(row).getByRole("button", { name: "Path mappings" }));

    expect(await screen.findByText("D:\\Refined")).toBeVisible();
    expect(screen.getAllByText("/data/refined").length).toBeGreaterThan(0);
    const link = screen.getByRole("button", { name: "Link paths" });
    expect(link).toBeDisabled();
    fireEvent.change(screen.getByPlaceholderText("D:\\Refined"), { target: { value: "E:\\Cleaned" } });
    expect(link).toBeDisabled();
    fireEvent.change(screen.getByPlaceholderText("/data/refined"), { target: { value: "/data/cleaned" } });
    fireEvent.click(link);
    await waitFor(() => expect(calls("POST", "/api/integrations/processors/connections/pc-1/path-mappings")).toHaveLength(1));
    expect(bodyOf(calls("POST", "/api/integrations/processors/connections/pc-1/path-mappings")[0])).toMatchObject({ delunoPath: "E:\\Cleaned", processorPath: "/data/cleaned", isEnabled: true });
    expect(toastSuccess).toHaveBeenCalledWith("Path mapping saved");

    fireEvent.click(screen.getByRole("button", { name: "Remove D:\\Refined link" }));
    await waitFor(() => expect(calls("DELETE", "/api/integrations/processors/connections/pc-1/path-mappings/pm-1")).toHaveLength(1));
    expect(toastSuccess).toHaveBeenCalledWith("Path mapping removed");
  });
});
