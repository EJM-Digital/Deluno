import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { AcquisitionBlocker, AcquisitionBlockersResponse } from "../../lib/api";
import { authedFetch } from "../../lib/use-auth";
import { ACQUISITION_BLOCKER_KINDS } from "../../lib/api";
import { LIST_TRACK } from "../ui/list-card";
import { AcquisitionBlockersCard, BLOCKER_TONE } from "./acquisition-blockers-card";

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

  /// Every kind the server can send has a colour chosen for it.
  it("has a tone for every kind the server can send", () => {
    // The lookup falls back to grey for anything it does not recognise, which
    // is right at runtime and a silent gap at build time: a kind added on the
    // server renders as "nothing much" until somebody notices. This is the
    // noticing, and it reads the real map rather than a copy of it.
    const missing = Object.values(ACQUISITION_BLOCKER_KINDS).filter((kind) => !(kind in BLOCKER_TONE));

    expect(missing, `these kinds have no tone: ${missing.join(", ")}`).toEqual([]);
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

  it("tells the person plainly that everything was cleared, searching the chosen library", async () => {
    vi.mocked(authedFetch).mockResolvedValue({
      ok: true,
      json: async () => ({ mediaId: "movie-1", cleared: ["x"], couldNotClear: [], searchStarted: true, summary: "Cleared 1 thing." })
    } as Response);
    const onForced = vi.fn();
    render(<AcquisitionBlockersCard blockers={response()} route="/api/movies" mediaId="movie-1" libraryId="lib 1" onForced={onForced} />);

    await userEvent.click(screen.getByRole("button", { name: /force a re-download/i }));
    await userEvent.click(screen.getByRole("button", { name: /clear it and search again/i }));

    expect(authedFetch).toHaveBeenCalledWith("/api/movies/movie-1/force-redownload?libraryId=lib%201", { method: "POST" });
    expect(toasts.success).toHaveBeenCalledWith("Cleared 1 thing.");
    expect(onForced).toHaveBeenCalled();
  });

  it("counts the records when more than one will be cleared, and lets the person back out", async () => {
    renderCard(response({ blockers: [blocker(), blocker({ kind: "import-excluded", source: "Deluno" })] }));

    await userEvent.click(screen.getByRole("button", { name: /force a re-download/i }));
    expect(screen.getByText(/2 records will be cleared/)).toBeInTheDocument();

    await userEvent.keyboard("{Escape}");
    expect(screen.queryByText(/2 records will be cleared/)).not.toBeInTheDocument();
  });

  it("falls back to the reason's own words when the server gave no effect, and to a quiet chip for a kind it does not know", async () => {
    renderCard(response({ blockers: [blocker({ kind: "something-new", clearEffect: undefined, summary: "Held for a new reason." })] }));

    await userEvent.click(screen.getByRole("button", { name: /force a re-download/i }));

    expect(screen.getAllByText(/Held for a new reason\./).length).toBeGreaterThan(1);
  });

  it.each([
    ["already-held", "text-success"],
    ["download-in-flight", "text-info"],
    ["processor-holding-file", "text-info"],
    ["import-excluded", "text-destructive"],
    ["search-skipped", "text-warning"],
    ["search-deferred", "text-warning"],
    ["not-yet-available", "text-muted-foreground"],
    ["previously-downloaded", "text-warning"],
    ["releases-blocked", "text-muted-foreground"],
    ["a-kind-from-a-newer-server", "text-muted-foreground"]
  ])("colours a %s reason with %s, and a kind it does not know in the quiet grey", (kind, colour) => {
    renderCard(response({ blockers: [blocker({ kind, source: "Somewhere" })] }));

    expect(screen.getByText("Somewhere")).toHaveClass(colour);
  });

  it("says a download already on its way is on its way, and anything else is a reason it will not download", () => {
    const { unmount } = render(
      <AcquisitionBlockersCard
        blockers={response({ blockers: [blocker(), blocker({ kind: "processor-holding-file", source: "Weir" })] })}
        route="/api/movies" mediaId="movie-1" onForced={vi.fn()}
      />
    );
    expect(screen.getByText("Already on Its Way")).toBeInTheDocument();
    expect(screen.getByText("2 reasons")).toBeInTheDocument();
    unmount();

    renderCard(response({ blockers: [blocker(), blocker({ kind: "import-excluded", source: "Deluno" })] }));
    expect(screen.getByText("Why This Will Not Download")).toBeInTheDocument();
    expect(screen.queryByText("Already on Its Way")).not.toBeInTheDocument();
  });

  it("says one reason in the singular, and heads the two columns", () => {
    renderCard(response({ blockers: [blocker({ kind: "import-excluded" })] }));

    expect(screen.getByText("1 reason")).toBeInTheDocument();
    expect(screen.getByRole("columnheader", { name: "Reason" })).toBeInTheDocument();
    expect(screen.getByRole("columnheader", { name: "Where" })).toBeInTheDocument();
  });

  it("shows when a wait ends in the person's own clock, and the server's summary beside the button", () => {
    renderCard(response({ summary: "Arrival is held back.", blockers: [blocker({ kind: "search-deferred", summary: "Deluno will search again in about 6 hours.", untilUtc: "2026-10-08T12:03:00Z" })] }));

    expect(screen.getByText(/^Deluno will search again in about 6 hours \(\d{1,2}:03 [ap]m\)\.$/)).toBeInTheDocument();
    expect(screen.getByText("Arrival is held back.")).toBeInTheDocument();
  });

  it("draws nothing when it was told something is in the way but gave no reasons", () => {
    const { container } = render(
      <AcquisitionBlockersCard blockers={response({ blockers: [], nothingIsBlocking: false })} route="/api/movies" mediaId="movie-1" onForced={vi.fn()} />
    );
    expect(container).toBeEmptyDOMElement();
  });

  it("draws nothing when it says nothing is blocking, even with reasons listed", () => {
    const { container } = render(
      <AcquisitionBlockersCard blockers={response({ nothingIsBlocking: true })} route="/api/movies" mediaId="movie-1" onForced={vi.fn()} />
    );
    expect(container).toBeEmptyDOMElement();
  });

  it("lists only the effects that can be cleared, in order, one sentence after another", async () => {
    renderCard(response({
      blockers: [
        blocker({ clearEffect: "Removes the download." }),
        blocker({ kind: "already-held", source: "Deluno", canClear: false, clearEffect: "Should not appear." }),
        blocker({ kind: "import-excluded", source: "Deluno", clearEffect: "Removes the exclusion." })
      ]
    }));

    await userEvent.click(screen.getByRole("button", { name: /force a re-download/i }));

    expect(screen.getByText("2 records will be cleared, and then Deluno will search again: Removes the download. Removes the exclusion. This reaches into things Deluno does not own, and pressing the button a second time does not undo it.")).toBeInTheDocument();
  });

  it("says one record will be cleared when only one can be", async () => {
    renderCard(response());

    await userEvent.click(screen.getByRole("button", { name: /force a re-download/i }));

    expect(screen.getByText(/^One record will be cleared, and then Deluno will search again: /)).toBeInTheDocument();
  });

  it("does not offer the button while another action on the page owns the buttons", () => {
    render(<AcquisitionBlockersCard blockers={response()} route="/api/movies" mediaId="movie-1" disabled onForced={vi.fn()} />);

    expect(screen.getByRole("button", { name: /force a re-download/i })).toBeDisabled();
  });

  it("offers the button when nothing else owns it", () => {
    renderCard(response());

    expect(screen.getByRole("button", { name: /force a re-download/i })).toBeEnabled();
  });

  it("forces without a library when the title is in only one", async () => {
    vi.mocked(authedFetch).mockResolvedValue({ ok: true, json: async () => ({ cleared: [], couldNotClear: [], summary: "Done." }) } as unknown as Response);
    renderCard(response());

    await userEvent.click(screen.getByRole("button", { name: /force a re-download/i }));
    await userEvent.click(screen.getByRole("button", { name: /clear it and search again/i }));

    expect(authedFetch).toHaveBeenCalledWith("/api/movies/movie-1/force-redownload", { method: "POST" });
  });

  it("shows Clearing and cannot be pressed again while the force runs, then closes the question and frees the button", async () => {
    let finish: (value: Response) => void = () => undefined;
    vi.mocked(authedFetch).mockReturnValue(new Promise<Response>((resolve) => { finish = resolve; }));
    renderCard(response());

    await userEvent.click(screen.getByRole("button", { name: /force a re-download/i }));
    await userEvent.click(screen.getByRole("button", { name: /clear it and search again/i }));

    expect(screen.getByRole("button", { name: "Clearing…" })).toBeDisabled();

    finish({ ok: true, json: async () => ({ cleared: [], couldNotClear: [], summary: "Done." }) } as unknown as Response);

    await waitFor(() => expect(screen.queryByText(/does not undo it/)).not.toBeInTheDocument());
    expect(screen.getByRole("button", { name: /force a re-download/i })).toBeEnabled();
  });

  it("lays the reason out wide and the place beside it narrow, with no arrow track, on a phone as on a desktop", () => {
    const { container } = render(<AcquisitionBlockersCard blockers={response()} route="/api/movies" mediaId="movie-1" onForced={vi.fn()} />);

    const table = container.querySelector<HTMLElement>("[style*='--list-cols']")!;
    expect(table.style.getPropertyValue("--list-cols")).toBe(`minmax(0,1.6fr) ${LIST_TRACK.action}`);
    expect(table.style.getPropertyValue("--list-cols-mobile")).toBe("minmax(0,1fr) auto");
  });

  it("says the force failed, and does not call it done, even when the refusal came with a readable body", async () => {
    vi.mocked(authedFetch).mockResolvedValue({ ok: false, json: async () => ({ couldNotClear: [], summary: "Done." }) } as unknown as Response);
    const onForced = renderCard(response());

    await userEvent.click(screen.getByRole("button", { name: /force a re-download/i }));
    await userEvent.click(screen.getByRole("button", { name: /clear it and search again/i }));

    await waitFor(() => expect(toasts.error).toHaveBeenCalled());
    expect(toasts.success).not.toHaveBeenCalled();
    expect(onForced).not.toHaveBeenCalled();
  });

  it("keeps the question open, and the button free, when the force fails", async () => {
    vi.mocked(authedFetch).mockResolvedValue({ ok: false } as Response);
    renderCard(response());

    await userEvent.click(screen.getByRole("button", { name: /force a re-download/i }));
    await userEvent.click(screen.getByRole("button", { name: /clear it and search again/i }));

    await waitFor(() => expect(toasts.error).toHaveBeenCalled());
    expect(screen.getByText(/does not undo it/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Clear it and search again" })).toBeEnabled();
  });

  it("keeps the question open while the force is still running", async () => {
    vi.mocked(authedFetch).mockReturnValue(new Promise(() => {}));
    renderCard(response());

    await userEvent.click(screen.getByRole("button", { name: /force a re-download/i }));
    await userEvent.click(screen.getByRole("button", { name: /clear it and search again/i }));
    await userEvent.keyboard("{Escape}");

    expect(screen.getByText(/does not undo it/)).toBeInTheDocument();
  });
});
