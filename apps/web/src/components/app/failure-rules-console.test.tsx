import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ImportFailureRule } from "../../lib/failure-reasons";
import { authedFetch } from "../../lib/use-auth";
import { FailureRulesConsole } from "./failure-rules-console";

vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../shell/toaster", () => ({
  toast: { success: vi.fn(), warning: vi.fn(), error: vi.fn() }
}));
const { toast: toasts } = (await import("../shell/toaster")) as unknown as {
  toast: { success: ReturnType<typeof vi.fn>; error: ReturnType<typeof vi.fn> };
};

/**
 * The screen that makes every one of DESIGN-007's decisions a default rather
 * than law.
 *
 * <p>James, having settled all sixteen: <i>"I think all these things we decided
 * need to have configuration toggles to set them on and off in a management /
 * blocklist console."</i> The right harshness depends on the library — somebody
 * on a fast line with spare disk wants it strict; somebody on a flaky share
 * does not.</p>
 */
describe("the failure rules", () => {
  beforeEach(() => vi.clearAllMocks());

  /// Seventeen codes in a flat list is an inventory. Grouped by whose fault it
  /// was, it reads as the argument the answers were made from.
  it("groups the failures by whose fault they were", () => {
    render(
      <FailureRulesConsole
        rules={[rule(), rule({ reasonCode: "missingSource", category: "yourSetup", decision: "Never", defaultDecision: "Never" })]}
        onChanged={vi.fn()}
      />
    );

    expect(screen.getByText("The file was wrong")).toBeInTheDocument();
    expect(screen.getByText("Your setup, not the release")).toBeInTheDocument();
    expect(screen.getByText("No video in the file")).toBeInTheDocument();
    expect(screen.getByText("The client said done, and the file was gone")).toBeInTheDocument();
  });

  /// A heading with nothing under it is a promise the screen does not keep.
  it("leaves out a group with nothing in it", () => {
    render(<FailureRulesConsole rules={[rule()]} onChanged={vi.fn()} />);

    expect(screen.queryByText("Not a failure at all")).not.toBeInTheDocument();
  });

  it("saves a stricter answer than the one Deluno ships with", async () => {
    vi.mocked(authedFetch).mockResolvedValue({ ok: true } as Response);
    const onChanged = vi.fn();

    render(
      <FailureRulesConsole
        rules={[rule({ reasonCode: "missingSource", category: "yourSetup", decision: "Never", defaultDecision: "Never" })]}
        onChanged={onChanged}
      />
    );
    await userEvent.click(screen.getByRole("radio", { name: "At once" }));

    expect(authedFetch).toHaveBeenCalledWith("/api/failure-rules/missingSource", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ decision: "Immediately" })
    });
    expect(onChanged).toHaveBeenCalled();
  });

  /**
   * The one that matters. Choosing the shipped answer again deletes the
   * override rather than storing it — if it stored it, a later change to what
   * Deluno ships with would never reach anybody who had ever pressed reset, and
   * the table would be frozen by the act of restoring it.
   */
  it("forgets the override rather than writing today's default down", async () => {
    vi.mocked(authedFetch).mockResolvedValue({ ok: true } as Response);

    render(
      <FailureRulesConsole
        rules={[rule({ decision: "Never", defaultDecision: "Immediately", isOverridden: true })]}
        onChanged={vi.fn()}
      />
    );
    await userEvent.click(screen.getByRole("radio", { name: "At once" }));

    expect(authedFetch).toHaveBeenCalledWith("/api/failure-rules/noVideoStream", { method: "DELETE" });
  });

  /// "Back to default" means nothing if the reader has to guess what the
  /// default was.
  it("says what Deluno's own answer was, on a row you have changed", () => {
    render(
      <FailureRulesConsole
        rules={[rule({ decision: "Never", defaultDecision: "Immediately", isOverridden: true })]}
        onChanged={vi.fn()}
      />
    );

    expect(screen.getByText('Deluno ships with "At once"')).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /put no video in the file back/i })).toBeInTheDocument();
  });

  it("offers no way back on a row nobody has changed", () => {
    render(<FailureRulesConsole rules={[rule()]} onChanged={vi.fn()} />);

    expect(screen.getByText("Deluno's own answer")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /back to/i })).not.toBeInTheDocument();
  });

  /// The confirmation says what will now happen, in the words of the thing that
  /// will happen — not "saved".
  it("confirms in terms of what Deluno will now do", async () => {
    vi.mocked(authedFetch).mockResolvedValue({ ok: true } as Response);

    render(
      <FailureRulesConsole
        rules={[rule({ reasonCode: "importFailed", category: "cannotSay", decision: "AfterOneRetry", defaultDecision: "AfterOneRetry" })]}
        onChanged={vi.fn()}
      />
    );
    await userEvent.click(screen.getByRole("radio", { name: "Ask me" }));

    expect(toasts.success).toHaveBeenCalledWith("Failed, with no reason recorded — Deluno now asks you first.");
  });

  it("says nothing was changed when the request fails", async () => {
    vi.mocked(authedFetch).mockResolvedValue({ ok: false } as Response);
    const onChanged = vi.fn();

    render(<FailureRulesConsole rules={[rule()]} onChanged={onChanged} />);
    await userEvent.click(screen.getByRole("radio", { name: "Never" }));

    expect(toasts.error).toHaveBeenCalled();
    expect(onChanged).not.toHaveBeenCalled();
  });

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
