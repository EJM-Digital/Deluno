import { render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

const toastError = vi.hoisted(() => vi.fn());
vi.mock("../components/shell/toaster", () => ({ toast: { error: toastError } }));

import { moveRefusalLines, toastMoveRefusals } from "./move-refusals";

describe("what the move says about titles it would not move (#1078)", () => {
  beforeEach(() => {
    toastError.mockReset();
  });

  it("gives each refused title its own reason", () => {
    expect(moveRefusalLines({ moveFailures: ["Arrival"], moveFailureReasons: ["Arrival: the folder is in use."] })).toEqual(["Arrival: the folder is in use."]);
  });

  it("names the title alone when an older host sent no reason, so a refusal is never just a count", () => {
    expect(moveRefusalLines({ moveFailures: ["Arrival", "Sintel"] })).toEqual([
      "Arrival: Deluno did not move it, so it stayed where it was.",
      "Sintel: Deluno did not move it, so it stayed where it was."
    ]);
    expect(moveRefusalLines({ moveFailureReasons: [], moveFailures: ["Arrival"] })).toHaveLength(1);
  });

  it("has nothing to say when nothing was refused", () => {
    expect(moveRefusalLines({})).toEqual([]);
  });

  it("shows nothing for no refusals, and one toast that stays until dismissed for some", () => {
    toastMoveRefusals([]);
    expect(toastError).not.toHaveBeenCalled();

    toastMoveRefusals(["Arrival: the folder is in use.", "Sintel: no space left."]);
    expect(toastError).toHaveBeenCalledTimes(1);
    const [title, options] = toastError.mock.calls[0];
    expect(title).toBe("2 titles not moved");
    expect(options.duration).toBe(Infinity);

    render(options.description);
    expect(screen.getByRole("list", { name: "Titles Deluno did not move and why" })).toBeInTheDocument();
    expect(screen.getByText("Sintel: no space left.")).toBeVisible();
  });
});
