import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { DownloadDispatchItem } from "../../lib/api";

/**
 * Low-severity send-drawer rows (#1329): each retry state is worded for a person, and a Dismiss the server refuses says so
 * and keeps the drawer open.
 */
const mocks = vi.hoisted(() => ({
  dismiss: vi.fn(),
  toast: Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn(), info: vi.fn(), warning: vi.fn(), message: vi.fn(), dismiss: vi.fn() })
}));
vi.mock("../../lib/failed-jobs", async (importOriginal) => ({ ...(await importOriginal<typeof import("../../lib/failed-jobs")>()), dismissFailedDispatch: mocks.dismiss }));
vi.mock("../shell/toaster", () => ({ toast: mocks.toast, realtimeToast: {}, Toaster: () => null }));
vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn(async () => new Response("[]", { status: 200 })) }));

const { DownloadDispatchDrawer } = await import("./download-dispatch-drawer");

const failed: DownloadDispatchItem = {
  id: "dispatch-1", libraryId: "library-1", mediaType: "movies", entityType: "movie", entityId: "movie-1", releaseName: "Wanted.Film.2011.1080p.WEB-DL-DELUNO",
  indexerName: "Fixture", downloadClientId: "client-1", downloadClientName: "qBittorrent", status: "failed", notesJson: null, createdUtc: "2026-09-24T02:00:00Z",
  grabStatus: "failed", grabFailureCode: "access_denied",
  failure: {
    serviceType: "download-client", serviceId: "client-1", serviceName: "qBittorrent", operation: "grab", kind: "Unknown", retryState: "NotRetryable", message: "forbidden",
    code: "access_denied", httpStatus: null, upstreamDetail: null, externalId: null, retryAfterUtc: null, attempts: 0, isTransient: false, legacyCategory: "access_denied",
    summary: "qBittorrent did not accept the release.", nextAction: "Check the download client, then search for the title again."
  }
} as DownloadDispatchItem;

beforeEach(() => { mocks.dismiss.mockReset(); vi.clearAllMocks(); });

describe("the send drawer (low rows)", () => {
  it.each([
    ["Retrying", "Retrying"],
    ["RetryScheduled", "Retry scheduled"],
    ["CircuitOpen", "Paused after failures"],
    ["ManualAction", "Needs action"],
    ["NotRetryable", "Will not retry"]
  ])("the %s retry state reads '%s' beside Why it failed", (retryState, label) => {
    render(<DownloadDispatchDrawer dispatch={{ ...failed, failure: { ...failed.failure!, retryState } } as DownloadDispatchItem} onClose={() => undefined} />);

    expect(screen.getByText("Why it failed").parentElement!.textContent).toContain(label);
  });

  it("a Dismiss the server refuses toasts 'That download could not be dismissed.' and leaves the drawer open", async () => {
    mocks.dismiss.mockRejectedValue(new Error("That download could not be dismissed."));
    const onClose = vi.fn();
    render(<DownloadDispatchDrawer dispatch={failed} onClose={onClose} />);

    fireEvent.click(screen.getByRole("button", { name: `Dismiss the failed download ${failed.releaseName}` }));

    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalledWith("That download could not be dismissed."));
    expect(onClose).not.toHaveBeenCalled();
  });

  it("a Dismiss that works says the release is dismissed and closes the drawer", async () => {
    mocks.dismiss.mockResolvedValue(undefined);
    const onClose = vi.fn();
    render(<DownloadDispatchDrawer dispatch={failed} onClose={onClose} />);

    fireEvent.click(screen.getByRole("button", { name: `Dismiss the failed download ${failed.releaseName}` }));

    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith(`${failed.releaseName} dismissed`));
    expect(mocks.dismiss).toHaveBeenCalledWith("dispatch-1");
    expect(onClose).toHaveBeenCalled();
  });
});
