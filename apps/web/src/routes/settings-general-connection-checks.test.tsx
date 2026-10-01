import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { LibraryItem, QualityProfileItem } from "../lib/api";
import { emptyPlatformSettingsSnapshot } from "../lib/api/types/settings";
import { authedFetch } from "../lib/use-auth";

/**
 * #1098: Check connections regularly is on for every install (it only reads) and Keep this computer awake is
 * off until switched on; both save the moment they are switched, in their own Settings section.
 */

vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));

const loaderData = { libraries: [] as LibraryItem[], qualityProfiles: [] as QualityProfileItem[], settings: emptyPlatformSettingsSnapshot };

vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate: vi.fn() })
}));

const { SettingsGeneralPage } = await import("./settings-general-page");

function renderPage() {
  vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => {
    const url = typeof input === "string" ? input : input.toString();
    if (url.includes("/api/system/log-level")) return new Response(JSON.stringify({ level: "Information" }), { status: 200 });
    if (url.includes("/api/system/lan-access")) return new Response(JSON.stringify({ state: "notChecked", port: 5099, ruleName: null, message: "", canAllow: false }), { status: 200 });
    return new Response(JSON.stringify({}), { status: 200 });
  });
  return render(<MemoryRouter><SettingsGeneralPage /></MemoryRouter>);
}

describe("connection checks (#1098)", () => {
  afterEach(() => vi.clearAllMocks());

  it("starts with regular checks on and Keep awake off, as a new or upgraded install reads", async () => {
    renderPage();

    expect(await screen.findByRole("switch", { name: "Check connections regularly" })).toBeChecked();
    expect(screen.getByRole("switch", { name: "Keep this computer awake" })).not.toBeChecked();
  });

  it("saves each switch as soon as it is flipped, touching only that setting", async () => {
    renderPage();
    const user = userEvent.setup();

    await user.click(await screen.findByRole("switch", { name: "Check connections regularly" }));
    await waitFor(() => expect(vi.mocked(authedFetch).mock.calls.some(([path, init]) => path === "/api/settings" && (init as RequestInit).method === "PATCH")).toBe(true));
    const first = vi.mocked(authedFetch).mock.calls.find(([path]) => path === "/api/settings")!;
    expect(JSON.parse(String((first[1] as RequestInit).body))).toEqual({ checkConnectionsRegularly: false });
    await waitFor(() => expect(screen.getByRole("switch", { name: "Check connections regularly" })).not.toBeChecked());

    vi.mocked(authedFetch).mockClear();
    await user.click(screen.getByRole("switch", { name: "Keep this computer awake" }));
    await waitFor(() => expect(vi.mocked(authedFetch).mock.calls.some(([path]) => path === "/api/settings")).toBe(true));
    const second = vi.mocked(authedFetch).mock.calls.find(([path]) => path === "/api/settings")!;
    expect(JSON.parse(String((second[1] as RequestInit).body))).toEqual({ keepComputerAwake: true });
  });
});
