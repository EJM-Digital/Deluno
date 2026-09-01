import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import type { ProviderHealthSelection } from "./provider-health-drawer";
import { ProviderHealthDrawer } from "./provider-health-drawer";

const retryingClient: ProviderHealthSelection = {
  kind: "download-client",
  provider: {
    id: "client-1",
    name: "qBittorrent lab",
    protocol: "qbittorrent",
    healthStatus: "unreachable",
    lastHealthMessage: "Deluno could not contact qBittorrent.",
    lastHealthTestUtc: "2026-09-01T05:00:00Z",
    lastHealthLatencyMs: 1200,
    lastHealthFailure: {
      serviceType: "download-client",
      serviceId: "client-1",
      serviceName: "qBittorrent lab",
      operation: "queue.refresh",
      kind: "Unavailable",
      retryState: "RetryScheduled",
      message: "The client refused the connection.",
      code: "ECONNREFUSED",
      httpStatus: null,
      upstreamDetail: "Connection refused by 192.168.1.142:8080.",
      externalId: null,
      retryAfterUtc: "2026-09-01T05:05:00Z",
      attempts: 2,
      isTransient: true,
      legacyCategory: "connection",
      summary: "qBittorrent did not accept Deluno's connection.",
      nextAction: "Check that qBittorrent is running and reachable, then test the connection again."
    }
  }
};

describe("ProviderHealthDrawer", () => {
  it("keeps a provider failure attributable and actionable from Health", () => {
    render(<ProviderHealthDrawer selection={retryingClient} onClose={() => undefined} />);

    expect(screen.getByRole("dialog")).toHaveAccessibleName("qBittorrent lab");
    expect(screen.queryByText("Search source", { exact: false })).not.toBeInTheDocument();
    expect(screen.getByText("Download client · qbittorrent")).toBeVisible();
    expect(screen.getByText("unreachable")).toBeVisible();
    expect(screen.getByText("qBittorrent did not accept Deluno's connection.")).toBeVisible();
    expect(screen.getByText(/Connection refused by 10\.1\.1\.142:8080\./)).toBeVisible();
    expect(screen.getByText("Retry scheduled")).toBeVisible();
    expect(screen.getByText("Deluno will retry automatically when the next eligible time arrives.")).toBeVisible();
    expect(screen.getByText("Check that qBittorrent is running and reachable, then test the connection again.")).toBeVisible();
    expect(screen.getByText("Service")).toBeVisible();
    expect(screen.getByText("Action")).toBeVisible();
    expect(screen.getByText("Queue refresh")).toBeVisible();
    expect(screen.getByText("Next eligible")).toBeVisible();
  });

  it("does not imply a scheduled retry for a terminal manual-action failure", () => {
    render(
      <ProviderHealthDrawer
        selection={{
          ...retryingClient,
          provider: {
            ...retryingClient.provider,
            lastHealthFailure: {
              ...retryingClient.provider.lastHealthFailure!,
              retryState: "ManualAction",
              retryAfterUtc: null
            }
          }
        }}
        onClose={() => undefined}
      />
    );

    expect(screen.getByText("Needs your action")).toBeVisible();
    expect(screen.getByText("Deluno will not retry this terminal result automatically.")).toBeVisible();
    expect(screen.queryByText("Next eligible")).not.toBeInTheDocument();
  });

  it("does not call a completed check untested when it has no typed failure", () => {
    render(
      <ProviderHealthDrawer
        selection={{
          ...retryingClient,
          provider: {
            ...retryingClient.provider,
            healthStatus: "healthy",
            lastHealthMessage: null,
            lastHealthFailure: null
          }
        }}
        onClose={() => undefined}
      />
    );

    expect(screen.getByText("The last check did not record a typed failure.")).toBeVisible();
    expect(screen.queryByText("Why it needs attention")).not.toBeInTheDocument();
  });
});
