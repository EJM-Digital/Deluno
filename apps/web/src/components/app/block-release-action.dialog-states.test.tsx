import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { DownloadTelemetryOverview } from "../../lib/api";
import { authedFetch } from "../../lib/use-auth";
import { fetchJson } from "../../lib/api/client";
import { BlockReleaseAction } from "./block-release-action";

/**
 * The block confirm's own states (coverage register, area search-grab): what it says while the queue is being checked, what
 * happens when that check fails, and what Cancel or Escape does. Only the network is answered.
 */
vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../../lib/api/client", () => ({ fetchJson: vi.fn() }));

vi.mock("../shell/toaster", () => ({
  toast: { success: vi.fn(), error: vi.fn() }
}));
const { toast: toasts } = (await import("../shell/toaster")) as unknown as {
  toast: { success: ReturnType<typeof vi.fn>; error: ReturnType<typeof vi.fn> };
};

const RELEASE = "Arrival.2016.2160p.UHD.BluRay";

function renderAction(onBlocked = vi.fn()) {
  render(
    <BlockReleaseAction
      releaseName={RELEASE}
      indexerName="Nebula"
      mediaType="movie"
      entityId="movie-1"
      title="Arrival"
      onBlocked={onBlocked}
    />
  );
  return onBlocked;
}

const postCalls = () => vi.mocked(authedFetch).mock.calls.filter(([, init]) => (init as RequestInit | undefined)?.method === "POST");

describe("the block confirm while the queue is checked, and its cancel", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(authedFetch).mockResolvedValue({ ok: true, json: async () => ({ id: "block-1", releaseName: RELEASE }) } as Response);
  });

  it("while the queue is checked the dialog says so, and the confirm is disabled until the answer comes", async () => {
    let answer!: (overview: DownloadTelemetryOverview) => void;
    vi.mocked(fetchJson).mockReturnValue(new Promise<DownloadTelemetryOverview>((resolve) => { answer = resolve; }));
    renderAction();

    await userEvent.click(screen.getByRole("button", { name: 'Block "Arrival"' }));

    expect(await screen.findByText("Checking whether it's downloading right now…")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Block this release" })).toBeDisabled();

    answer({ summary: { activeCount: 0, queuedCount: 0, completedCount: 0, stalledCount: 0, processingCount: 0, importReadyCount: 0, totalSpeedMbps: 0, totalUploadSpeedMbps: 0, waitingForProcessorCount: 0 }, items: [], capturedUtc: "2026-09-27T09:00:00Z" } as unknown as DownloadTelemetryOverview);
    await waitFor(() => expect(screen.queryByText("Checking whether it's downloading right now…")).toBeNull());
    expect(screen.getByRole("button", { name: "Block this release" })).toBeEnabled();
  });

  it("if the queue check fails, blocking still works with no stop offer", async () => {
    vi.mocked(fetchJson).mockRejectedValue(new Error("telemetry is down"));
    const onBlocked = renderAction();

    await userEvent.click(screen.getByRole("button", { name: 'Block "Arrival"' }));
    await waitFor(() => expect(screen.queryByText("Checking whether it's downloading right now…")).toBeNull());
    expect(screen.queryByRole("checkbox", { name: /stop it/i })).toBeNull();

    await userEvent.click(await screen.findByRole("button", { name: "Block this release" }));
    await waitFor(() => expect(onBlocked).toHaveBeenCalled());
  });

  it("Cancel closes the confirm and blocks nothing", async () => {
    vi.mocked(fetchJson).mockResolvedValue({ summary: {}, items: [], capturedUtc: "2026-09-27T09:00:00Z" } as unknown as DownloadTelemetryOverview);
    const onBlocked = renderAction();

    await userEvent.click(screen.getByRole("button", { name: 'Block "Arrival"' }));
    await userEvent.click(await screen.findByRole("button", { name: "Cancel" }));

    await waitFor(() => expect(screen.queryByRole("button", { name: "Block this release" })).toBeNull());
    expect(postCalls()).toHaveLength(0);
    expect(onBlocked).not.toHaveBeenCalled();
  });

  it("Escape cannot close the confirm while the block is being sent", async () => {
    vi.mocked(fetchJson).mockResolvedValue({ summary: {}, items: [], capturedUtc: "2026-09-27T09:00:00Z" } as unknown as DownloadTelemetryOverview);
    let release!: () => void;
    const gate = new Promise<void>((resolve) => { release = resolve; });
    vi.mocked(authedFetch).mockImplementation(async (_url, init) => {
      if ((init as RequestInit | undefined)?.method === "POST") await gate;
      return { ok: true, json: async () => ({ id: "block-1", releaseName: RELEASE }) } as Response;
    });
    renderAction();

    await userEvent.click(screen.getByRole("button", { name: 'Block "Arrival"' }));
    await userEvent.click(await screen.findByRole("button", { name: "Block this release" }));
    await userEvent.keyboard("{Escape}");

    expect(screen.getByRole("button", { name: "Block this release" })).toBeInTheDocument();
    release();
  });
});
