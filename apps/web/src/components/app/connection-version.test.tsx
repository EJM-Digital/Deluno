import { render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { fetchJson } from "../../lib/api";
import { useConnectionVersions, versionKey, type ConnectionVersion } from "../../lib/connection-versions";
import { formatResetTime } from "../../lib/reset-times";
import { ConnectionVersionBeside, ConnectionVersionText } from "./connection-version";

vi.mock("../../lib/api", () => ({ fetchJson: vi.fn() }));

const upToDate: ConnectionVersion = { kind: "downloadClient", id: "qb", version: "5.0.2", latestVersion: "5.0.2", updateAvailable: false, releaseUrl: null };
const outOfDate: ConnectionVersion = {
  kind: "downloadClient",
  id: "qb",
  version: "4.6.7",
  latestVersion: "5.0.2",
  updateAvailable: true,
  releaseUrl: "https://github.com/qbittorrent/qBittorrent/releases/tag/release-5.0.2"
};

describe("a connection's version in its row (#1117)", () => {
  it("says just the version when it is current", () => {
    const { container } = render(<ConnectionVersionText info={upToDate} />);
    expect(container.textContent).toBe("5.0.2");
  });

  it("says the newer version beside it when one is out, linked to its release page", () => {
    const { container } = render(<ConnectionVersionText info={outOfDate} />);
    expect(container.textContent).toBe("4.6.7 · 5.0.2 available");
    const link = screen.getByRole("link", { name: /5\.0\.2 available/ });
    expect(link).toHaveAttribute("href", "https://github.com/qbittorrent/qBittorrent/releases/tag/release-5.0.2");
    expect(link.className).toContain("text-info");
  });

  it("says the newer version without a link when no release page is known", () => {
    const { container } = render(<ConnectionVersionText info={{ ...outOfDate, releaseUrl: null }} />);
    expect(container.textContent).toBe("4.6.7 · 5.0.2 available");
    expect(screen.queryByRole("link")).toBeNull();
  });

  it("never calls it out of date when the latest version is not known", () => {
    const { container } = render(<ConnectionVersionText info={{ ...outOfDate, latestVersion: null, updateAvailable: true }} />);
    expect(container.textContent).toBe("4.6.7");
    expect(container.textContent).not.toContain("available");
  });

  it("says when GitHub's limit stopped the look for a newer version, in the viewer's own time (#1181)", () => {
    const until = new Date(Date.now() + 40 * 60_000);
    const { container } = render(<ConnectionVersionText info={{ ...upToDate, latestVersion: null, latestCheckPausedUntilUtc: until.toISOString() }} />);
    expect(container.textContent).toContain("5.0.2 · can't look for a newer version until ");
    expect(container.textContent).toContain(formatResetTime(until));
  });

  it("says when the release feed could not be read, never that the version is the newest (#1181)", () => {
    const { container } = render(<ConnectionVersionText info={{ ...upToDate, latestVersion: null, latestCheckProblem: "the release feed isn't reachable" }} />);
    expect(container.textContent).toBe("5.0.2 · can't look for a newer version: the release feed isn't reachable");
  });

  it("shows nothing for a connection that reported no version", () => {
    const { container } = render(<ConnectionVersionBeside info={undefined} />);
    expect(container.textContent).toBe("");
  });

  it("falls back to a live reading until the stored version arrives", () => {
    const { container } = render(<ConnectionVersionBeside info={undefined} fallback="4.3.2" />);
    expect(container.textContent).toBe("4.3.2");
  });
});

describe("reading every connection's version (#1117)", () => {
  beforeEach(() => vi.mocked(fetchJson).mockReset());

  function Probe() {
    const { versions } = useConnectionVersions();
    return <ConnectionVersionBeside info={versions.get(versionKey("downloadClient", "qb"))} />;
  }

  it("shows the version once the server has answered", async () => {
    vi.mocked(fetchJson).mockResolvedValue([outOfDate]);
    const { container } = render(<Probe />);
    await waitFor(() => expect(container.textContent).toBe("4.6.7 · 5.0.2 available"));
    expect(fetchJson).toHaveBeenCalledWith("/api/connections/versions");
  });

  it("shows nothing, and nothing is wrong, when the server answers with something else", async () => {
    vi.mocked(fetchJson).mockResolvedValue({ title: "Not found" });
    const { container } = render(<Probe />);
    await waitFor(() => expect(fetchJson).toHaveBeenCalled());
    expect(container.textContent).toBe("");
  });
});

describe("a connection's version, edge cases (#1117)", () => {
  it("shows nothing at all when there is no info or the connection reported no version", () => {
    expect(render(<ConnectionVersionText info={null} />).container).toBeEmptyDOMElement();
    expect(render(<ConnectionVersionText info={{ ...upToDate, version: null as unknown as string }} />).container).toBeEmptyDOMElement();
  });

  it("clicking the release-notes link does not also click the row around it", () => {
    const onRowClick = vi.fn();
    render(<div onClick={onRowClick}><ConnectionVersionText info={outOfDate} /></div>);
    const link = screen.getByRole("link", { name: "5.0.2 available: read the release notes" });
    link.addEventListener("click", (event) => event.preventDefault());
    link.click();
    expect(onRowClick).not.toHaveBeenCalled();
  });
});
