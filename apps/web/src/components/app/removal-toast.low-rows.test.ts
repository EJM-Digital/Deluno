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

describe("the toast after a title is removed (low rows)", () => {
  it("says '{title} removed from Deluno' with no Restore when no files went to the recycle bin", () => {
    showRemovalToast("Dune", { recycleBinItemIds: "" }, vi.fn());

    expect(mocks.toast.success).toHaveBeenCalledExactlyOnceWith("Dune removed from Deluno");
  });

  it("says '{title} could not be restored' and points to System, Recycle bin when the restore call fails or answers that it did not work", async () => {
    showRemovalToast("Dune", { recycleBinItemIds: "r1", recycleBinRetentionDays: "30" }, vi.fn());
    const restore = mocks.toast.success.mock.calls[0]![1].action.onClick as () => void;

    mocks.fetchJson.mockRejectedValueOnce(new Error("offline"));
    restore();
    await vi.waitFor(() => expect(mocks.toast.error).toHaveBeenCalledWith("Dune could not be restored", { description: "Open System, Recycle bin to restore it from there." }));

    mocks.toast.error.mockReset();
    mocks.fetchJson.mockResolvedValueOnce({ success: false, message: "The recycle entry is gone." });
    restore();
    await vi.waitFor(() => expect(mocks.toast.error).toHaveBeenCalledWith("Dune could not be restored", { description: "The recycle entry is gone." }));
  });
});
