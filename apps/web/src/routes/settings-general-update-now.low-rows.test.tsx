import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api/types/settings";
import { authedFetch } from "../lib/use-auth";

vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));

const toastSuccess = vi.fn();
const toastError = vi.fn();
vi.mock("../components/shell/toaster", async (importOriginal) => {
  const original = await importOriginal<typeof import("../components/shell/toaster")>();
  return {
    ...original,
    toast: Object.assign(() => undefined, { success: (...a: unknown[]) => toastSuccess(...a), error: (...a: unknown[]) => toastError(...a), message: vi.fn(), info: vi.fn(), warning: vi.fn(), dismiss: vi.fn() }),
    realtimeToast: { success: vi.fn(), error: vi.fn(), message: vi.fn(), info: vi.fn(), warning: vi.fn() }
  };
});

const loaderData = { libraries: [], qualityProfiles: [], settings: emptyPlatformSettingsSnapshot };
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate: vi.fn() })
}));

const idle = { isEnabled: false, intervalHours: 24, lastCheckedUtc: null, lastUpdatedUtc: null, status: "disabled", error: null, updatedTrackerIds: [] as string[] };

function answerRunWith(run: Record<string, unknown> | "rejects") {
  vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => {
    const url = typeof input === "string" ? input : input.toString();
    if (url.includes("/update-check/run")) {
      return run === "rejects"
        ? new Response(JSON.stringify({ message: "The definitions server did not answer." }), { status: 404 })
        : new Response(JSON.stringify({ ...idle, ...run }), { status: 200 });
    }
    if (url.includes("/api/trackers/definitions/update-check")) return new Response(JSON.stringify(idle), { status: 200 });
    if (url.includes("/api/system/lan-access")) return new Response(JSON.stringify({ state: "notChecked", port: 5099, ruleName: null, message: "", canAllow: false }), { status: 200 });
    if (url.includes("/api/system/log-level")) return new Response(JSON.stringify({ level: "Information" }), { status: 200 });
    return new Response(JSON.stringify({}), { status: 200 });
  });
}

const { SettingsGeneralPage } = await import("./settings-general-page");

async function pressUpdateNow() {
  render(<MemoryRouter><SettingsGeneralPage /></MemoryRouter>);
  await waitFor(() => expect(screen.getByRole("button", { name: "Update now" })).toBeTruthy());
  fireEvent.click(screen.getByRole("button", { name: "Update now" }));
}

describe("Update now on the tracker definitions (low rows)", () => {
  afterEach(() => {
    vi.clearAllMocks();
  });

  it("says how many definitions were updated, singular and plural, even while the schedule is off", async () => {
    answerRunWith({ status: "updated", updatedTrackerIds: ["a"] });
    await pressUpdateNow();
    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Updated 1 tracker definition."));
  });

  it("says 'Updated 2 tracker definitions.' for two", async () => {
    answerRunWith({ status: "updated", updatedTrackerIds: ["a", "b"] });
    await pressUpdateNow();
    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Updated 2 tracker definitions."));
  });

  it("says they are already up to date when nothing changed", async () => {
    answerRunWith({ status: "up-to-date", updatedTrackerIds: [] });
    await pressUpdateNow();
    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Tracker definitions are already up to date."));
  });

  it("shows the failure text when the update failed, and the reason when the request is refused", async () => {
    answerRunWith({ status: "failed", error: "GitHub did not answer." });
    await pressUpdateNow();
    await waitFor(() => expect(toastError).toHaveBeenCalledWith(expect.stringContaining("GitHub did not answer.")));
    expect(toastSuccess).not.toHaveBeenCalled();
  });

  it("a refused request toasts the server's words", async () => {
    answerRunWith("rejects");
    await pressUpdateNow();
    await waitFor(() => expect(toastError).toHaveBeenCalled());
    expect(toastSuccess).not.toHaveBeenCalled();
  });
});
