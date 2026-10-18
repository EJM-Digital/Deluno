import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { ReleaseFilterMenus } from "./release-filter-menus";
import { filterCandidates } from "../../lib/release-candidate-columns";

const base = { protocols: [] as string[], protocol: "", onProtocolChange: () => undefined, indexer: "", onIndexerChange: () => undefined };

describe("the indexer filter above a list of releases", () => {
  it("is not drawn when the releases came from one indexer, and nothing at all is drawn when there is nothing to pick", () => {
    const { container, rerender } = render(<ReleaseFilterMenus {...base} indexers={["Fixture Indexer"]} />);
    expect(screen.queryByRole("combobox", { name: "Filter by indexer" })).toBeNull();
    expect(container).toBeEmptyDOMElement();

    rerender(<ReleaseFilterMenus {...base} indexers={["Fixture Indexer", "Second Indexer"]} />);
    expect(screen.getByRole("combobox", { name: "Filter by indexer" })).toBeInTheDocument();
  });

  it("offers Any indexer and each indexer's name, and picking one tells the page which indexer to narrow to", () => {
    const picked = vi.fn();
    render(<ReleaseFilterMenus {...base} indexers={["Fixture Indexer", "Second Indexer"]} onIndexerChange={picked} />);

    fireEvent.click(screen.getByRole("combobox", { name: "Filter by indexer" }));
    expect(screen.getAllByRole("option").map((option) => option.textContent)).toEqual(["Any indexer", "Fixture Indexer", "Second Indexer"]);
    fireEvent.click(screen.getByRole("option", { name: "Second Indexer" }));

    expect(picked).toHaveBeenCalledWith("Second Indexer");
  });

  it("narrows the releases to the chosen indexer's and brings every one back on Any indexer", () => {
    const releases = [
      { releaseName: "A", indexerName: "Fixture Indexer", indexerProtocol: "torznab", releaseAgeHours: null, seeders: null, leechers: null, sizeBytes: null, grabs: null },
      { releaseName: "B", indexerName: "Second Indexer", indexerProtocol: "torznab", releaseAgeHours: null, seeders: null, leechers: null, sizeBytes: null, grabs: null }
    ];

    expect(filterCandidates(releases, { protocol: "", indexerName: "Second Indexer" }).map((item) => item.releaseName)).toEqual(["B"]);
    expect(filterCandidates(releases, { protocol: "", indexerName: "" }).map((item) => item.releaseName)).toEqual(["A", "B"]);
  });
});
