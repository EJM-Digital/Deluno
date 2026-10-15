import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

/**
 * System > API (#1294): the key-generating form and the list of keys, on the data the route hands the page.
 * Creating goes through the shared JSON helper and revoking through the signed-in fetch; each is mocked so a
 * test shows the request that was made and the words that came back.
 */

const fetchJsonMock = vi.hoisted(() => vi.fn());
vi.mock("../lib/api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/api")>()),
  fetchJson: fetchJsonMock
}));

const authedFetchMock = vi.hoisted(() => vi.fn());
vi.mock("../lib/use-auth", () => ({ authedFetch: authedFetchMock }));

const toastMock = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn() }));
vi.mock("../components/shell/toaster", () => ({ toast: toastMock, Toaster: () => null }));

const revalidate = vi.hoisted(() => vi.fn());
const loader = vi.hoisted(() => ({ data: { apiKeys: [] as unknown[], scopeTemplates: [] as unknown[] } }));
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loader.data,
  useRevalidator: () => ({ revalidate })
}));

const { SystemApiPage } = await import("./system-api-page");

const templates = [
  { id: "automation", name: "Automation", description: "Read everything and queue downloads.", scopes: ["read", "queue"], capabilities: [] },
  { id: "read-only", name: "Read only", description: "Look, never change.", scopes: ["read"], capabilities: [] }
];
const key = (n: number, over: Record<string, unknown> = {}) => ({
  id: `k${n}`, name: `Key ${n}`, prefix: `deluno_ab${n}`, scopes: "read,queue", lastUsedUtc: null, createdUtc: "2026-10-01T10:00:00Z", updatedUtc: "2026-10-01T10:00:00Z", ...over
});

function renderPage() {
  return render(
    <MemoryRouter>
      <SystemApiPage />
    </MemoryRouter>
  );
}

afterEach(() => {
  fetchJsonMock.mockReset();
  authedFetchMock.mockReset();
  toastMock.success.mockReset();
  toastMock.error.mockReset();
  revalidate.mockReset();
  loader.data = { apiKeys: [], scopeTemplates: templates };
});

describe("Generate an API key", () => {
  it("Key name starts as External automation, is required, and the button is off while the name is blank", () => {
    loader.data = { apiKeys: [], scopeTemplates: templates };
    renderPage();

    const name = screen.getByLabelText("Key name");
    expect(name).toHaveValue("External automation");
    expect(name).toBeRequired();
    const generate = screen.getByRole("button", { name: "Generate API key" });
    expect(generate).not.toBeDisabled();

    fireEvent.change(name, { target: { value: "   " } });
    expect(generate).toBeDisabled();
    fireEvent.change(name, { target: { value: "Home Assistant" } });
    expect(generate).not.toBeDisabled();
  });

  it("Access offers the server's presets, starts on Automation with its description, and Custom scopes takes a comma list", () => {
    loader.data = { apiKeys: [], scopeTemplates: templates };
    renderPage();

    const access = screen.getByLabelText("Access");
    expect(access).toHaveValue("automation");
    expect(within(access).getAllByRole("option").map((option) => option.textContent)).toEqual(["Automation", "Read only", "Custom scopes"]);
    expect(screen.getByText("Read everything and queue downloads.")).toBeInTheDocument();

    fireEvent.change(access, { target: { value: "read-only" } });
    expect(screen.getByText("Look, never change.")).toBeInTheDocument();

    fireEvent.change(access, { target: { value: "__custom" } });
    expect(screen.getByPlaceholderText("read, queue, imports")).toBeInTheDocument();
    expect(screen.getByText(/Leaving it empty is refused rather than granting everything/)).toBeInTheDocument();
  });

  it("generating posts the name and the chosen access, shows the one-time key, and reads the list again", async () => {
    loader.data = { apiKeys: [], scopeTemplates: templates };
    fetchJsonMock.mockResolvedValueOnce({ apiKey: "deluno_secret_value", item: key(1, { name: "Home Assistant" }) });
    renderPage();

    fireEvent.change(screen.getByLabelText("Key name"), { target: { value: "Home Assistant" } });
    fireEvent.click(screen.getByRole("button", { name: "Generate API key" }));

    await waitFor(() =>
      expect(fetchJsonMock).toHaveBeenCalledWith("/api/api-keys", expect.objectContaining({ method: "POST", body: JSON.stringify({ name: "Home Assistant", scopes: "automation" }) }))
    );
    expect(await screen.findByText("deluno_secret_value")).toBeInTheDocument();
    expect(screen.getByText("one-time secret")).toBeInTheDocument();
    expect(toastMock.success).toHaveBeenCalledWith("API key generated");
    expect(revalidate).toHaveBeenCalled();
  });

  it("a key the server refuses shows its words and no key", async () => {
    loader.data = { apiKeys: [], scopeTemplates: templates };
    fetchJsonMock.mockRejectedValueOnce(new Error("Give the key at least one scope."));
    renderPage();

    fireEvent.click(screen.getByRole("button", { name: "Generate API key" }));

    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith("Give the key at least one scope."));
    expect(screen.queryByText("one-time secret")).toBeNull();
  });

  it("explains how integrations authenticate and lists the integration endpoints with their method and scope", () => {
    renderPage();

    expect(screen.getByText("How integrations authenticate")).toBeInTheDocument();
    expect(screen.getAllByText("X-Api-Key").length).toBeGreaterThan(0);
    expect(screen.getByText("deluno_")).toBeInTheDocument();
    expect(screen.getByText("Integration endpoints")).toBeInTheDocument();
    for (const path of ["/api/openapi/v1.json", "/api/integrations/external/manifest", "/api/integrations/external/queue", "/api/monitoring/export/prometheus", "/api/integrations/processors/events"]) {
      expect(screen.getByText(path)).toBeInTheDocument();
    }
    expect(screen.getAllByText(/^scope: /).length).toBeGreaterThanOrEqual(20);
    expect(screen.getByText(/curl -H "X-Api-Key: deluno_\.\.\." /)).toBeInTheDocument();
  });
});

describe("Active keys", () => {
  it("says there are no keys yet when there are none", () => {
    loader.data = { apiKeys: [], scopeTemplates: templates };
    renderPage();
    expect(screen.getByText("No API keys yet")).toBeInTheDocument();
  });

  it("lists each key's name, prefix, scopes, created time and last used, Never and unused when it has not been used", () => {
    loader.data = {
      apiKeys: [key(1, { lastUsedUtc: "2026-10-12T10:00:00Z" }), key(2, { scopes: "read" })],
      scopeTemplates: templates
    };
    renderPage();

    expect(screen.getByText("2 keys")).toBeInTheDocument();
    const rows = screen.getAllByRole("row").filter((row) => within(row).queryByRole("button", { name: "Revoke" }));
    expect(rows).toHaveLength(2);
    expect(rows[0]!.textContent).toContain("Key 1");
    expect(rows[0]!.textContent).toContain("deluno_ab1");
    expect(rows[0]!.textContent).toContain("read,queue");
    expect(rows[0]!.textContent).not.toContain("Never");
    expect(rows[1]!.textContent).toContain("Key 2");
    expect(rows[1]!.textContent).toContain("Never");
    expect(rows[1]!.textContent).toContain("unused");
  });
});

describe("Revoke", () => {
  async function askToRevoke() {
    loader.data = { apiKeys: [key(1, { name: "Dashboard" })], scopeTemplates: templates };
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "Revoke" }));
    return screen.findByRole("dialog");
  }

  it("asks first, names the key, and Cancel keeps it", async () => {
    const dialog = await askToRevoke();
    expect(within(dialog).getByText('Revoke "Dashboard"?')).toBeInTheDocument();
    expect(within(dialog).getByText(/Anything currently using this key will stop authenticating immediately/)).toBeInTheDocument();

    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(authedFetchMock).not.toHaveBeenCalled();
    expect(screen.getByText("Dashboard")).toBeInTheDocument();
  });

  it("confirming deletes the key, says it was revoked and reads the list again", async () => {
    authedFetchMock.mockResolvedValueOnce({ ok: true, status: 204 });
    const dialog = await askToRevoke();

    fireEvent.click(within(dialog).getByRole("button", { name: "Revoke key" }));

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/api-keys/k1", { method: "DELETE" }));
    await waitFor(() => expect(toastMock.success).toHaveBeenCalledWith("API key revoked"));
    expect(revalidate).toHaveBeenCalled();
  });

  it("a revoke the server refuses says so and does not claim success", async () => {
    authedFetchMock.mockResolvedValueOnce({ ok: false, status: 500 });
    const dialog = await askToRevoke();

    fireEvent.click(within(dialog).getByRole("button", { name: "Revoke key" }));

    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith("API key could not be revoked."));
    expect(toastMock.success).not.toHaveBeenCalled();
    expect(revalidate).not.toHaveBeenCalled();
  });

  it("revoking the key that was just created hides the one-time key panel", async () => {
    loader.data = { apiKeys: [], scopeTemplates: templates };
    fetchJsonMock.mockResolvedValueOnce({ apiKey: "deluno_fresh", item: key(7, { name: "Fresh" }) });
    authedFetchMock.mockResolvedValueOnce({ ok: true, status: 204 });
    const view = renderPage();
    fireEvent.click(screen.getByRole("button", { name: "Generate API key" }));
    expect(await screen.findByText("deluno_fresh")).toBeInTheDocument();

    // The route now hands the page the list with the new key in it.
    loader.data = { apiKeys: [key(7, { name: "Fresh" })], scopeTemplates: templates };
    view.rerender(
      <MemoryRouter>
        <SystemApiPage />
      </MemoryRouter>
    );
    fireEvent.click(await screen.findByRole("button", { name: "Revoke" }));
    fireEvent.click(await within(await screen.findByRole("dialog")).findByRole("button", { name: "Revoke key" }));

    await waitFor(() => expect(screen.queryByText("deluno_fresh")).toBeNull());
  });
});
