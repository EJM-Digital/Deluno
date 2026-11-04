import { act, renderHook, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { fetchJson } from "../lib/api";
import { useConfiguredProviders } from "./use-configured-providers";

vi.mock("../lib/api", async (importOriginal) => ({ ...(await importOriginal<typeof import("../lib/api")>()), fetchJson: vi.fn() }));
beforeEach(() => { vi.mocked(fetchJson).mockReset(); });

describe("which metadata providers are set up", () => {
  it("lists only the sources that have a key, by name", async () => {
    vi.mocked(fetchJson).mockResolvedValue({ sources: [{ source: "tmdb", isConfigured: true }, { source: "omdb", isConfigured: false }, { source: "tvdb", isConfigured: true }] } as never);
    const { result } = renderHook(() => useConfiguredProviders());
    expect(result.current).toEqual([]);
    await waitFor(() => expect(result.current).toEqual(["tmdb", "tvdb"]));
    expect(fetchJson).toHaveBeenCalledWith("/api/metadata/status");
  });

  it("claims nothing when the status has no sources", async () => {
    vi.mocked(fetchJson).mockResolvedValue({} as never);
    const { result } = renderHook(() => useConfiguredProviders());
    await waitFor(() => expect(fetchJson).toHaveBeenCalled());
    expect(result.current).toEqual([]);
  });

  it("claims nothing is missing when the status cannot be read", async () => {
    vi.mocked(fetchJson).mockImplementation(() => Promise.reject(new Error("down")));
    const { result } = renderHook(() => useConfiguredProviders());
    await act(async () => { await new Promise((resolve) => setTimeout(resolve, 20)); });
    expect(fetchJson).toHaveBeenCalledTimes(1);
    expect(result.current).toEqual([]);
  });
});
