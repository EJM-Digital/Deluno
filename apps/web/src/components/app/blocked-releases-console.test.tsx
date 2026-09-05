import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { BlockedRelease } from "../../lib/api";
import { authedFetch } from "../../lib/use-auth";
import { BlockedReleasesConsole } from "./blocked-releases-console";

vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../shell/toaster", () => ({
  toast: { success: vi.fn(), warning: vi.fn(), error: vi.fn() }
}));
const { toast: toasts } = (await import("../shell/toaster")) as unknown as {
  toast: { success: ReturnType<typeof vi.fn>; error: ReturnType<typeof vi.fn> };
};

/**
 * The screen that makes a permanent refusal safe.
 *
 * <p>Refusals last until somebody clears them, which was chosen deliberately —
 * and is only defensible because nothing is hidden. Without this list, the
 * design is Radarr's blocklist with the original complaint still attached: a
 * title stops arriving and the reason sits where nobody can see it.</p>
 */
describe("the blocklist", () => {
  beforeEach(() => vi.clearAllMocks());

  it("shows the reason in words rather than in Deluno's vocabulary", () => {
    render(<BlockedReleasesConsole releases={[release()]} onChanged={vi.fn()} />);

    // The import records "noVideoStream". Printing that asks the reader to
    // learn the codebase to find out why their film never arrived.
    expect(screen.getByText("No video in the file")).toBeInTheDocument();
    expect(screen.queryByText("noVideoStream")).not.toBeInTheDocument();
    expect(screen.getByText("Arrival.2016.2160p")).toBeInTheDocument();
  });

  /// A code you can search for beats a word that tells you nothing.
  it("falls back to the code itself for a reason it has no words for", () => {
    render(<BlockedReleasesConsole releases={[release({ reasonCode: "somethingNew" })]} onChanged={vi.fn()} />);

    expect(screen.getByText("somethingNew")).toBeInTheDocument();
  });

  it("un-refuses a release and tells the page to reload", async () => {
    vi.mocked(authedFetch).mockResolvedValue({ ok: true } as Response);
    const onChanged = vi.fn();

    render(<BlockedReleasesConsole releases={[release()]} onChanged={onChanged} />);
    await userEvent.click(screen.getByRole("button", { name: /un-refuse/i }));

    expect(authedFetch).toHaveBeenCalledWith("/api/blocked-releases/block-1", { method: "DELETE" });
    expect(onChanged).toHaveBeenCalled();
  });

  /// Clearing in bulk must not become a storm of searches, so the message says
  /// what did not happen as well as what did.
  it("says that un-refusing has not started a search", async () => {
    vi.mocked(authedFetch).mockResolvedValue({ ok: true } as Response);

    render(<BlockedReleasesConsole releases={[release()]} onChanged={vi.fn()} />);
    await userEvent.click(screen.getByRole("button", { name: /un-refuse/i }));

    expect(toasts.success).toHaveBeenCalledWith(expect.stringContaining("Search for the title when you want it"));
  });

  it("says nothing was changed when the request fails", async () => {
    vi.mocked(authedFetch).mockResolvedValue({ ok: false } as Response);
    const onChanged = vi.fn();

    render(<BlockedReleasesConsole releases={[release()]} onChanged={onChanged} />);
    await userEvent.click(screen.getByRole("button", { name: /un-refuse/i }));

    expect(toasts.error).toHaveBeenCalled();
    expect(onChanged).not.toHaveBeenCalled();
  });

  /// An empty blocklist should read as "nothing has gone wrong", not as a
  /// broken screen.
  it("explains itself when nothing has been refused", () => {
    render(<BlockedReleasesConsole releases={[]} onChanged={vi.fn()} />);

    expect(screen.getByText("Nothing has been refused")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /un-refuse/i })).not.toBeInTheDocument();
  });

  function release(overrides: Partial<BlockedRelease> = {}): BlockedRelease {
    return {
      id: "block-1",
      releaseKey: "arrival.2016.2160p|nebula",
      releaseName: "Arrival.2016.2160p",
      indexerName: "Nebula",
      mediaType: "movies",
      entityId: "movie-1",
      title: "Arrival",
      reasonCode: "noVideoStream",
      reason: "No video stream was detected in this file.",
      torrentHashOrItemId: "hash-1",
      downloadClientId: "qbittorrent-main",
      downloadClientName: "qBittorrent",
      blockedUtc: "2026-09-05T12:00:00Z",
      ...overrides
    };
  }
});
