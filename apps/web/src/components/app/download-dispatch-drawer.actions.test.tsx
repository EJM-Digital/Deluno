import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { DownloadDispatchItem } from "../../lib/api";

/** The drawer's own wiring: its three actions and what each tells the page. What Remove and Block do inside are proven in their own tests. */
const authedFetch = vi.hoisted(() => vi.fn());
vi.mock("../../lib/use-auth", () => ({ authedFetch }));
const toast = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn(), info: vi.fn() }));
vi.mock("../shell/toaster", () => ({ toast }));
vi.mock("./remove-download-action", () => ({
  RemoveDownloadAction: ({ onRemoved, releaseName }: { onRemoved?: () => void; releaseName: string }) => (
    <button type="button" onClick={() => onRemoved?.()}>{`Remove download of ${releaseName}`}</button>
  )
}));
vi.mock("./block-release-action", () => ({
  BlockReleaseAction: ({ onBlocked, releaseName }: { onBlocked?: () => void; releaseName: string }) => (
    <button type="button" onClick={() => onBlocked?.()}>{`Block ${releaseName}`}</button>
  )
}));

const { DownloadDispatchDrawer } = await import("./download-dispatch-drawer");

const base: DownloadDispatchItem = {
  id: "dispatch-1",
  libraryId: "library-1",
  mediaType: "movies",
  entityType: "movie",
  entityId: "movie-1",
  releaseName: "Wanted.Film.2011.1080p.WEB-DL-DELUNO",
  indexerName: "Fixture",
  downloadClientId: "client-1",
  downloadClientName: "qBittorrent",
  status: "failed",
  notesJson: null,
  createdUtc: "2026-09-24T02:00:00Z",
  grabStatus: "failed",
  grabFailureCode: "access_denied",
  failure: {
    serviceType: "download-client", serviceId: "client-1", serviceName: "qBittorrent", operation: "grab", kind: "Unknown",
    retryState: "NotRetryable", message: "forbidden", code: "access_denied", httpStatus: null, upstreamDetail: null, externalId: null,
    retryAfterUtc: null, attempts: 0, isTransient: false, legacyCategory: "access_denied",
    summary: "qBittorrent did not accept the release.", nextAction: "Check the download client, then search for the title again."
  }
};

beforeEach(() => {
  vi.clearAllMocks();
  authedFetch.mockResolvedValue(new Response(null, { status: 204 }));
});

describe("the download dispatch drawer's actions", () => {
  it("Dismiss on a failed send posts the dismissal, says so, closes the drawer and tells the page to read again", async () => {
    const onClose = vi.fn();
    const onBlocked = vi.fn();
    render(<DownloadDispatchDrawer dispatch={base} onClose={onClose} onBlocked={onBlocked} />);

    fireEvent.click(screen.getByRole("button", { name: `Dismiss the failed download ${base.releaseName}` }));

    await waitFor(() => expect(onBlocked).toHaveBeenCalledTimes(1));
    expect(authedFetch).toHaveBeenCalledWith("/api/download-dispatches/dispatch-1/dismiss", { method: "POST" });
    expect(toast.success).toHaveBeenCalledWith(`${base.releaseName} dismissed`);
    expect(onClose).toHaveBeenCalled();
  });

  it("a dismissal the server refuses says so and leaves the drawer open", async () => {
    authedFetch.mockResolvedValue(new Response("{}", { status: 500 }));
    const onClose = vi.fn();
    const onBlocked = vi.fn();
    render(<DownloadDispatchDrawer dispatch={base} onClose={onClose} onBlocked={onBlocked} />);

    fireEvent.click(screen.getByRole("button", { name: `Dismiss the failed download ${base.releaseName}` }));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("That download could not be dismissed."));
    expect(onClose).not.toHaveBeenCalled();
    expect(onBlocked).not.toHaveBeenCalled();
  });

  it("offers no Dismiss for a send that has not failed", () => {
    render(<DownloadDispatchDrawer dispatch={{ ...base, status: "sent", grabStatus: "sent", failure: undefined } as DownloadDispatchItem} onClose={() => undefined} />);

    expect(screen.queryByRole("button", { name: /^Dismiss the failed download/ })).toBeNull();
  });

  it("Remove download and Block release each close the drawer and tell the page to read again", () => {
    const onClose = vi.fn();
    const onBlocked = vi.fn();
    render(<DownloadDispatchDrawer dispatch={base} onClose={onClose} onBlocked={onBlocked} />);

    fireEvent.click(screen.getByRole("button", { name: `Remove download of ${base.releaseName}` }));
    expect(onClose).toHaveBeenCalledTimes(1);
    expect(onBlocked).toHaveBeenCalledTimes(1);

    fireEvent.click(screen.getByRole("button", { name: `Block ${base.releaseName}` }));
    expect(onClose).toHaveBeenCalledTimes(2);
    expect(onBlocked).toHaveBeenCalledTimes(2);
  });
});
