import { beforeEach, describe, expect, it, vi } from "vitest";

const mocks = vi.hoisted(() => ({
  fetchJson: vi.fn(),
  toast: { success: vi.fn(), error: vi.fn() }
}));
vi.mock("../../lib/api", () => ({ fetchJson: mocks.fetchJson }));
vi.mock("../shell/toaster", () => ({ toast: mocks.toast }));

const { showRemovalToast } = await import("./removal-toast");

beforeEach(() => {
  mocks.fetchJson.mockReset();
  mocks.toast.success.mockReset();
  mocks.toast.error.mockReset();
});

function restoreAction(metadata: Record<string, string | null> | null | undefined, navigate = vi.fn()) {
  showRemovalToast("Dune", metadata, navigate);
  return mocks.toast.success.mock.calls[0]![1].action.onClick as () => void;
}

describe("restoring a removed title from its toast", () => {
  it("offers Restore for 15 seconds, saying how long the files are kept", () => {
    restoreAction({ recycleBinItemIds: "r1", recycleBinRetentionDays: "30" });
    const options = mocks.toast.success.mock.calls[0]![1];
    expect(options.duration).toBe(15000);
    expect(options.description).toContain("30 days");
    expect(options.action.label).toBe("Restore");
  });

  it("restores a film and opens it", async () => {
    const navigate = vi.fn();
    const restore = restoreAction({ recycleBinItemIds: "r1" }, navigate);
    mocks.fetchJson.mockResolvedValueOnce({ success: true, message: "Back again.", titleRestored: true, titleId: "m1", titleEntityType: "movie" });
    restore();
    await vi.waitFor(() => expect(navigate).toHaveBeenCalledWith("/movies/m1"));
    expect(mocks.fetchJson).toHaveBeenCalledWith("/api/recycle-bin/r1/restore", { method: "POST" });
    expect(mocks.toast.success).toHaveBeenLastCalledWith("Dune restored", { description: "Back again." });
  });

  it("restores a show, opens only the first title it brought back, and restores every item", async () => {
    const navigate = vi.fn();
    const restore = restoreAction({ recycleBinItemIds: "r1,r2" }, navigate);
    mocks.fetchJson
      .mockResolvedValueOnce({ success: true, message: "One.", titleRestored: true, titleId: "s1", titleEntityType: "series" })
      .mockResolvedValueOnce({ success: true, message: "Two.", titleRestored: true, titleId: "s2", titleEntityType: "series" });
    restore();
    await vi.waitFor(() => expect(navigate).toHaveBeenCalledWith("/tv/s1"));
    expect(navigate).toHaveBeenCalledTimes(1);
    expect(mocks.fetchJson).toHaveBeenCalledTimes(2);
  });

  it("restores files without opening a page when no title came back", async () => {
    const navigate = vi.fn();
    const restore = restoreAction({ recycleBinItemIds: "r1" }, navigate);
    mocks.fetchJson.mockResolvedValueOnce({ success: true, message: null, titleRestored: false });
    restore();
    await vi.waitFor(() => expect(mocks.toast.success).toHaveBeenLastCalledWith("Dune restored", { description: undefined }));
    expect(navigate).not.toHaveBeenCalled();
  });

  it("reports the reason when an item could not be restored and opens nothing", async () => {
    const navigate = vi.fn();
    const restore = restoreAction({ recycleBinItemIds: "r1,r2" }, navigate);
    mocks.fetchJson
      .mockResolvedValueOnce({ success: true, message: "ok", titleRestored: true, titleId: "m1", titleEntityType: "movie" })
      .mockResolvedValueOnce({ success: false, message: "The folder is missing." });
    restore();
    await vi.waitFor(() => expect(mocks.toast.error).toHaveBeenCalledWith("Dune could not be restored", { description: "The folder is missing." }));
    expect(navigate).not.toHaveBeenCalled();
  });

  it("says plainly that a title was removed when nothing is in the metadata", () => {
    showRemovalToast("Dune", null, vi.fn());
    expect(mocks.toast.success).toHaveBeenCalledExactlyOnceWith("Dune removed from Deluno");
  });
});
