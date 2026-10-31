import { act, fireEvent, render, screen } from "@testing-library/react";
import { useContext } from "react";
import { describe, expect, it, vi } from "vitest";
import { UnsavedChangesContext, type UnsavedChangesRegistration } from "../../hooks/use-unsaved-changes";
import { UnsavedChangesProvider } from "./unsaved-changes-provider";

/** Drives the provider's register / unregister directly, with stand-in blockers, to show how it treats repeats and strangers. */

let api: NonNullable<React.ContextType<typeof UnsavedChangesContext>>;
function Grab() {
  api = useContext(UnsavedChangesContext)!;
  return null;
}

function registration(over: Partial<UnsavedChangesRegistration> & { state?: string } = {}) {
  const proceed = vi.fn();
  const reset = vi.fn();
  const { state = "blocked", ...rest } = over;
  const value = {
    id: "a", dirty: true, description: "Mind the gap.",
    blocker: { state, proceed, reset, location: undefined } as unknown as UnsavedChangesRegistration["blocker"],
    saveRef: { current: undefined },
    ...rest
  } as UnsavedChangesRegistration;
  return { value, proceed, reset };
}

function mount() {
  render(<UnsavedChangesProvider><Grab /></UnsavedChangesProvider>);
}

describe("the unsaved-changes registry", () => {
  it("keeps the same prompt when an editor registers again with nothing changed, and drops it when that editor leaves", () => {
    mount();
    const first = registration();
    act(() => api.register(first.value));
    expect(screen.getByText("Mind the gap.")).toBeTruthy();

    act(() => api.register({ ...first.value, blocker: { ...first.value.blocker } as never }));
    expect(screen.getByText("Mind the gap.")).toBeTruthy();

    act(() => api.unregister("someone-else"));
    expect(screen.getByText("Mind the gap.")).toBeTruthy();

    act(() => api.unregister("a"));
    expect(screen.queryByText("Mind the gap.")).toBeNull();
  });

  it("replaces the prompt when another editor registers or the wording changes", () => {
    mount();
    act(() => api.register(registration().value));
    act(() => api.register(registration({ description: "New words." }).value));
    expect(screen.getByText("New words.")).toBeTruthy();
    act(() => api.register(registration({ id: "b", description: "Other editor." }).value));
    expect(screen.getByText("Other editor.")).toBeTruthy();
  });

  it("does not ask when the blocker is not holding a move or the editor is clean", () => {
    mount();
    act(() => api.register(registration({ state: "unblocked" }).value));
    expect(screen.queryByText("Unsaved changes")).toBeNull();
    act(() => api.register(registration({ dirty: false }).value));
    expect(screen.queryByText("Unsaved changes")).toBeNull();
  });

  it("leaves with Discard and continue, and stays with Cancel", () => {
    mount();
    const entry = registration();
    act(() => api.register(entry.value));
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(entry.reset).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByRole("button", { name: "Discard and continue" }));
    expect(entry.proceed).toHaveBeenCalledTimes(1);
  });

  it("stops waiting for the save when the editor goes away mid-save", async () => {
    mount();
    const entry = registration();
    entry.value.saveRef.current = async () => { api.unregister("a"); };
    act(() => api.register(entry.value));
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Save and continue" })); });
    // The save goes on polling every 50 ms until it sees the editor gone; let it end inside the test, or its last state
    // update lands after the test environment is torn down.
    await act(async () => { await new Promise<void>((resolve) => window.setTimeout(resolve, 120)); });
    expect(screen.queryByText("Mind the gap.")).toBeNull();
    expect(entry.proceed).not.toHaveBeenCalled();
  });
});
