import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { BlockedRelease } from "../../lib/api";
import type { ImportFailureRule } from "../../lib/failure-reasons";
import { authedFetch } from "../../lib/use-auth";
import { BlockedReleasesConsole } from "./blocked-releases-console";

vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../shell/toaster", () => ({
  toast: { success: vi.fn(), warning: vi.fn(), error: vi.fn() }
}));
const { toast: toasts } = (await import("../shell/toaster")) as unknown as {
  toast: {
    success: ReturnType<typeof vi.fn>;
    warning: ReturnType<typeof vi.fn>;
    error: ReturnType<typeof vi.fn>;
  };
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
    render(<BlockedReleasesConsole releases={[release()]} rules={[]} onChanged={vi.fn()} />);

    // The import records "noVideoStream". Printing that asks the reader to
    // learn the codebase to find out why their film never arrived.
    expect(screen.getByText("No video in the file")).toBeInTheDocument();
    expect(screen.queryByText("noVideoStream")).not.toBeInTheDocument();
    expect(screen.getByText("Arrival.2016.2160p")).toBeInTheDocument();
  });

  /// A code you can search for beats a word that tells you nothing.
  it("falls back to the code itself for a reason it has no words for", () => {
    render(
      <BlockedReleasesConsole releases={[release({ reasonCode: "somethingNew" })]} rules={[]} onChanged={vi.fn()} />
    );

    expect(screen.getByText("somethingNew")).toBeInTheDocument();
  });

  it("un-refuses a release and tells the page to reload", async () => {
    vi.mocked(authedFetch).mockResolvedValue({ ok: true } as Response);
    const onChanged = vi.fn();

    render(<BlockedReleasesConsole releases={[release()]} rules={[]} onChanged={onChanged} />);
    await userEvent.click(screen.getByRole("button", { name: /un-refuse/i }));

    expect(authedFetch).toHaveBeenCalledWith("/api/blocked-releases/block-1", { method: "DELETE" });
    expect(onChanged).toHaveBeenCalled();
  });

  /// Clearing in bulk must not become a storm of searches, so the message says
  /// what did not happen as well as what did.
  it("says that un-refusing has not started a search", async () => {
    vi.mocked(authedFetch).mockResolvedValue({ ok: true } as Response);

    render(<BlockedReleasesConsole releases={[release()]} rules={[]} onChanged={vi.fn()} />);
    await userEvent.click(screen.getByRole("button", { name: /un-refuse/i }));

    expect(toasts.success).toHaveBeenCalledWith(expect.stringContaining("Search for the title when you want it"));
  });

  it("says nothing was changed when the request fails", async () => {
    vi.mocked(authedFetch).mockResolvedValue({ ok: false } as Response);
    const onChanged = vi.fn();

    render(<BlockedReleasesConsole releases={[release()]} rules={[]} onChanged={onChanged} />);
    await userEvent.click(screen.getByRole("button", { name: /un-refuse/i }));

    expect(toasts.error).toHaveBeenCalled();
    expect(onChanged).not.toHaveBeenCalled();
  });

  /// An empty blocklist should read as "nothing has gone wrong", not as a
  /// broken screen.
  it("explains itself when nothing has been refused", () => {
    render(<BlockedReleasesConsole releases={[]} rules={[]} onChanged={vi.fn()} />);

    expect(screen.getByText("Nothing has been refused")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /un-refuse/i })).not.toBeInTheDocument();
  });

  /**
   * "Ask me" is only worth offering if the question actually gets asked
   * somewhere a person will see it — and if the two kinds of entry are told
   * apart. A proposal in the refused list would be Deluno claiming to have
   * decided something it explicitly did not.
   */
  describe("a release it is asking about", () => {
    it("is kept apart from the ones it has refused", () => {
      render(
        <BlockedReleasesConsole
          releases={[release(), release({ id: "block-2", releaseName: "Dune.2021.2160p", state: "proposed" })]}
          rules={[]}
          onChanged={vi.fn()}
        />
      );

      const waiting = screen.getByRole("heading", { name: /waiting for you/i }).closest("section")!;
      expect(within(waiting).getByText("Dune.2021.2160p")).toBeInTheDocument();
      expect(within(waiting).queryByText("Arrival.2016.2160p")).not.toBeInTheDocument();
      expect(screen.getByText("1 refused release")).toBeInTheDocument();
    });

    it("offers both answers, and refusing is the one that changes searches", async () => {
      vi.mocked(authedFetch).mockResolvedValue({ ok: true } as Response);

      render(
        <BlockedReleasesConsole releases={[release({ state: "proposed" })]} rules={[]} onChanged={vi.fn()} />
      );
      await userEvent.click(screen.getByRole("button", { name: /refuse it/i }));

      expect(authedFetch).toHaveBeenCalledWith("/api/blocked-releases/block-1/refuse", { method: "POST" });
      expect(toasts.success).toHaveBeenCalledWith(expect.stringContaining("Searches skip it"));
    });

    it("allows one through the same route that clears a refusal", async () => {
      vi.mocked(authedFetch).mockResolvedValue({ ok: true } as Response);

      render(
        <BlockedReleasesConsole releases={[release({ state: "proposed" })]} rules={[]} onChanged={vi.fn()} />
      );
      await userEvent.click(screen.getByRole("button", { name: /allow it/i }));

      expect(authedFetch).toHaveBeenCalledWith("/api/blocked-releases/block-1", { method: "DELETE" });
    });

    /// No question, no section. An empty "waiting for you" reads as a chore
    /// that has not been done.
    it("is absent entirely when there is nothing to decide", () => {
      render(<BlockedReleasesConsole releases={[release()]} rules={[]} onChanged={vi.fn()} />);

      expect(screen.queryByRole("heading", { name: /waiting for you/i })).not.toBeInTheDocument();
    });
  });

  /**
   * The manual half of the scheduled clear-out — for a refusal that predates
   * the setting, or one whose client was off when the schedule came round.
   * DESIGN-007: nothing automatic is only automatic.
   */
  describe("clearing up a refused copy by hand", () => {
    it("says what the download client was asked to do", async () => {
      vi.mocked(authedFetch).mockResolvedValue({
        ok: true,
        json: async () => ({ outcome: "cleared" })
      } as Response);
      const onChanged = vi.fn();

      render(<BlockedReleasesConsole releases={[release()]} rules={[]} onChanged={onChanged} />);
      await userEvent.click(screen.getByRole("button", { name: /clean up now/i }));

      expect(authedFetch).toHaveBeenCalledWith("/api/blocked-releases/block-1/cleanup", { method: "POST" });
      expect(toasts.success).toHaveBeenCalledWith(expect.stringContaining("Cleared at the download client"));
      expect(onChanged).toHaveBeenCalled();
    });

    /**
     * The one that matters. Pressing a button does not get to overrule the
     * rule that knows what the tracker expects — and being told to wait is not
     * a failure, so it must not read like one.
     */
    it("reads as a deliberate wait, not an error, when the tracker still needs it", async () => {
      vi.mocked(authedFetch).mockResolvedValue({
        ok: true,
        json: async () => ({ outcome: "stillSharing" })
      } as Response);

      render(<BlockedReleasesConsole releases={[release()]} rules={[]} onChanged={vi.fn()} />);
      await userEvent.click(screen.getByRole("button", { name: /clean up now/i }));

      expect(toasts.warning).toHaveBeenCalledWith(expect.stringContaining("sharing rule still needs"));
      expect(toasts.error).not.toHaveBeenCalled();
    });

    /// A client that is off now will not be off for ever, so the words say it
    /// will be tried again rather than that it failed for good.
    it("says a silent client will be tried again", async () => {
      vi.mocked(authedFetch).mockResolvedValue({
        ok: true,
        json: async () => ({ outcome: "clientUnavailable" })
      } as Response);

      render(<BlockedReleasesConsole releases={[release()]} rules={[]} onChanged={vi.fn()} />);
      await userEvent.click(screen.getByRole("button", { name: /clean up now/i }));

      expect(toasts.warning).toHaveBeenCalledWith(expect.stringContaining("try again"));
    });
  });

  /// The rules are set once and then left alone; the list answers "why has my
  /// film not arrived". Seventeen rules above the list would bury it.
  it("keeps the rules folded away, and says how many you have changed", () => {
    render(
      <BlockedReleasesConsole
        releases={[]}
        rules={[rule(), rule({ reasonCode: "missingSource", isOverridden: true })]}
        onChanged={vi.fn()}
      />
    );

    expect(screen.getByText("2 kinds of failure · 1 answered your way")).toBeInTheDocument();
    expect(screen.queryByRole("radiogroup")).not.toBeInTheDocument();
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
      state: "refused",
      ...overrides
    };
  }

  function rule(overrides: Partial<ImportFailureRule> = {}): ImportFailureRule {
    return {
      reasonCode: "noVideoStream",
      category: "badFile",
      decision: "Immediately",
      defaultDecision: "Immediately",
      isOverridden: false,
      ...overrides
    };
  }
});
