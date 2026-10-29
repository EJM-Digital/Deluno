import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it } from "vitest";
import { ConnectionsCard, type ConnectionTesting } from "./connections-card";
import { versionKey, type ConnectionVersion } from "../../../lib/connection-versions";

afterEach(cleanup);

const row = (id: string, name: string) => ({ key: `indexer:${id}`, kind: "indexer", id, name, state: "ok", sub: "Torznab", checkedUtc: new Date().toISOString(), testPath: null }) as never;
const info = (id: string, extra: Partial<ConnectionVersion>): [string, ConnectionVersion] => [versionKey("indexer", id), { kind: "indexer", id, version: "3.2.1", latestVersion: "3.2.1", updateAvailable: false, releaseUrl: null, ...extra }];

describe("System: connection versions", () => {
  it("shows each connection's version beside its name, and a newer one when it is out", () => {
    const versions = new Map([info("n1", { version: "3.2.1", latestVersion: "3.3.0", updateAvailable: true, releaseUrl: "https://example.test/r" }), info("n2", {})]);
    const testing: ConnectionTesting = { results: new Map(), checking: new Set(), passed: new Set(), test: async () => {}, testAll: async () => {}, allBusy: false, versions };
    render(<MemoryRouter><ConnectionsCard rows={[row("n1", "Alpha"), row("n2", "Beta")]} testing={testing} now={Date.now()} /></MemoryRouter>);
    const alpha = screen.getByText("Alpha").closest(".sy-nm") as HTMLElement;
    expect(alpha.textContent).toContain("3.2.1");
    expect(alpha.textContent).toContain("3.3.0");
    const beta = screen.getByText("Beta").closest(".sy-nm") as HTMLElement;
    expect(beta.textContent).toContain("3.2.1");
    expect(beta.textContent).not.toContain("3.3.0");
  });
});
