import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it } from "vitest";
import { StorageCard, type DriveView } from "../system/storage-card";
import { byStage, laneCounts } from "./journey";
import { NO_TITLES } from "./titles";
import { Journey } from "./track";


describe("Live journey: the Searching station", () => {
  it("opens the Automation page from the Searching station", () => {
    const grouped = byStage([]);
    render(
      <MemoryRouter>
        <Journey grouped={grouped} counts={laneCounts(grouped, undefined)} context={{ titles: NO_TITLES, arrivals: new Set(), indexers: 4 }} hasProcessor={false} usual={{}} by={Date.now()} lastArrival={null} />
      </MemoryRouter>
    );
    const station = screen.getByRole("link", { name: "Searching: 0" });
    expect(station).toHaveAttribute("href", "/search-cycles");
  });
});

describe("System: Storage header", () => {
  it("shows the free space of all drives as \"N free\" and links \"Storage →\" to Settings' libraries", () => {
    const drive = { key: "d", name: "Data", path: "D:", total: 2e12, free: 350e9, mine: 0, fullInDays: null, io: null, health: "ok", healthNote: null } as unknown as DriveView;
    render(<MemoryRouter><StorageCard drives={[drive]} /></MemoryRouter>);
    expect(screen.getByText(/^\d+(\.\d+)? \w+ free$/)).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Open storage settings" })).toHaveAttribute("href", "/settings/libraries");
  });
});
