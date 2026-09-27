import { act, renderHook } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { useLatestRequest } from "./use-latest-request";

describe("useLatestRequest (#934)", () => {
  it("marks every earlier token stale the moment a newer one starts", () => {
    const { result } = renderHook(() => useLatestRequest());

    let tokenA = 0;
    act(() => { tokenA = result.current.next(); });
    expect(result.current.isStale(tokenA)).toBe(false);

    // Click B lands before A's response does — A must never render.
    let tokenB = 0;
    act(() => { tokenB = result.current.next(); });
    expect(result.current.isStale(tokenA)).toBe(true);
    expect(result.current.isStale(tokenB)).toBe(false);
  });

  it("keeps the same request current across re-renders", () => {
    const { result, rerender } = renderHook(() => useLatestRequest());
    let token = 0;
    act(() => { token = result.current.next(); });
    rerender();
    expect(result.current.isStale(token)).toBe(false);
  });

  it("resolves out of order — A then B started, B then A finish — and only B counts as current", () => {
    const { result } = renderHook(() => useLatestRequest());
    const tokenA = result.current.next();
    const tokenB = result.current.next();

    // B's response arrives first.
    expect(result.current.isStale(tokenB)).toBe(false);
    // A's response arrives after, and is still the older request.
    expect(result.current.isStale(tokenA)).toBe(true);
  });
});
