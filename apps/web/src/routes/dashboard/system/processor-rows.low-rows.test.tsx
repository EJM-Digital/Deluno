import { cleanup, render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it } from "vitest";
import { ConnectionsCard, type ConnectionTesting } from "./connections-card";
import { buildConnections } from "./connections-model";

afterEach(cleanup);

const testing: ConnectionTesting = { results: new Map(), checking: new Set(), passed: new Set(), test: async () => {}, testAll: async () => {}, allBusy: false };
const input = { indexers: [], clients: [], clientRows: [], metadataMs: null, results: new Map(), now: Date.parse("2026-10-28T10:00:00Z") } as unknown as Parameters<typeof buildConnections>[0];

describe("System tab: processors in Connections (low rows)", () => {
  it("puts an unreachable processor in a red group as 'not answering', and a switched-off one as an off row with no check time or Test button", () => {
    const rows = buildConnections({
      ...input,
      connections: [
        { kind: "processor", id: "p1", name: "Cleaner", enabled: true, healthy: false, countToday: 0, busy: 0 },
        { kind: "processor", id: "p2", name: "Old cleaner", enabled: false, healthy: true, countToday: 0, busy: 0 }
      ]
    } as never);
    render(<MemoryRouter><ConnectionsCard rows={rows} testing={testing} now={Date.now()} /></MemoryRouter>);
    const card = screen.getByRole("region", { name: "Connections" });

    const group = card.querySelector(".sy-ct-grp")!;
    expect(group).toHaveClass("sy-red");
    const down = card.querySelector<HTMLElement>('[title^="Cleaner:"]')!;
    expect(down).toHaveClass("sy-down");
    expect(within(down).getByText("not answering")).toBeInTheDocument();
    const off = card.querySelector<HTMLElement>('[title^="Old cleaner:"]')!;
    expect(off).toHaveClass("sy-off");
    expect(within(off).getByText("switched off")).toBeInTheDocument();
    expect(within(off).getByText("—")).toBeInTheDocument();
    expect(within(off).queryByRole("button")).toBeNull();
    expect(within(down).queryByRole("button", { name: "Test Cleaner" })).not.toBeNull();
    expect(card).toHaveTextContent("1 down");
  });
});
