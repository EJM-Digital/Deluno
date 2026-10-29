import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { SystemPage } from "./system-screen";

// #1329: the two notices the System screen carries above its health checks: other devices turned away by the firewall,
// and an installed app that was rebuilt after a reinstall.

const fetchJson = vi.hoisted(() => vi.fn());
vi.mock("../lib/api", async (importOriginal) => ({ ...(await importOriginal<object>()), fetchJson }));
vi.mock("../lib/api/client", async (importOriginal) => ({ ...(await importOriginal<object>()), fetchJson }));
vi.mock("../lib/use-auth", () => ({ authedFetch: async () => ({ ok: true, status: 200, json: async () => [], text: async () => "[]" }) }));
vi.mock("../components/shell/toaster", () => ({ toast: { success: vi.fn(), error: vi.fn() }, Toaster: () => null }));

// eslint-disable-next-line @typescript-eslint/no-explicit-any
const loaderData: any = {
  activity: [], activityDisplaySettings: { pageSize: 250 }, automation: [], backupSettings: null, backups: [], downloadClients: [],
  healthChecks: { checks: [], summary: { problemCount: 0, unsnoozedProblemCount: 0 } }, failedJobs: [], indexers: [], jobs: [], lanAccess: null,
  libraries: [], monitoring: { alerts: [] }, processors: [], recycleBin: [], recycleBinSettings: null, retryWindows: [], searchCycles: [],
  settings: emptyPlatformSettingsSnapshot, systemInfo: null, systemTasks: []
};

vi.mock("../lib/route-cache", async (importOriginal) => {
  const { readyRouteSources } = await import("../test/route-sources");
  return {
    ...(await importOriginal<typeof import("../lib/route-cache")>()),
    useRouteSources: () => readyRouteSources(loaderData, vi.fn()),
    refetchSources: async () => undefined
  };
});

const blocked = { state: "blocked", port: 5099, ruleName: null, message: "Windows Firewall is turning other devices away.", canAllow: true };
const rebuilt = { kind: "transmission", displayName: "Transmission", message: "Transmission's settings were rebuilt; 3 torrents may need adding back.", itemCount: 3, canReadd: true };

function answer(lan: unknown, notices: unknown[]) {
  fetchJson.mockImplementation(async (url: string) => (url === "/api/system/lan-access" ? lan : url === "/api/system/component-recovery" ? notices : []));
}

afterEach(() => fetchJson.mockReset());

describe("the notices on the System screen (#1329)", () => {
  it("shows the other-devices notice and the recovery notice above the health checks when there is something to say", async () => {
    answer(blocked, [rebuilt]);

    render(<MemoryRouter initialEntries={["/system"]}><SystemPage /></MemoryRouter>);

    expect(await screen.findByRole("status", { name: "Other devices on your network" })).toBeTruthy();
    expect(screen.getByText("Windows Firewall is turning other devices away.")).toBeTruthy();
    expect(await screen.findByRole("status", { name: "Transmission was rebuilt" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Add them back" })).toBeTruthy();
  });

  it("shows neither when other devices can reach Deluno and no app was rebuilt", async () => {
    answer({ state: "open", port: 5099, ruleName: null, message: "Open.", canAllow: false }, []);

    render(<MemoryRouter initialEntries={["/system"]}><SystemPage /></MemoryRouter>);

    await screen.findAllByText("Health checks");
    expect(screen.queryByRole("status", { name: "Other devices on your network" })).toBeNull();
    expect(screen.queryByRole("status", { name: "Transmission was rebuilt" })).toBeNull();
    expect(fetchJson).toHaveBeenCalledWith("/api/system/component-recovery");
    expect(fetchJson).toHaveBeenCalledWith("/api/system/lan-access");
  });
});
