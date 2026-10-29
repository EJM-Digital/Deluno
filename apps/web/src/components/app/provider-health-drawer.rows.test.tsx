import { fireEvent, render, screen, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import type { IntegrationFailure } from "../../lib/api";
import { ProviderHealthDrawer, type ProviderHealthSelection } from "./provider-health-drawer";

/** The provider health drawer, field by field (#1329): the health result, the wording for each retry state, and Close. */

const failure = (retryState: string, over: Partial<IntegrationFailure> = {}): IntegrationFailure => ({
  serviceType: "indexer", serviceId: "i1", serviceName: "Alpha", operation: "search.query", kind: "Unavailable", retryState,
  message: "It did not answer.", code: null, httpStatus: null, upstreamDetail: null, externalId: null, retryAfterUtc: null, attempts: 1,
  isTransient: true, legacyCategory: "connection", summary: "Alpha did not answer.", nextAction: "Check Alpha.", ...over
});

const selection = (over: Partial<ProviderHealthSelection["provider"]> = {}): ProviderHealthSelection => ({
  kind: "indexer",
  provider: { id: "i1", name: "Alpha", protocol: "torznab", healthStatus: "healthy", lastHealthMessage: null, ...over }
});

describe("Health result", () => {
  it("shows when it was last checked, the response time and the message it reported", () => {
    render(<ProviderHealthDrawer selection={selection({ lastHealthTestUtc: "2026-09-01T05:00:00Z", lastHealthLatencyMs: 240, lastHealthMessage: "All good." })} onClose={vi.fn()} />);

    const result = screen.getByText("Health result").parentElement!.parentElement as HTMLElement;
    expect(within(result).getByText("Last checked")).toBeInTheDocument();
    expect(within(result).queryByText("Not tested")).not.toBeInTheDocument();
    expect(within(result).getByText("240 ms")).toBeInTheDocument();
    expect(within(result).getByText("Reported message")).toBeInTheDocument();
    expect(within(result).getByText("All good.")).toBeInTheDocument();
    expect(within(result).getByText("healthy")).toBeInTheDocument();
  });

  it("says 'Not tested' with no response time or message when it has never been checked", () => {
    render(<ProviderHealthDrawer selection={selection()} onClose={vi.fn()} />);

    expect(screen.getByText("Not tested")).toBeInTheDocument();
    expect(screen.queryByText("Response time")).not.toBeInTheDocument();
    expect(screen.queryByText("Reported message")).not.toBeInTheDocument();
  });
});

describe("What happens next", () => {
  it("says the last check did not record a typed failure when it was tested and reported nothing, and that nothing is recorded when it was never tested", () => {
    const first = render(<ProviderHealthDrawer selection={selection({ lastHealthTestUtc: "2026-09-01T05:00:00Z" })} onClose={vi.fn()} />);
    expect(screen.getByText("The last check did not record a typed failure.")).toBeInTheDocument();
    first.unmount();

    render(<ProviderHealthDrawer selection={selection()} onClose={vi.fn()} />);
    expect(screen.getByText("Deluno has not recorded a test result for this provider yet.")).toBeInTheDocument();
    expect(screen.queryByText("The last check did not record a typed failure.")).not.toBeInTheDocument();
  });

  it.each([
    ["Retrying", "Retrying automatically", "Deluno is retrying this request automatically."],
    ["RetryScheduled", "Retry scheduled", "Deluno has scheduled an automatic retry."],
    ["CircuitOpen", "Paused after repeated failures", "Deluno paused automatic requests after repeated failures to avoid making the provider problem worse."],
    ["ManualAction", "Needs your action", "Deluno will not retry this terminal result automatically."],
    ["NotRetryable", "Will not retry automatically", "Deluno will not retry this action automatically."],
    ["Mystery", "Mystery", "Deluno has not reported whether this action will retry automatically."]
  ])("for %s it reads '%s' and explains '%s'", (retryState, label, sentence) => {
    render(<ProviderHealthDrawer selection={selection({ lastHealthFailure: failure(retryState) })} onClose={vi.fn()} />);

    expect(screen.getByText(label)).toBeInTheDocument();
    expect(screen.getByText(sentence)).toBeInTheDocument();
    expect(screen.getByText("Check Alpha.")).toBeInTheDocument();
  });

  it("a scheduled retry with a next eligible time says it will retry when that time arrives", () => {
    render(<ProviderHealthDrawer selection={selection({ lastHealthFailure: failure("RetryScheduled", { retryAfterUtc: "2026-09-01T05:05:00Z" }) })} onClose={vi.fn()} />);

    expect(screen.getByText("Deluno will retry automatically when the next eligible time arrives.")).toBeInTheDocument();
    expect(screen.getByText("Next eligible")).toBeInTheDocument();
  });
});

describe("Close", () => {
  it("the Close button hands the close to the screen", () => {
    const onClose = vi.fn();
    render(<ProviderHealthDrawer selection={selection()} onClose={onClose} />);

    const buttons = screen.getAllByRole("button", { name: "Close" });
    fireEvent.click(buttons[buttons.length - 1]!);
    expect(onClose).toHaveBeenCalledTimes(1);
  });
});
