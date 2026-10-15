import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api/types/settings";
import { authedFetch } from "../lib/use-auth";

/**
 * Settings > General, the Cloudflare helper card: the address (trimmed, blank clears), its timeout (blank means 60),
 * its kind, Test helper with the address as typed, and Save general settings with its footer states.
 */

vi.setConfig({ testTimeout: 30_000 });
vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));

const loaderData = { libraries: [], qualityProfiles: [], settings: { ...emptyPlatformSettingsSnapshot } as Record<string, unknown> };
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate: vi.fn() })
}));

const { SettingsGeneralPage } = await import("./settings-general-page");
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });

function renderPage() {
  return render(<MemoryRouter><SettingsGeneralPage /></MemoryRouter>);
}
const patches = () => vi.mocked(authedFetch).mock.calls.filter(([url, init]) => url === "/api/settings" && (init as RequestInit | undefined)?.method === "PATCH");
const lastPatch = () => JSON.parse(String((patches().at(-1)![1] as RequestInit).body));

beforeEach(() => {
  cleanup();
  loaderData.settings = { ...emptyPlatformSettingsSnapshot, cloudflareHelperUrl: "", cloudflareHelperTimeoutSeconds: 60, cloudflareHelperKind: "auto" };
  vi.mocked(authedFetch).mockReset();
  vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => {
    const url = String(input);
    if (url.includes("/api/system/log-level")) return json({ level: "Information" });
    if (url.includes("/api/system/lan-access")) return json({ state: "notChecked", port: 5099, ruleName: null, message: "", canAllow: false });
    return json({});
  });
});

describe("Cloudflare helper", () => {
  it("saves the address trimmed, a blank timeout as 60 seconds and the chosen kind, in one PATCH", async () => {
    renderPage();
    fireEvent.change(await screen.findByPlaceholderText("http://127.0.0.1:8191"), { target: { value: "  http://127.0.0.1:8191  " } });
    fireEvent.change(screen.getByPlaceholderText("60"), { target: { value: "" } });
    fireEvent.change(screen.getByDisplayValue(/Auto-detect/), { target: { value: "byparr" } });
    fireEvent.click(screen.getByRole("button", { name: /Save general settings/ }));
    await waitFor(() => expect(patches()).toHaveLength(1));
    expect(lastPatch()).toEqual({ cloudflareHelperUrl: "http://127.0.0.1:8191", cloudflareHelperTimeoutSeconds: 60, cloudflareHelperKind: "byparr" });
    await waitFor(() => expect(screen.getByText("Saved just now")).toBeInTheDocument());
  });

  it("clears the helper when the address is blanked and saved", async () => {
    loaderData.settings = { ...emptyPlatformSettingsSnapshot, cloudflareHelperUrl: "http://127.0.0.1:8191", cloudflareHelperTimeoutSeconds: 90, cloudflareHelperKind: "flaresolverr" };
    renderPage();
    fireEvent.change(await screen.findByPlaceholderText("http://127.0.0.1:8191"), { target: { value: "" } });
    fireEvent.click(screen.getByRole("button", { name: /Save general settings/ }));
    await waitFor(() => expect(patches()).toHaveLength(1));
    expect(lastPatch()).toEqual({ cloudflareHelperUrl: "", cloudflareHelperTimeoutSeconds: 90, cloudflareHelperKind: "flaresolverr" });
  });

  it("shows the server's message under the address when it refuses it, and the footer says it could not save", async () => {
    vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === "/api/settings") return json({ errors: { cloudflareHelperUrl: ["Use an http or https address."] } }, 400);
      if (url.includes("/api/system/log-level")) return json({ level: "Information" });
      if (url.includes("/api/system/lan-access")) return json({ state: "notChecked", port: 5099, ruleName: null, message: "", canAllow: false });
      return json({});
    });
    renderPage();
    fireEvent.change(await screen.findByPlaceholderText("http://127.0.0.1:8191"), { target: { value: "ftp://nope" } });
    fireEvent.click(screen.getByRole("button", { name: /Save general settings/ }));
    expect((await screen.findAllByText("Use an http or https address.")).length).toBeGreaterThan(0);
    expect(screen.queryByText("Saved just now")).toBeNull();
  });

  it("keeps Test helper dimmed until an address is typed, then posts the address as typed (unsaved) and shows the answer", async () => {
    vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === "/api/trackers/cloudflare-helper/test") return json({ healthy: true, message: "FlareSolverr answered." });
      if (url.includes("/api/system/log-level")) return json({ level: "Information" });
      if (url.includes("/api/system/lan-access")) return json({ state: "notChecked", port: 5099, ruleName: null, message: "", canAllow: false });
      return json({});
    });
    renderPage();
    const test = await screen.findByRole("button", { name: /Test helper/ });
    expect(test).toBeDisabled();
    fireEvent.change(screen.getByPlaceholderText("http://127.0.0.1:8191"), { target: { value: " http://helper.example.test:8191 " } });
    expect(test).toBeEnabled();
    fireEvent.click(test);
    expect(await screen.findByText("FlareSolverr answered.")).toHaveClass("text-success");
    const call = vi.mocked(authedFetch).mock.calls.find(([url]) => url === "/api/trackers/cloudflare-helper/test")!;
    expect(JSON.parse(String((call[1] as RequestInit).body))).toEqual({ url: "http://helper.example.test:8191" });
    expect(patches()).toHaveLength(0);
  });

  it("shows a red message when the helper cannot be reached", async () => {
    vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === "/api/trackers/cloudflare-helper/test") return json({ healthy: false, message: "Nothing answered at that address." });
      if (url.includes("/api/system/log-level")) return json({ level: "Information" });
      if (url.includes("/api/system/lan-access")) return json({ state: "notChecked", port: 5099, ruleName: null, message: "", canAllow: false });
      return json({});
    });
    renderPage();
    fireEvent.change(await screen.findByPlaceholderText("http://127.0.0.1:8191"), { target: { value: "http://127.0.0.1:1" } });
    fireEvent.click(screen.getByRole("button", { name: /Test helper/ }));
    expect(await screen.findByText("Nothing answered at that address.")).toHaveClass("text-destructive");
  });
});

describe("Save general settings", () => {
  it("is dimmed until something changes, says Unsaved changes while edited and goes back when the edit is undone", async () => {
    renderPage();
    const save = await screen.findByRole("button", { name: /Save general settings/ });
    expect(save).toBeDisabled();
    fireEvent.change(screen.getByPlaceholderText("http://127.0.0.1:8191"), { target: { value: "http://127.0.0.1:8191" } });
    expect(save).toBeEnabled();
    expect(screen.getByText("Unsaved changes")).toBeInTheDocument();
    fireEvent.change(screen.getByPlaceholderText("http://127.0.0.1:8191"), { target: { value: "" } });
    expect(save).toBeDisabled();
  });
});
