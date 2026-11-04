import { act, renderHook, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

const fetchJson = vi.fn();
vi.mock("./api", () => ({ fetchJson: (...args: unknown[]) => fetchJson(...args) }));

import { connectionVersionLine, useConnectionVersions, versionKey, type ConnectionVersion } from "./connection-versions";

function version(overrides: Partial<ConnectionVersion> = {}): ConnectionVersion {
  return { kind: "downloadClient", id: "c1", version: "4.6.7", latestVersion: "5.0.2", updateAvailable: true, releaseUrl: null, ...overrides };
}

describe("the versions every connection last reported", () => {
  beforeEach(() => { fetchJson.mockReset(); });

  it("keys a version by what it is and which one", () => {
    expect(versionKey("indexer", "i1")).toBe("indexer:i1");
  });

  it("starts with none, then holds each connection under its key", async () => {
    fetchJson.mockResolvedValue([version(), version({ kind: "indexer", id: "i1", version: "1.0" })]);
    const { result } = renderHook(() => useConnectionVersions());
    expect(result.current.versions.size).toBe(0);

    await waitFor(() => expect(result.current.versions.size).toBe(2));
    expect(result.current.versions.get("downloadClient:c1")?.version).toBe("4.6.7");
    expect(result.current.versions.get("indexer:i1")?.version).toBe("1.0");
    expect(fetchJson).toHaveBeenCalledWith("/api/connections/versions");
  });

  it("shows nothing and is not wrong when the server does not answer", async () => {
    fetchJson.mockImplementation(() => Promise.reject(new Error("offline")));
    const { result } = renderHook(() => useConnectionVersions());
    await waitFor(() => expect(fetchJson).toHaveBeenCalledTimes(1));
    await act(async () => { await Promise.resolve(); });
    expect(result.current.versions.size).toBe(0);
  });

  it("ignores an answer that is not a list", async () => {
    fetchJson.mockResolvedValue({ unexpected: true });
    const { result } = renderHook(() => useConnectionVersions());
    await waitFor(() => expect(fetchJson).toHaveBeenCalledTimes(1));
    await act(async () => { await Promise.resolve(); });
    expect(result.current.versions.size).toBe(0);
  });

  it("does not fill in an answer that arrives after the screen has gone", async () => {
    let answer: (value: unknown) => void = () => undefined;
    fetchJson.mockReturnValue(new Promise((resolve) => { answer = resolve; }));
    const { result, unmount } = renderHook(() => useConnectionVersions());
    unmount();
    await act(async () => { answer([version()]); await Promise.resolve(); });
    expect(result.current.versions.size).toBe(0);
  });

  it("reads again when a screen reloads it after testing a connection", async () => {
    fetchJson.mockResolvedValueOnce([version()]).mockResolvedValueOnce([version({ version: "5.0.2", updateAvailable: false })]);
    const { result } = renderHook(() => useConnectionVersions());
    await waitFor(() => expect(result.current.versions.get("downloadClient:c1")?.version).toBe("4.6.7"));

    act(() => result.current.reload());

    await waitFor(() => expect(result.current.versions.get("downloadClient:c1")?.version).toBe("5.0.2"));
    expect(fetchJson).toHaveBeenCalledTimes(2);
  });
});

describe("a connection's version as one plain line", () => {
  it("says the version with the newer one available", () => {
    expect(connectionVersionLine(version())).toBe("4.6.7 · 5.0.2 available");
  });

  it("says just the version when it is current, or when the newer one is not known", () => {
    expect(connectionVersionLine(version({ updateAvailable: false }))).toBe("4.6.7");
    expect(connectionVersionLine(version({ latestVersion: null }))).toBe("4.6.7");
  });

  it("falls back to the version the screen already has, and to nothing", () => {
    expect(connectionVersionLine(null, "3.1")).toBe("3.1");
    expect(connectionVersionLine(undefined, undefined)).toBe("");
    expect(connectionVersionLine(null, null)).toBe("");
    expect(connectionVersionLine(version({ version: "" }), "9.9")).toBe("");
  });

  it("prefers what the connection reported over the fallback", () => {
    expect(connectionVersionLine(version({ updateAvailable: false }), "9.9")).toBe("4.6.7");
  });
});
