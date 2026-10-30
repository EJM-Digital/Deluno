import { act, renderHook } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

const mocks = vi.hoisted(() => ({ authedFetch: vi.fn(), revalidate: vi.fn() }));
vi.mock("./use-auth", () => ({ authedFetch: mocks.authedFetch }));
vi.mock("react-router-dom", () => ({ useRevalidator: () => ({ revalidate: mocks.revalidate }) }));

const { useApiMutation } = await import("./use-api-mutation");

beforeEach(() => { mocks.authedFetch.mockReset(); mocks.revalidate.mockReset(); });

const reply = (status: number, body?: unknown) => new Response(body === undefined ? null : JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

describe("saving through the API", () => {
  it("sends the request as JSON, returns the answer, marks saved and refreshes the page data", async () => {
    mocks.authedFetch.mockResolvedValue(reply(200, { id: "1" }));
    const { result } = renderHook(() => useApiMutation<{ name: string }, { id: string }>("/api/things", "PUT"));
    expect(result.current.state).toBe("idle");

    let answer: unknown;
    await act(async () => { answer = await result.current.mutate({ name: "n" }); });

    expect(answer).toEqual({ id: "1" });
    expect(mocks.authedFetch).toHaveBeenCalledWith("/api/things", { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ name: "n" }) });
    expect(result.current.state).toBe("saved");
    expect(mocks.revalidate).toHaveBeenCalledTimes(1);
  });

  it("answers nothing for a 204", async () => {
    mocks.authedFetch.mockResolvedValue(reply(204));
    const { result } = renderHook(() => useApiMutation("/api/things", "DELETE"));
    let answer: unknown = "unset";
    await act(async () => { answer = await result.current.mutate({}); });
    expect(answer).toBeUndefined();
    expect(result.current.state).toBe("saved");
  });

  it("explains in plain words when nothing answered, and rethrows any other failure as it is", async () => {
    const { result } = renderHook(() => useApiMutation("/api/things", "POST"));
    mocks.authedFetch.mockRejectedValueOnce(new TypeError("Failed to fetch"));
    await act(async () => {
      await expect(result.current.mutate({})).rejects.toThrow("Deluno could not reach its server, so nothing was saved. Check that it is running and try again.");
    });
    expect(result.current.state).toBe("error");

    const boom = new Error("boom");
    mocks.authedFetch.mockRejectedValueOnce(boom);
    await act(async () => { await expect(result.current.mutate({})).rejects.toBe(boom); });
    expect(mocks.revalidate).not.toHaveBeenCalled();
  });

  it("keeps the server's field messages and raises the first as the error", async () => {
    mocks.authedFetch.mockResolvedValue(reply(400, { title: "Bad", errors: { name: ["Too short.", "Add more."], path: ["Missing."] } }));
    const { result } = renderHook(() => useApiMutation("/api/things", "POST"));
    await act(async () => { await expect(result.current.mutate({})).rejects.toThrow("Too short."); });
    expect(result.current.fieldErrors).toEqual({ name: "Too short. Add more.", path: "Missing." });
    expect(result.current.state).toBe("error");
  });

  it("uses the problem title, or the status, when there are no field messages", async () => {
    const { result } = renderHook(() => useApiMutation("/api/things", "POST"));
    mocks.authedFetch.mockResolvedValueOnce(reply(409, { title: "Already exists." }));
    await act(async () => { await expect(result.current.mutate({})).rejects.toThrow("Already exists."); });
    mocks.authedFetch.mockResolvedValueOnce(new Response("not json", { status: 500 }));
    await act(async () => { await expect(result.current.mutate({})).rejects.toThrow("Request failed with status 500."); });
    expect(result.current.fieldErrors).toEqual({});
  });
});
