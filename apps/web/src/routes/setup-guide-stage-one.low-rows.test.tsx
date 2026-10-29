import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

const fetchJson = vi.hoisted(() => vi.fn());
const authedFetch = vi.hoisted(() => vi.fn());
vi.mock("../lib/api", async (importOriginal) => ({ ...(await importOriginal<object>()), fetchJson }));
vi.mock("../lib/api/client", async (importOriginal) => ({ ...(await importOriginal<object>()), fetchJson }));
vi.mock("../lib/use-auth", async (importOriginal) => ({
  ...(await importOriginal<object>()),
  authedFetch,
  useAuth: () => ({ user: { displayName: "Sam Rivers", username: "sam" } })
}));

const { SetupGuidePage, STEPS } = await import("./setup-guide-page");

const okJson = (body: unknown = {}) => new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } });

function loaderData(stepId: string, channel = "stable") {
  return {
    settings: { movieRootPath: "D:/Media/Movies", seriesRootPath: "D:/Media/TV", timezone: "UTC" },
    libraries: [], qualityProfiles: [], customFormats: [], indexers: [], clients: [], metadataStatus: null,
    progress: { lastCompletedStep: STEPS.findIndex((step) => step.id === stepId), isSkipped: false, isCompleted: false, updatedUtc: "2026-10-19T00:00:00Z" },
    draft: {
      mode: "simple", mediaIntent: "both", movieRootPath: "D:/Media/Movies", seriesRootPath: "D:/Media/TV", movieDownloadsPath: "", seriesDownloadsPath: "",
      qualityPreset: "balanced1080p", formatGoal: "balanced", indexerName: "", indexerProtocol: "torznab", indexerUrl: "",
      clientName: "", clientProtocol: "qbittorrent", clientHost: "", clientPort: "8080", backupEnabled: true,
      firstTitleType: "movies", firstTitle: "", firstTitleYear: "", firstTitleMonitored: true
    },
    guide: { id: "g", name: "g", version: 1, schemaVersion: 1, source: {}, integritySha256: "", qualityTiers: [], qualityProfiles: [], customFormats: [], bundles: [] },
    allowedHosts: { enabled: false, allowedHosts: [], detectedHosts: [], currentHost: "localhost" },
    contentBlocklist: { enabled: false, mode: "block", patterns: [] },
    localNetworkAuth: { enabled: false, additionalLocalNetworks: [], allowList: [], trustedProxies: [], currentAddress: "192.168.1.20", currentAddressIsLocal: true, currentAddressTrusted: true, allowedHostsEnabled: false },
    updatePreferences: { mode: "notify-only", channel, autoCheck: true }
  };
}

function renderStep(stepId: string, lanAccess: () => Promise<unknown> = async () => ({ state: "open", port: 5000, ruleName: null, message: "Windows lets them in.", canAllow: false }), channel = "stable") {
  fetchJson.mockImplementation(async (url: string) => (url === "/api/system/lan-access" ? lanAccess() : []));
  authedFetch.mockImplementation(async () => okJson());
  const router = createMemoryRouter([{ path: "/", element: <SetupGuidePage />, loader: () => loaderData(stepId, channel) }], { initialEntries: ["/"] });
  return render(<RouterProvider router={router} />);
}

const choice = (group: string, label: string) => screen.findByRole("radio", { name: label, hidden: false }).then((el) => {
  const radios = screen.getAllByRole("radiogroup", { name: group })[0].querySelectorAll("[role=radio]");
  return Array.from(radios).find((r) => r.textContent === label) as HTMLElement ?? el;
});

afterEach(() => { cleanup(); fetchJson.mockReset(); authedFetch.mockReset(); });

describe("Stage 1 of the guide, low rows (#1329)", () => {
  it("Your account shows the signed-in display name and 'Signed in as <username>'", async () => {
    renderStep("account");
    expect(await screen.findByText("Sam Rivers")).toBeTruthy();
    expect(screen.getByText("Signed in as sam")).toBeTruthy();
  });

  it.each([
    ["First day of week", "Sunday", "calendarFirstDayOfWeek", "sunday"],
    ["Time format", "24-hour", "timeFormat", "24"],
    ["Short date format", "2026-03-25", "shortDateFormat", "iso"],
    ["Short date format", "03/25/2026", "shortDateFormat", "mdy"],
    ["Long date format", "Wednesday, March 25", "longDateFormat", "mdy"]
  ])("%s: choosing %s selects it (%s=%s is what the baseline later writes)", async (group, label, _key, _value) => {
    renderStep("timeRegion");
    const option = await choice(group, label);
    expect(option.getAttribute("aria-checked")).toBe("false");
    fireEvent.click(option);
    await waitFor(() => expect(option.getAttribute("aria-checked")).toBe("true"));
  });

  it("typing a timezone keeps the typed text, and Use this browser sets the browser's own timezone", async () => {
    renderStep("timeRegion");
    const box = await screen.findByLabelText("Timezone") as HTMLInputElement;
    fireEvent.change(box, { target: { value: "Australia/Sydney" } });
    expect(box.value).toBe("Australia/Sydney");
    fireEvent.click(screen.getByRole("button", { name: "Use this browser" }));
    expect(box.value).toBe(Intl.DateTimeFormat().resolvedOptions().timeZone);
  });

  it("the Other devices step says it is checking while the check runs", async () => {
    renderStep("network", () => new Promise(() => undefined));
    expect(await screen.findByText("Checking whether other devices can reach Deluno...")).toBeTruthy();
  });

  it("the Other devices step says Deluno could not check when the check request fails", async () => {
    renderStep("network", async () => { throw new Error("down"); });
    expect(await screen.findByText(/^Deluno could not check whether other devices can reach it\./)).toBeTruthy();
    expect(screen.queryByText("Checking whether other devices can reach Deluno...")).toBeNull();
  });

  it("the Updates step names the current update channel", async () => {
    renderStep("updates", undefined, "beta");
    fireEvent.click(await screen.findByRole("radio", { name: "Check for updates by itself? Yes" }));
    expect(await screen.findByText(/you stay on the beta channel/)).toBeTruthy();
  });

  it("the allow-list Add button is disabled while the box is empty, adds an entry, and the X removes it", async () => {
    renderStep("signIn");
    fireEvent.click(await screen.findByRole("radio", { name: "Skip sign-in for devices on your home network? Yes" }));
    const add = await screen.findByRole("button", { name: "Add address to the allow list" }) as HTMLButtonElement;
    expect(add.disabled).toBe(true);
    const box = screen.getByPlaceholderText("192.168.1.0/28");
    fireEvent.change(box, { target: { value: "192.168.1.0/28" } });
    expect(add.disabled).toBe(false);
    fireEvent.click(add);
    expect(await screen.findByText("192.168.1.0/28")).toBeTruthy();
    expect(add.disabled).toBe(true);
    fireEvent.click(screen.getByRole("button", { name: "Remove 192.168.1.0/28 from the allow list" }));
    await waitFor(() => expect(screen.queryByText("192.168.1.0/28")).toBeNull());
  });
});
