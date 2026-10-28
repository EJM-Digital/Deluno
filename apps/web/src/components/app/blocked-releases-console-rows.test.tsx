import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { BlockedRelease, PlatformSettingsSnapshot } from "../../lib/api";
import type { ImportFailureRule } from "../../lib/failure-reasons";
import { authedFetch } from "../../lib/use-auth";
import { BlockedReleasesConsole } from "./blocked-releases-console";

vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../shell/toaster", () => ({
  toast: { success: vi.fn(), warning: vi.fn(), error: vi.fn() }
}));
const { toast: toasts } = (await import("../shell/toaster")) as unknown as {
  toast: { success: ReturnType<typeof vi.fn>; warning: ReturnType<typeof vi.fn>; error: ReturnType<typeof vi.fn> };
};

/**
 * The blocklist tab's list and its two folded sections, for what a fresh server cannot show: refusals with different reasons and
 * dates, a cleanup that comes back empty or fails, a save that is refused. The component is the real one; the answers are put in.
 */
describe("the blocklist list", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    window.localStorage.clear();
  });

  it("the columns sort by release, why and refused date", async () => {
    const releases = [
      release({ id: "a", releaseName: "Charlie.2020", reasonCode: "noVideoStream", blockedUtc: "2026-09-02T12:00:00Z" }),
      release({ id: "b", releaseName: "Alpha.2020", reasonCode: "likelySample", blockedUtc: "2026-09-03T12:00:00Z" }),
      release({ id: "c", releaseName: "Bravo.2020", reasonCode: "manualFailure", blockedUtc: "2026-09-01T12:00:00Z" })
    ];
    render(<BlockedReleasesConsole {...schedules()} releases={releases} rules={[]} onChanged={vi.fn()} />);
    const order = () => screen.getAllByRole("row").map((row) => row.textContent?.match(/Alpha|Bravo|Charlie/)?.[0]).filter(Boolean);
    const sortBy = async (name: string) => {
      const header = screen.getAllByRole("columnheader").find((candidate) => candidate.textContent === name)!;
      await userEvent.click(within(header).getByRole("button"));
      return header;
    };

    await sortBy("Release");
    expect(order()).toEqual(["Alpha", "Bravo", "Charlie"]);
    await sortBy("Release");
    expect(order()).toEqual(["Charlie", "Bravo", "Alpha"]);
    await sortBy("Refused");
    expect(order()).toEqual(["Bravo", "Charlie", "Alpha"]); // 1 Sep, 2 Sep, 3 Sep
    const why = await sortBy("Why");
    expect(why).toHaveAttribute("aria-sort", "ascending");
    // By the words shown: "A sample, not the film" (Alpha), the raw code "manualFailure" (Bravo), "No video in the file" (Charlie).
    expect(order()).toEqual(["Alpha", "Bravo", "Charlie"]);
  });

  it("Clean up now says there was nothing left when the client holds nothing", async () => {
    vi.mocked(authedFetch).mockResolvedValue({ ok: true, json: async () => ({ outcome: "nothingToClear" }) } as Response);
    render(<BlockedReleasesConsole {...schedules()} releases={[release()]} rules={[]} onChanged={vi.fn()} />);
    await userEvent.click(screen.getByRole("button", { name: /clean up now/i }));
    expect(toasts.warning).toHaveBeenCalledWith("There was nothing left to clear.");
    expect(toasts.error).not.toHaveBeenCalled();
  });

  it("Clean up now toasts that it could not be cleared up when the request fails, and does not reload", async () => {
    vi.mocked(authedFetch).mockResolvedValue({ ok: false } as Response);
    const onChanged = vi.fn();
    render(<BlockedReleasesConsole {...schedules()} releases={[release()]} rules={[]} onChanged={onChanged} />);
    await userEvent.click(screen.getByRole("button", { name: /clean up now/i }));
    expect(toasts.error).toHaveBeenCalledWith("That could not be cleared up.");
    expect(onChanged).not.toHaveBeenCalled();
  });

  it("Clean up all warns that nothing was cleared and how many are still sharing, or that there was nothing left", async () => {
    vi.mocked(authedFetch).mockResolvedValue({ ok: true, json: async () => ({ cleared: 0, waitingOnSharing: 2, failed: 0 }) } as Response);
    const first = render(<BlockedReleasesConsole {...schedules()} releases={[release()]} rules={[]} onChanged={vi.fn()} />);
    await userEvent.click(screen.getByRole("button", { name: "Clean up all" }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Clean up all" }));
    await waitFor(() => expect(toasts.warning).toHaveBeenCalledWith("Nothing was cleared — 2 releases still sharing."));
    first.unmount();

    toasts.warning.mockClear();
    vi.mocked(authedFetch).mockResolvedValue({ ok: true, json: async () => ({ cleared: 0, waitingOnSharing: 1, failed: 0 }) } as Response);
    const second = render(<BlockedReleasesConsole {...schedules()} releases={[release()]} rules={[]} onChanged={vi.fn()} />);
    await userEvent.click(screen.getByRole("button", { name: "Clean up all" }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Clean up all" }));
    await waitFor(() => expect(toasts.warning).toHaveBeenCalledWith("Nothing was cleared — 1 release still sharing."));
    second.unmount();

    toasts.warning.mockClear();
    vi.mocked(authedFetch).mockResolvedValue({ ok: true, json: async () => ({ cleared: 0, waitingOnSharing: 0, failed: 0 }) } as Response);
    render(<BlockedReleasesConsole {...schedules()} releases={[release()]} rules={[]} onChanged={vi.fn()} />);
    await userEvent.click(screen.getByRole("button", { name: "Clean up all" }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Clean up all" }));
    await waitFor(() => expect(toasts.warning).toHaveBeenCalledWith("There was nothing left to clear."));
  });

  it("Clean up all toasts that the blocklist could not be cleared up when the request fails", async () => {
    vi.mocked(authedFetch).mockResolvedValue({ ok: false } as Response);
    render(<BlockedReleasesConsole {...schedules()} releases={[release()]} rules={[]} onChanged={vi.fn()} />);
    await userEvent.click(screen.getByRole("button", { name: "Clean up all" }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Clean up all" }));
    await waitFor(() => expect(toasts.error).toHaveBeenCalledWith("The blocklist could not be cleared up."));
  });
});

describe("the blocklist's folded sections", () => {
  beforeEach(() => vi.clearAllMocks());

  it("the rules and schedules disclosures open and close", async () => {
    render(<BlockedReleasesConsole {...schedules()} releases={[]} rules={[rule()]} onChanged={vi.fn()} />);
    const rules = screen.getByRole("button", { name: /What Deluno does when an import fails/ });
    const schedulesToggle = screen.getByRole("button", { name: /How often Deluno checks, and what it keeps/ });
    expect(rules).toHaveAttribute("aria-expanded", "false");
    expect(schedulesToggle).toHaveAttribute("aria-expanded", "false");
    expect(screen.queryByRole("radiogroup")).toBeNull();
    expect(screen.queryByLabelText(/check my files every/i)).toBeNull();

    await userEvent.click(rules);
    expect(rules).toHaveAttribute("aria-expanded", "true");
    expect(screen.getAllByRole("radiogroup").length).toBeGreaterThan(0);
    await userEvent.click(schedulesToggle);
    expect(schedulesToggle).toHaveAttribute("aria-expanded", "true");
    expect(screen.getByLabelText(/check my files every/i)).toBeInTheDocument();

    await userEvent.click(rules);
    expect(rules).toHaveAttribute("aria-expanded", "false");
    expect(screen.queryByRole("radiogroup")).toBeNull();
    await userEvent.click(schedulesToggle);
    expect(screen.queryByLabelText(/check my files every/i)).toBeNull();
  });

  it("the schedules summary follows the settings: hour, day, week or N hours, and a day or N days kept", () => {
    const cases: Array<[number, number, string]> = [
      [1, 1, "Files checked every hour · removed files kept for a day"],
      [24, 30, "Files checked every day · removed files kept for 30 days"],
      [168, 90, "Files checked every week · removed files kept for 90 days"],
      [6, 7, "Files checked every 6 hours · removed files kept for 7 days"]
    ];
    for (const [fileCheckHours, retentionDays, summary] of cases) {
      const { unmount } = render(
        <BlockedReleasesConsole releases={[]} rules={[]} settings={{ libraryFileCheckHours: fileCheckHours } as PlatformSettingsSnapshot} recycleBin={{ retentionDays, maxSizeMb: 10_000 }} onChanged={vi.fn()} />
      );
      expect(screen.getByText(summary)).toBeInTheDocument();
      unmount();
    }
  });

  it("a recycle-bin retention below one day is not saved", async () => {
    render(<BlockedReleasesConsole {...schedules()} releases={[]} rules={[]} onChanged={vi.fn()} />);
    await userEvent.click(screen.getByRole("button", { name: /How often Deluno checks, and what it keeps/ }));
    await userEvent.selectOptions(screen.getByLabelText(/keep removed files for/i), "__custom");
    await userEvent.type(screen.getByPlaceholderText("Days"), "0");
    expect(authedFetch).not.toHaveBeenCalled();
    expect(toasts.success).not.toHaveBeenCalled();
  });

  it("a schedule that cannot be saved toasts so, for the file check and for the retention, and does not reload", async () => {
    vi.mocked(authedFetch).mockResolvedValue({ ok: false } as Response);
    const onChanged = vi.fn();
    render(<BlockedReleasesConsole {...schedules()} releases={[]} rules={[]} onChanged={onChanged} />);
    await userEvent.click(screen.getByRole("button", { name: /How often Deluno checks, and what it keeps/ }));
    await userEvent.selectOptions(screen.getByLabelText(/check my files every/i), "24");
    await waitFor(() => expect(toasts.error).toHaveBeenCalledWith("That schedule could not be saved."));
    await userEvent.selectOptions(screen.getByLabelText(/keep removed files for/i), "30");
    await waitFor(() => expect(toasts.error).toHaveBeenCalledWith("That retention could not be saved."));
    expect(onChanged).not.toHaveBeenCalled();
  });

  it("a failure rule's answer cannot be changed again while the first change is still being saved", async () => {
    let finish: (value: Response) => void = () => undefined;
    vi.mocked(authedFetch).mockImplementation(() => new Promise<Response>((resolve) => { finish = resolve; }));
    render(<BlockedReleasesConsole {...schedules()} releases={[]} rules={[rule()]} onChanged={vi.fn()} />);
    await userEvent.click(screen.getByRole("button", { name: /What Deluno does when an import fails/ }));
    const group = screen.getAllByRole("radiogroup")[0];
    const radios = within(group).getAllByRole("radio");
    expect(radios.every((radio) => !(radio as HTMLButtonElement).disabled)).toBe(true);

    await userEvent.click(within(group).getByRole("radio", { name: "Never" }));
    await waitFor(() => expect(within(group).getAllByRole("radio").every((radio) => (radio as HTMLButtonElement).disabled || radio.getAttribute("aria-disabled") === "true")).toBe(true));
    finish({ ok: true } as Response);
    await waitFor(() => expect(within(group).getAllByRole("radio").some((radio) => !(radio as HTMLButtonElement).disabled && radio.getAttribute("aria-disabled") !== "true")).toBe(true));
  });
});

function schedules() {
  return { settings: { libraryFileCheckHours: 6 } as PlatformSettingsSnapshot, recycleBin: { retentionDays: 7, maxSizeMb: 10_000 } };
}

function release(overrides: Partial<BlockedRelease> = {}): BlockedRelease {
  return {
    id: "block-1", releaseKey: "arrival.2016.2160p|nebula", releaseName: "Arrival.2016.2160p", indexerName: "Nebula", mediaType: "movies", entityId: "movie-1",
    title: "Arrival", reasonCode: "noVideoStream", reason: "No video stream was detected in this file.", torrentHashOrItemId: "hash-1", downloadClientId: "qbittorrent-main",
    downloadClientName: "qBittorrent", blockedUtc: "2026-09-05T12:00:00Z", state: "refused", ...overrides
  };
}

function rule(overrides: Partial<ImportFailureRule> = {}): ImportFailureRule {
  return { reasonCode: "noVideoStream", category: "badFile", decision: "Immediately", defaultDecision: "Immediately", isOverridden: false, ...overrides };
}
