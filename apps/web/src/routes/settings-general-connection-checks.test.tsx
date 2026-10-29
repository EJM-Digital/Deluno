import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { LibraryItem, QualityProfileItem } from "../lib/api";
import { emptyPlatformSettingsSnapshot } from "../lib/api/types/settings";
import { authedFetch } from "../lib/use-auth";

/** #1432: no Connection checks section; #1098: Keep this computer awake is off until switched on and saves at once. */

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

describe("keep awake and the removed connection checks (#1098, #1432)", () => {
  afterEach(() => vi.clearAllMocks());

  it("has no Connection checks section or switch: health checks always run", async () => {
    renderPage();
    await screen.findByRole("switch", { name: "Keep this computer awake" });

    expect(screen.queryByText(/connection checks/i)).toBeNull();
    expect(screen.queryByRole("switch", { name: /check connections regularly/i })).toBeNull();
  });

  it("starts with Keep awake off and saves it as soon as it is flipped, touching only that setting", async () => {
    renderPage();
    const user = userEvent.setup();

    const awake = await screen.findByRole("switch", { name: "Keep this computer awake" });
    expect(awake).not.toBeChecked();
    await user.click(awake);
    await waitFor(() => expect(vi.mocked(authedFetch).mock.calls.some(([path]) => path === "/api/settings")).toBe(true));
    const call = vi.mocked(authedFetch).mock.calls.find(([path]) => path === "/api/settings")!;
    expect(JSON.parse(String((call[1] as RequestInit).body))).toEqual({ keepComputerAwake: true });
  });
});
