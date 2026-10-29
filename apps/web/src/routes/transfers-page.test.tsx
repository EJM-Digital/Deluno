import { render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";

const activityLoader = vi.hoisted(() => vi.fn(() => ({ cached: true })));
const queueLoader = vi.hoisted(() => vi.fn(() => ({ cached: true })));
vi.mock("./activity-page", () => ({ activityLoader, ActivityPage: () => <p>the history</p> }));
vi.mock("./queue-screen", () => ({ queueLoader, QueuePage: () => <p>the live downloads</p> }));

const { LegacyActivityRedirect, TransfersPage, transfersLoader } = await import("./transfers-page");

function Where() {
  const location = useLocation();
  return <output data-testid="where">{location.pathname}{location.search}</output>;
}

function open(address: string) {
  return render(
    <MemoryRouter initialEntries={[address]}>
      <Routes>
        <Route path="/queue" element={<TransfersPage />} />
        <Route path="/activity" element={<LegacyActivityRedirect />} />
      </Routes>
      <Where />
    </MemoryRouter>
  );
}

describe("Transfers is one page with three views (#1426)", () => {
  it.each([
    ["/queue", "the live downloads"],
    ["/queue?view=handoffs", "the live downloads"],
    ["/queue?manualImportPath=D%3A%2Fx", "the live downloads"],
    ["/queue?view=history", "the history"],
    ["/queue?view=history&status=failed", "the history"]
  ])("%s shows %s", (address, text) => {
    open(address);
    expect(screen.getByText(text)).toBeInTheDocument();
  });

  it.each([
    ["/activity", "/queue?view=history"],
    ["/activity?status=failed", "/queue?view=history&status=failed"],
    ["/activity?section=imports", "/queue?view=history&section=imports"],
    ["/activity?type=turned-down&since=today&reason=language", "/queue?view=history&type=turned-down&since=today&reason=language"],
    ["/activity?event=evt-1&since=2026-10-01&until=2026-10-01", "/queue?view=history&event=evt-1&since=2026-10-01&until=2026-10-01"]
  ])("the old address %s opens the history at %s", (address, target) => {
    open(address);
    expect(screen.getByTestId("where").textContent).toBe(target);
    expect(screen.getByText("the history")).toBeInTheDocument();
  });

  it("starts only the reads of the view that is asked for", () => {
    activityLoader.mockClear();
    queueLoader.mockClear();
    transfersLoader({ request: new Request("http://localhost/queue?view=history") });
    expect(activityLoader).toHaveBeenCalledTimes(1);
    expect(queueLoader).not.toHaveBeenCalled();

    transfersLoader({ request: new Request("http://localhost/queue?view=handoffs") });
    transfersLoader({ request: new Request("http://localhost/queue") });
    transfersLoader();
    expect(queueLoader).toHaveBeenCalledTimes(3);
    expect(activityLoader).toHaveBeenCalledTimes(1);
  });
});
