import { cleanup, render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it } from "vitest";
import type { DashboardSummary } from "../../../lib/api/types/operations";
import { Band } from "./band";
import { Shelf } from "./shelf";
import { NO_TITLES } from "./titles";

afterEach(cleanup);

// The top band with nothing in its tables: no samples, no operations, no upcoming episodes.
function renderBand(summary: DashboardSummary | undefined) {
  return render(
    <MemoryRouter>
      <Band summary={summary} samples={[]} operations={[]} upcoming={[]} across />
    </MemoryRouter>
  );
}

const withCycle = (lastSearchCycle: unknown) => ({ automation: { lastSearchCycle } }) as unknown as DashboardSummary;

describe("Live top band: Speed and Working on now", () => {
  it("names the Speed label's link \"Transfers: Speed\" and sends it to Transfers", () => {
    renderBand(undefined);
    const link = screen.getByRole("link", { name: "Transfers: Speed" });
    expect(link).toHaveAttribute("href", "/queue");
  });

  it("says \"Nothing is running right now. No search has run yet.\" when nothing runs and no search ever ran", () => {
    renderBand(withCycle(null));
    expect(screen.getByText("Nothing is running right now. No search has run yet.")).toBeInTheDocument();
    expect(screen.queryByText(/The last search was/)).toBeNull();
  });
});

describe("Live your library shelf", () => {
  const shelf = (latest: boolean) => (
    <MemoryRouter>
      <Shelf items={[]} titles={NO_TITLES} newest={new Set()} heading="Nothing yet today" latest={latest} />
    </MemoryRouter>
  );

  it("says \"Nothing in the last seven days.\" when the shelf shows the latest arrivals and there are none", () => {
    render(shelf(true));
    expect(screen.getByText("Nothing in the last seven days.")).toBeInTheDocument();
    expect(screen.queryByText("Nothing has arrived yet today.")).toBeNull();
  });

  it("sends the shelf header's Library link to the movies library", () => {
    render(shelf(false));
    const region = screen.getByRole("region", { name: "Your library" });
    const link = within(region).getByRole("link", { name: /^Library:/ });
    expect(link).toHaveAttribute("href", "/movies");
  });
});
