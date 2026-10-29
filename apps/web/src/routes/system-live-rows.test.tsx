import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ConfigAreas } from "../lib/config-areas";
import type { AllowedHostsOverview, LocalNetworkAuthOverview } from "../lib/api";

/**
 * System > API and System > Security follow the server (#1329): a change to the security area made anywhere else
 * (another tab, an automatic pass) arrives as a push, the page reads its data again once, and nothing the person
 * had typed and not yet saved is lost.
 */

const mocks = vi.hoisted(() => ({ many: [] as Array<(name: string, payload: unknown) => void>, loader: vi.fn() }));

vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
vi.mock("../lib/use-signalr", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/use-signalr")>()),
  useSignalREvent: () => undefined,
  useSignalREvents: (events: readonly string[], _subject: unknown, handler: (name: string, payload: unknown) => void) => {
    mocks.many.push((name, payload) => { if (events.includes(name)) handler(name, payload); });
  },
  useLiveEvent: () => undefined
}));

const { SystemApiPage } = await import("./system-api-page");
const { SystemSecurityPage } = await import("./system-security-page");

const fire = (name: string, payload: unknown = null) => act(() => { for (const handler of mocks.many) handler(name, payload); });

const hosts: AllowedHostsOverview = {
  enabled: false, allowedHosts: [], currentHost: "my-pc",
  detectedHosts: [{ host: "localhost", reason: "Always answers from this machine" }, { host: "my-pc", reason: "This computer's name" }]
};
const network: LocalNetworkAuthOverview = {
  enabled: false, additionalLocalNetworks: [], allowList: [], trustedProxies: [],
  currentAddress: "192.168.1.99", currentAddressIsLocal: true, currentAddressTrusted: true, allowedHostsEnabled: false
};

beforeEach(() => {
  mocks.many = [];
  mocks.loader.mockReset();
});

describe("System > API reads again when the security area changes elsewhere", () => {
  it("a key made in another tab appears in the list after a security ConfigChanged, and a change to another area reads nothing", async () => {
    const key = { id: "k9", name: "Made elsewhere", prefix: "deluno_zz9", scopes: "read", lastUsedUtc: null, createdUtc: "2026-10-01T10:00:00Z", updatedUtc: "2026-10-01T10:00:00Z" };
    const router = createMemoryRouter(
      [{ path: "/system/api", element: <SystemApiPage />, loader: () => { mocks.loader(); return { apiKeys: mocks.loader.mock.calls.length > 1 ? [key] : [], scopeTemplates: [] }; } }],
      { initialEntries: ["/system/api"] }
    );
    render(<RouterProvider router={router} />);
    await screen.findByText("Generate API key");
    expect(screen.queryByText("Made elsewhere")).not.toBeInTheDocument();

    fire("ConfigChanged", { area: ConfigAreas.notifications });
    await new Promise((resolve) => setTimeout(resolve, 700));
    expect(mocks.loader).toHaveBeenCalledTimes(1);

    fire("ConfigChanged", { area: ConfigAreas.security });
    expect(await screen.findByText("Made elsewhere", undefined, { timeout: 3000 })).toBeInTheDocument();
    expect(mocks.loader).toHaveBeenCalledTimes(2);
  });
});

describe("System > Security reads again when the security area changes elsewhere", () => {
  it("shows the new state after a security ConfigChanged and keeps an address typed and not yet added", async () => {
    const router = createMemoryRouter(
      [{ path: "/system/security", element: <SystemSecurityPage />, loader: () => {
        mocks.loader();
        const second = mocks.loader.mock.calls.length > 1;
        return { allowedHosts: { ...hosts, allowedHosts: second ? ["set-elsewhere.example.com"] : [] }, localNetworkAuth: network };
      } }],
      { initialEntries: ["/system/security"] }
    );
    render(<RouterProvider router={router} />);
    const box = await screen.findByPlaceholderText("*.example.com");
    fireEvent.change(box, { target: { value: "typed-not-saved.example.com" } });

    fire("ConfigChanged", { area: ConfigAreas.security });

    await waitFor(() => expect(mocks.loader).toHaveBeenCalledTimes(2), { timeout: 3000 });
    expect(await screen.findByText("set-elsewhere.example.com", undefined, { timeout: 3000 })).toBeInTheDocument();
    expect(screen.getByPlaceholderText("*.example.com")).toHaveValue("typed-not-saved.example.com");
  });
});
