import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { PlatformSettingsSnapshot } from "../../lib/api";
import { authedFetch } from "../../lib/use-auth";
import { FailureSchedulesConsole } from "./failure-schedules-console";

vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../shell/toaster", () => ({
  toast: { success: vi.fn(), warning: vi.fn(), error: vi.fn() }
}));
const { toast: toasts } = (await import("../shell/toaster")) as unknown as {
  toast: { success: ReturnType<typeof vi.fn>; error: ReturnType<typeof vi.fn> };
};

/**
 * The third section of the failure and blocklist console: how often Deluno
 * looks, and how long you have to change your mind about what it took.
 *
 * <p>The file check had been declared configurable since the day it was written
 * and was not — the System screen printed "6h · configured" beside it while
 * nothing configured anything.</p>
 */
describe("how often Deluno checks", () => {
  beforeEach(() => vi.clearAllMocks());

  it("saves a new cadence for the file check", async () => {
    vi.mocked(authedFetch).mockResolvedValue({ ok: true } as Response);
    const onChanged = vi.fn();

    render(<FailureSchedulesConsole settings={settings()} recycleBin={bin()} onChanged={onChanged} />);
    await userEvent.selectOptions(screen.getByLabelText(/check my files every/i), "24");

    expect(authedFetch).toHaveBeenCalledWith("/api/settings", {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ libraryFileCheckHours: 24 })
    });
    expect(onChanged).toHaveBeenCalled();
  });

  /// Said in terms of what will now happen, not "saved".
  it("confirms in terms of what Deluno will now do", async () => {
    vi.mocked(authedFetch).mockResolvedValue({ ok: true } as Response);

    render(<FailureSchedulesConsole settings={settings()} recycleBin={bin()} onChanged={vi.fn()} />);
    await userEvent.selectOptions(screen.getByLabelText(/check my files every/i), "1");

    expect(toasts.success).toHaveBeenCalledWith("Deluno will check your files every hour.");
  });

  /**
   * The server clamps to 1–168 hours, and so does this. Saving a number Deluno
   * will not run at and then showing it back would be worse than refusing it.
   */
  it("does not save a cadence the scheduler would not accept", async () => {
    render(<FailureSchedulesConsole settings={settings()} recycleBin={bin()} onChanged={vi.fn()} />);

    await userEvent.selectOptions(screen.getByLabelText(/check my files every/i), "__custom");
    await userEvent.type(screen.getByPlaceholderText("1–168 hours"), "0");

    expect(authedFetch).not.toHaveBeenCalled();
  });

  it("keeps the recycle bin's retention here too, because it is the same question", async () => {
    vi.mocked(authedFetch).mockResolvedValue({ ok: true } as Response);

    render(<FailureSchedulesConsole settings={settings()} recycleBin={bin()} onChanged={vi.fn()} />);
    await userEvent.selectOptions(screen.getByLabelText(/keep removed files for/i), "30");

    expect(authedFetch).toHaveBeenCalledWith(
      "/api/recycle-bin/settings",
      expect.objectContaining({ body: JSON.stringify({ retentionDays: 30, maxSizeMb: 10_000 }) })
    );
    expect(toasts.success).toHaveBeenCalledWith(expect.stringContaining("30 days to change your mind"));
  });

  it("says nothing was changed when the request fails", async () => {
    vi.mocked(authedFetch).mockResolvedValue({ ok: false } as Response);
    const onChanged = vi.fn();

    render(<FailureSchedulesConsole settings={settings()} recycleBin={bin()} onChanged={onChanged} />);
    await userEvent.selectOptions(screen.getByLabelText(/check my files every/i), "24");

    expect(toasts.error).toHaveBeenCalled();
    expect(onChanged).not.toHaveBeenCalled();
  });

  function settings(): PlatformSettingsSnapshot {
    return { libraryFileCheckHours: 6 } as PlatformSettingsSnapshot;
  }

  function bin() {
    return { retentionDays: 7, maxSizeMb: 10_000 };
  }
});
