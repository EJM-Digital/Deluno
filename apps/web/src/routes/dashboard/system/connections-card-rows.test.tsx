import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it } from "vitest";
import { ConnectionsCard, type ConnectionTesting } from "./connections-card";

afterEach(cleanup);

const testing: ConnectionTesting = {
  results: new Map(),
  checking: new Set(),
  passed: new Set(),
  test: async () => {},
  testAll: async () => {},
  allBusy: false
};

describe("System: Connections card", () => {
  it("says \"Nothing is connected yet. Add an indexer or a download client and it shows here.\" on a new install", () => {
    render(<MemoryRouter><ConnectionsCard rows={[]} testing={testing} now={Date.now()} /></MemoryRouter>);
    expect(screen.getByText("Nothing is connected yet. Add an indexer or a download client and it shows here.")).toBeInTheDocument();
  });

  it("sends \"Manage connections\" to Find and Download's indexers", () => {
    render(<MemoryRouter><ConnectionsCard rows={[]} testing={testing} now={Date.now()} /></MemoryRouter>);
    const link = screen.getByRole("link", { name: "Manage connections" });
    expect(link).toHaveTextContent("Manage →");
    expect(link).toHaveAttribute("href", "/indexers/indexers");
  });
});
