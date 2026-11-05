import { act, cleanup, render, renderHook, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const mocks = vi.hoisted(() => ({
  authedFetch: vi.fn(),
  toast: { success: vi.fn(), error: vi.fn() }
}));
vi.mock("../../lib/use-auth", () => ({ authedFetch: mocks.authedFetch }));
vi.mock("../../components/shell/toaster", () => ({ toast: mocks.toast }));

const { ActionButton, send, useAction } = await import("./downloads-kit");

beforeEach(() => {
  mocks.authedFetch.mockReset();
  mocks.toast.success.mockReset();
  mocks.toast.error.mockReset();
});
afterEach(cleanup);

describe("running one action", () => {
  it("says nothing when the task has nothing to say, and says the task's words when it does", async () => {
    const { result } = renderHook(() => useAction());

    await act(async () => { await result.current.run("quiet", async () => undefined); });
    expect(mocks.toast.success).not.toHaveBeenCalled();
    expect(result.current.busy).toBeNull();

    await act(async () => { await result.current.run("loud", async () => "Queued to run again."); });
    expect(mocks.toast.success).toHaveBeenCalledWith("Queued to run again.");
  });

  it("says the error's own words, or a plain sentence when what was thrown is not an error, and is free again after", async () => {
    const { result } = renderHook(() => useAction());

    await act(async () => { await result.current.run("a", async () => { throw new Error("The client said no."); }); });
    expect(mocks.toast.error).toHaveBeenLastCalledWith("The client said no.");

    await act(async () => { await result.current.run("b", async () => { throw "nope"; }); });
    expect(mocks.toast.error).toHaveBeenLastCalledWith("That did not work. Nothing was changed.");
    expect(result.current.busy).toBeNull();
  });
});

describe("sending a request", () => {
  it("posts with no body and no content type, and hands back what the server answered", async () => {
    mocks.authedFetch.mockResolvedValue(new Response(JSON.stringify({ message: "Done." }), { status: 200 }));

    expect(await send("/api/jobs/1/retry", "Could not retry.")).toEqual({ message: "Done." });
    expect(mocks.authedFetch).toHaveBeenCalledWith("/api/jobs/1/retry", { method: "POST", headers: undefined, body: undefined });
  });

  it("sends a body as JSON with the verb it was given, and answers null when the reply is not JSON", async () => {
    mocks.authedFetch.mockResolvedValue(new Response("", { status: 200 }));

    expect(await send("/api/jobs/1", "Could not change it.", { method: "PUT", body: { a: 1 } })).toBeNull();
    expect(mocks.authedFetch).toHaveBeenCalledWith("/api/jobs/1", { method: "PUT", headers: { "Content-Type": "application/json" }, body: "{\"a\":1}" });
  });

  it("throws with the server's own words when it says no, and with the caller's sentence when it says nothing", async () => {
    mocks.authedFetch.mockResolvedValueOnce(new Response(JSON.stringify({ message: "That job is gone." }), { status: 404 }));
    await expect(send("/api/jobs/1/retry", "Could not retry.")).rejects.toThrow("That job is gone.");

    mocks.authedFetch.mockResolvedValueOnce(new Response("", { status: 500 }));
    await expect(send("/api/jobs/1/retry", "Could not retry.")).rejects.toThrow("Could not retry.");
  });
});

describe("an action button", () => {
  const free = { busy: null, run: vi.fn() };

  it("is a plain button by default, named for what it acts on", () => {
    render(<ActionButton label="Retry" name="Retry Sintel (2010)" busyKey="retry" state={free} onClick={() => {}} hint="Try it again." />);

    const button = screen.getByRole("button", { name: "Retry Sintel (2010)" });
    expect(button).toHaveAttribute("title", "Try it again.");
    expect(button).toBeEnabled();
  });

  it("takes the shared box on the board, a quieter one when it is a ghost, and the card box on a card", () => {
    render(
      <>
        <ActionButton label="A" name="Uniform" busyKey="a" state={free} onClick={() => {}} uniform />
        <ActionButton label="B" name="Uniform ghost" busyKey="b" state={free} onClick={() => {}} uniform variant="ghost" />
        <ActionButton label="C" name="Card" busyKey="c" state={free} onClick={() => {}} card />
        <ActionButton label="D" name="Card ghost" busyKey="d" state={free} onClick={() => {}} card variant="ghost" />
      </>
    );

    expect(screen.getByRole("button", { name: "Uniform" }).className).toContain("w-30");
    expect(screen.getByRole("button", { name: "Uniform ghost" }).className).toContain("text-muted-foreground");
    expect(screen.getByRole("button", { name: "Card" }).className).toContain("w-21");
    expect(screen.getByRole("button", { name: "Card ghost" }).className).toContain("text-muted-foreground");
  });

  it("shows its own spinner while it is the one running, and every button waits while any one runs", () => {
    render(
      <>
        <ActionButton label="Mine" name="Mine" busyKey="mine" state={{ busy: "mine", run: vi.fn() }} onClick={() => {}} />
        <ActionButton label="Other" name="Other" busyKey="other" state={{ busy: "mine", run: vi.fn() }} onClick={() => {}} />
      </>
    );

    expect(screen.getByRole("button", { name: "Mine" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Mine" }).querySelector("svg")).not.toBeNull();
    expect(screen.getByRole("button", { name: "Other" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Other" }).querySelector("svg")).toBeNull();
  });
});
