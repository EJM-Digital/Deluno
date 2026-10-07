import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { SearchResultNote } from "./search-result-note";

describe("the result of the last search", () => {
  it("says nothing before any search has been run", () => {
    const { container } = render(<SearchResultNote note={null} />);
    expect(container).toBeEmptyDOMElement();
  });

  it("says it is searching while the search runs", () => {
    render(<SearchResultNote note={{ state: "searching", label: "Searching your indexers..." }} />);
    expect(screen.getByRole("status", { name: "Result of the last search" })).toHaveTextContent("Searching your indexers...");
  });

  it("keeps what was sent, or why nothing was, on the page", () => {
    const { rerender } = render(<SearchResultNote note={{ state: "done", tone: "success", title: "Sent The Matrix 1999 2160p to Deluge." }} />);
    expect(screen.getByRole("status")).toHaveTextContent("Sent The Matrix 1999 2160p to Deluge.");

    rerender(<SearchResultNote note={{ state: "done", tone: "info", title: "Nothing found.", description: "No indexer had a release that fits the profile." }} />);
    expect(screen.getByRole("status")).toHaveTextContent("Nothing found.");
    expect(screen.getByRole("status")).toHaveTextContent("No indexer had a release that fits the profile.");
  });
});
