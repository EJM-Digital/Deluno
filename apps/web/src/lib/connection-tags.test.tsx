import { act, renderHook } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { applyConnectionTags, useRowSelection } from "./connection-tags";
import { authedFetch } from "./use-auth";

vi.mock("./use-auth", () => ({ authedFetch: vi.fn() }));

const reply = (body: unknown, status = 200) => new Response(typeof body === "string" ? body : JSON.stringify(body), { status });
beforeEach(() => { vi.mocked(authedFetch).mockReset(); });

describe("changing tags on several connections", () => {
  it("posts the kind, the connections, the tags and the mode in one request, and returns how many changed", async () => {
    vi.mocked(authedFetch).mockResolvedValue(reply({ updated: 3 }));
    const result = await applyConnectionTags("indexer", ["a", "b", "c"], ["4k"], "add");
    expect(result).toEqual({ updated: 3 });
    expect(authedFetch).toHaveBeenCalledWith("/api/tags/bulk-connections", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ kind: "indexer", ids: ["a", "b", "c"], tags: ["4k"], mode: "add" })
    });
  });

  it("says the first validation message the server gave", async () => {
    vi.mocked(authedFetch).mockResolvedValue(reply({ errors: { tags: ["Too many tags", "Other"], ids: ["No ids"] }, title: "Invalid" }, 400));
    await expect(applyConnectionTags("downloadClient", ["a"], ["x"], "replace")).rejects.toThrow("Too many tags");
  });

  it("says the title when there are no field messages", async () => {
    vi.mocked(authedFetch).mockResolvedValue(reply({ title: "Not allowed" }, 403));
    await expect(applyConnectionTags("notification", ["a"], [], "remove")).rejects.toThrow("Not allowed");
  });

  it("says it could not change the tags when the server gives no words", async () => {
    vi.mocked(authedFetch).mockResolvedValue(reply("<html>", 500));
    await expect(applyConnectionTags("indexer", ["a"], [], "add")).rejects.toThrow("Could not change the tags.");
    vi.mocked(authedFetch).mockResolvedValue(reply({}, 500));
    await expect(applyConnectionTags("indexer", ["a"], [], "add")).rejects.toThrow("Could not change the tags.");
    vi.mocked(authedFetch).mockResolvedValue(reply({ errors: {} }, 500));
    await expect(applyConnectionTags("indexer", ["a"], [], "add")).rejects.toThrow("Could not change the tags.");
  });
});

describe("ticking rows on a list", () => {
  it("starts off, with nothing ticked", () => {
    const { result } = renderHook(() => useRowSelection());
    expect(result.current.active).toBe(false);
    expect(result.current.selected.size).toBe(0);
  });

  it("ticks and unticks a row", () => {
    const { result } = renderHook(() => useRowSelection());
    act(() => result.current.toggle("a"));
    act(() => result.current.toggle("b"));
    expect([...result.current.selected]).toEqual(["a", "b"]);
    act(() => result.current.toggle("a"));
    expect([...result.current.selected]).toEqual(["b"]);
  });

  it("ticks every row at once, replacing what was ticked, and clears them", () => {
    const { result } = renderHook(() => useRowSelection());
    act(() => result.current.toggle("z"));
    act(() => result.current.setAll(["a", "b"]));
    expect([...result.current.selected]).toEqual(["a", "b"]);
    act(() => result.current.clear());
    expect(result.current.selected.size).toBe(0);
  });

  it("starts and stops choosing, forgetting the rows each time", () => {
    const { result } = renderHook(() => useRowSelection());
    act(() => result.current.toggleActive());
    expect(result.current.active).toBe(true);
    act(() => result.current.toggle("a"));
    act(() => result.current.toggleActive());
    expect(result.current.active).toBe(false);
    expect(result.current.selected.size).toBe(0);
    act(() => result.current.toggle("b"));
    act(() => result.current.toggleActive());
    expect(result.current.active).toBe(true);
    expect(result.current.selected.size).toBe(0);
  });

  it("keeps the same functions between renders", () => {
    const { result, rerender } = renderHook(() => useRowSelection());
    const first = result.current;
    rerender();
    expect(result.current.toggle).toBe(first.toggle);
    expect(result.current.toggleActive).toBe(first.toggleActive);
    expect(result.current.setAll).toBe(first.setAll);
    expect(result.current.clear).toBe(first.clear);
  });
});
