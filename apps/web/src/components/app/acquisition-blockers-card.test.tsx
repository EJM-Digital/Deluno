import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { AcquisitionBlocker, AcquisitionBlockersResponse } from "../../lib/api";
import { authedFetch } from "../../lib/use-auth";
import { AcquisitionBlockersCard } from "./acquisition-blockers-card";

vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn() }));

// Declared inside the factory, because vi.mock is hoisted above every
// top-level binding in this file.
vi.mock("../shell/toaster", () => ({
  toast: { success: vi.fn(), warning: vi.fn(), error: vi.fn() }
}));
const { toast: toasts } = await import("../shell/toaster") as unknown as {
  toast: { success: ReturnType<typeof vi.fn>; warning: ReturnType<typeof vi.fn>; error: ReturnType<typeof vi.fn> };
};

function blocker(overrides: Partial<AcquisitionBlocker> = {}): AcquisitionBlocker {
  return {
    kind: "download-in-flight",
    source: "qBittorrent",
    summary: "A download is already with qBittorrent.",
    detail: "It was sent 2 hours ago and has not finished.",
    canClear: true,
    clearEffect: "Removes the download from qBittorrent, along with its files.",
    ...overrides
  };
}

function response(overrides: Partial<AcquisitionBlockersResponse> = {}): AcquisitionBlockersResponse {
  return {
    mediaId: "movie-1",
    mediaType: "movies",
    title: "Arrival",
    blockers: [blocker()],
    nothingIsBlocking: false,
    summary: "One thing is holding Arrival back.",
    canForce: true,
    ...overrides
  };
}

function renderCard(blockers: AcquisitionBlockersResponse | null, onForced = vi.fn()) {
  render(
    <AcquisitionBlockersCard
      blockers={blockers}
      route="/api/movies"
      mediaId="movie-1"
      onForced={onForced}
    />
  );
  return onForced;
}

/**
 * The card that says the quiet part.
 *
 * <p>Radarr blocklists a release that failed to import, SABnzbd remembers the
 * name, qBittorrent still holds the infohash — three correct mechanisms, all
 * three silent, and a title that never arrives with no account of why. What is
 * asserted here is the account: that the reasons are shown in the server's own
 * words, that the button naming what it will do appears only when there is
 * something to do, and that a force which half worked says so.</p>
 */
describe("the acquisition blockers card", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("says nothing at all when nothing is in the way", () => {
    // A permanent "no problems" panel is a panel people stop seeing, and this
    // one has to be noticed on the day it finally has something to say.
    const { container } = render(
      <AcquisitionBlockersCard
        blockers={response({ blockers: [], nothingIsBlocking: true, canForce: false })}
        route="/api/movies"
        mediaId="movie-1"
        onForced={vi.fn()}
      />
    );

    expect(container).toBeEmptyDOMElement();
  });

  it("renders nothing rather than an error when the answer could not be fetched", () => {
    const { container } = render(
      <AcquisitionBlockersCard blockers={null} route="/api/movies" mediaId="movie-1" onForced={vi.fn()} />
    );

    expect(container).toBeEmptyDOMElement();
  });

  it("shows each reason in the server's words, and where the record lives", () => {
    renderCard(
      response({
        blockers: [
          blocker(),
          blocker({
            kind: "import-excluded",
            source: "Deluno",
            summary: "An exclusion covers this title.",
            detail: "Lists and collections will not add it back.",
            clearEffect: "Removes the exclusion."
          })
        ]
      })
    );

    expect(screen.getByText("A download is already with qBittorrent.")).toBeInTheDocument();
    expect(screen.getByText("It was sent 2 hours ago and has not finished.")).toBeInTheDocument();
    expect(screen.getByText("An exclusion covers this title.")).toBeInTheDocument();
    expect(screen.getByText("qBittorrent")).toBeInTheDocument();
    expect(screen.getByText("Deluno")).toBeInTheDocument();
  });

  it("offers no button when there is nothing an override could clear", () => {
    // Already holding the file at its target is a complete explanation and not
    // an obstacle. A button here would promise something it cannot do.
    renderCard(
      response({
        blockers: [
          blocker({
            kind: "already-held",
            source: "Deluno",
            summary: "This is already in your library at its target quality.",
            canClear: false,
            clearEffect: null
          })
        ],
        canForce: false,
        summary: "Arrival is already here."
      })
    );

    expect(screen.getByText("This is already in your library at its target quality.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /force a re-download/i })).not.toBeInTheDocument();
  });

  it("names what will happen before it happens, in the server's own words", async () => {
    renderCard(response());

    await userEvent.click(screen.getByRole("button", { name: /force a re-download/i }));

    expect(screen.getByText(/Removes the download from qBittorrent, along with its files\./)).toBeInTheDocument();
    expect(screen.getByText(/does not undo it/)).toBeInTheDocument();
  });

  it("reports what a force actually did, not that it succeeded", async () => {
    vi.mocked(authedFetch).mockResolvedValue({
      ok: true,
      json: async () => ({
        mediaId: "movie-1",
        cleared: ["Removed the download from qBittorrent, along with its files."],
        couldNotClear: ["Weir would not release the file: it is still converting."],
        searchStarted: true,
        summary: "Cleared 1 of 2 things holding Arrival back. Weir would not release the file: it is still converting."
      })
    } as Response);

    const onForced = renderCard(response());

    await userEvent.click(screen.getByRole("button", { name: /force a re-download/i }));
    await userEvent.click(screen.getByRole("button", { name: /clear it and search again/i }));

    // A warning, not a success — half of it did not work, and a green tick
    // over that is the silence this whole feature exists to remove.
    expect(toasts.warning).toHaveBeenCalledWith(
      "Cleared 1 of 2 things holding Arrival back. Weir would not release the file: it is still converting."
    );
    expect(toasts.success).not.toHaveBeenCalled();
    expect(onForced).toHaveBeenCalled();
  });

  it("says nothing was changed when the request itself fails", async () => {
    vi.mocked(authedFetch).mockResolvedValue({ ok: false } as Response);
    const onForced = renderCard(response());

    await userEvent.click(screen.getByRole("button", { name: /force a re-download/i }));
    await userEvent.click(screen.getByRole("button", { name: /clear it and search again/i }));

    expect(toasts.error).toHaveBeenCalledWith("The re-download could not be forced. Nothing was changed.");
    expect(onForced).not.toHaveBeenCalled();
  });
});
