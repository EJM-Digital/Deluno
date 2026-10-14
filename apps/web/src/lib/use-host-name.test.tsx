import { act, renderHook, waitFor } from "@testing-library/react";
import { QueryClientProvider } from "@tanstack/react-query";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

/** #1101: the tab and the sidebar name the computer Deluno runs on, from /api/system/info. */
const mocks = vi.hoisted(() => ({ token: "t" as string | null, calls: 0 }));

vi.mock("./use-auth", () => ({ useAuth: () => ({ token: mocks.token }) }));
vi.mock("./api", () => ({
  fetchJson: () => {
    mocks.calls += 1;
    return Promise.resolve({ hostName: "WORKSTATION", hostLooksLikeContainerId: false });
  }
}));

const { useHostName, hostTitle } = await import("./use-host-name");
const { queryClient } = await import("./route-cache");

const wrapper = ({ children }: { children: ReactNode }) => <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;

describe("host name (#1101)", () => {
  beforeEach(() => {
    queryClient.clear();
    mocks.calls = 0;
    mocks.token = "t";
  });

  it("titles the tab with the name once known and plain Deluno before", () => {
    expect(hostTitle("WORKSTATION")).toBe("Deluno · WORKSTATION");
    expect(hostTitle("  ")).toBe("Deluno");
    expect(hostTitle(null)).toBe("Deluno");
    expect(hostTitle(undefined)).toBe("Deluno");
  });

  it("is unknown at first, then the name the server gives", async () => {
    const { result } = renderHook(() => useHostName(), { wrapper });
    expect(result.current).toBeNull();
    await waitFor(() => expect(result.current).toBe("WORKSTATION"));
  });

  it("does not ask while signed out", async () => {
    mocks.token = null;
    const { result } = renderHook(() => useHostName(), { wrapper });
    vi.useFakeTimers();
    try {
      await act(async () => { await vi.advanceTimersByTimeAsync(20); });
    } finally {
      vi.useRealTimers();
    }
    expect(result.current).toBeNull();
    expect(mocks.calls).toBe(0);
  });

  it("shares one request between the tab and the sidebar", async () => {
    renderHook(() => [useHostName(), useHostName()], { wrapper });
    await waitFor(() => expect(mocks.calls).toBe(1));
  });
});
