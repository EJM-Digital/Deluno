import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

const fetchJson = vi.hoisted(() => vi.fn());
vi.mock("../lib/api", async (importOriginal) => ({ ...(await importOriginal<object>()), fetchJson }));
vi.mock("../lib/use-auth", async (importOriginal) => ({
  ...(await importOriginal<object>()),
  authedFetch: vi.fn(async () => new Response("{}", { status: 200 }))
}));

const { SetupGuidePage, STEPS, AUTOMATION_QUESTIONS } = await import("./setup-guide-page");

// The Yes/No questions of the guided setup (#936, #1245), each with the step it lives on and the exact words on its buttons.
const SINGLE_QUESTIONS = [
  { step: "signIn", question: "Skip sign-in for devices on your home network?" },
  { step: "addresses", question: "Only answer on the addresses you use?" },
  { step: "updates", question: "Update Deluno automatically?" }
] as const;

function loaderData(overrides: { stepId: string; completed?: boolean; draft?: Record<string, unknown>; hostsEnabled?: boolean; skipSignIn?: boolean; autoUpdate?: boolean }) {
  return {
    settings: { movieRootPath: "D:/Media/Movies", seriesRootPath: "D:/Media/TV", timezone: "UTC" },
    libraries: [], qualityProfiles: [], customFormats: [], indexers: [], clients: [], metadataStatus: null,
    progress: { lastCompletedStep: STEPS.findIndex((step) => step.id === overrides.stepId), isSkipped: false, isCompleted: overrides.completed ?? false, updatedUtc: "2026-10-11T00:00:00Z" },
    draft: {
      mode: "simple", mediaIntent: "both", movieRootPath: "D:/Media/Movies", seriesRootPath: "D:/Media/TV", downloadsPath: "",
      qualityPreset: "balanced1080p", formatGoal: "balanced", indexerName: "", indexerProtocol: "torznab", indexerUrl: "",
      clientName: "", clientProtocol: "qbittorrent", clientHost: "", clientPort: "8080", metadataProviderMode: "direct", metadataBrokerUrl: "",
      backupEnabled: true, firstTitleType: "movies", firstTitle: "", firstTitleYear: "", firstTitleMonitored: true, ...overrides.draft
    },
    guide: { id: "g", name: "g", version: 1, schemaVersion: 1, source: {}, integritySha256: "", qualityTiers: [], customFormats: [], qualityProfiles: [], bundles: [] },
    allowedHosts: { enabled: overrides.hostsEnabled ?? false, allowedHosts: [], detectedHosts: [{ host: "192.168.1.10", reason: "This computer" }], currentHost: "localhost" },
    contentBlocklist: {},
    localNetworkAuth: { enabled: overrides.skipSignIn ?? false, additionalLocalNetworks: [], allowList: [], trustedProxies: [], currentAddress: "192.168.1.20", currentAddressIsLocal: true, currentAddressTrusted: true, allowedHostsEnabled: false },
    updatePreferences: { mode: overrides.autoUpdate ? "download-apply-on-restart" : "notify-only", channel: "stable", autoCheck: true }
  };
}

function renderStep(overrides: Parameters<typeof loaderData>[0]) {
  fetchJson.mockResolvedValue([]);
  const router = createMemoryRouter([{ path: "/", element: <SetupGuidePage />, loader: () => loaderData(overrides) }], { initialEntries: ["/"] });
  return render(<RouterProvider router={router} />);
}

const continueButton = () => screen.findByRole("button", { name: /^Continue$/ }) as Promise<HTMLButtonElement>;
const answers = (question: string) => ({
  yes: screen.getByRole("radio", { name: `${question} Yes` }) as HTMLButtonElement,
  no: screen.getByRole("radio", { name: `${question} No` }) as HTMLButtonElement
});

afterEach(() => { cleanup(); fetchJson.mockReset(); });

describe("every Yes/No question starts unanswered on a fresh install (#1245)", () => {
  it.each(SINGLE_QUESTIONS)("$question: neither answer is chosen and Continue waits for one", async ({ step, question }) => {
    renderStep({ stepId: step });
    const button = await continueButton();
    const { yes, no } = answers(question);
    expect(yes.getAttribute("aria-checked")).toBe("false");
    expect(no.getAttribute("aria-checked")).toBe("false");
    expect(button.disabled).toBe(true);
    fireEvent.click(no);
    expect(no.getAttribute("aria-checked")).toBe("true");
    expect((await continueButton()).disabled).toBe(false);
  });

  it("Automation: all fourteen questions start unanswered, and Continue opens only once every one is answered", async () => {
    renderStep({ stepId: "automation" });
    const button = await continueButton();
    for (const item of AUTOMATION_QUESTIONS) {
      const { yes, no } = answers(item.question);
      expect(yes.getAttribute("aria-checked"), item.question).toBe("false");
      expect(no.getAttribute("aria-checked"), item.question).toBe("false");
    }
    expect(button.disabled).toBe(true);
    for (const item of AUTOMATION_QUESTIONS) fireEvent.click(answers(item.question).no);
    expect((await continueButton()).disabled).toBe(false);
  });
});

describe("a re-run shows each saved answer, labelled as the current setting (#1245)", () => {
  it.each(SINGLE_QUESTIONS)("$question", async ({ step, question }) => {
    renderStep({ stepId: step, completed: true, hostsEnabled: true, skipSignIn: true, autoUpdate: true });
    await continueButton();
    expect(answers(question).yes.getAttribute("aria-checked")).toBe("true");
    expect(screen.getByText("Current setting: Yes")).toBeTruthy();
  });
});
