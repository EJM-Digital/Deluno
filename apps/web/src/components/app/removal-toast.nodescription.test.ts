import { describe, expect, it, vi } from "vitest";

const toast = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn() }));
vi.mock("../../lib/api", () => ({ fetchJson: vi.fn() }));
vi.mock("../shell/toaster", () => ({ toast }));
vi.mock("../../lib/recycle-bin-words", () => ({
  describeRemovalOutcome: () => ({ itemIds: ["r1"], description: null })
}));

const { showRemovalToast } = await import("./removal-toast");

describe("the removal toast when the recycle bin gives no wording", () => {
  it("still offers Restore, with no description line", () => {
    showRemovalToast("Dune", {}, vi.fn());
    const options = toast.success.mock.calls[0]![1];
    expect(options.description).toBeUndefined();
    expect(options.action.label).toBe("Restore");
  });
});
