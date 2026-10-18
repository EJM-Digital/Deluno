import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { useState } from "react";
import { createMemoryRouter, Link, RouterProvider, useLocation, useSearchParams } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useUnsavedChanges } from "../../hooks/use-unsaved-changes";
import { UnsavedChangesProvider } from "./unsaved-changes-provider";

/**
 * The prompt that stands between a person's unsaved edits and a move to another screen: what Save and continue does, what
 * closing the prompt does, when it never appears, and the browser's own "leave this page?" question.
 */

type Save = () => void | Promise<void>;

/** A settings-like editor: one field, a Save that clears the dirty mark (or does not), and links to other screens. */
function Editor({ save, ownSave, neverClean }: { save?: Save; ownSave?: boolean; neverClean?: boolean }) {
  const [value, setValue] = useState("");
  const [saved, setSaved] = useState("");
  const dirty = value !== saved;
  const location = useLocation();
  const [, setParams] = useSearchParams();
  useUnsavedChanges(dirty, "Your changes to this page are not saved.", ownSave ? async () => { await save?.(); if (!neverClean) setSaved(value); } : undefined);

  return (
    <div>
      <output data-testid="where">{location.pathname}{location.search}{location.hash}</output>
      <form onSubmit={(event) => { event.preventDefault(); void Promise.resolve(save?.()).then(() => setSaved(value)).catch(() => undefined); }}>
        <input aria-label="Folder" value={value} onChange={(event) => setValue(event.target.value)} />
        <button type="submit">Save</button>
      </form>
      <Link to="/other">Go to another screen</Link>
      <Link to="/editor?tab=files">Same screen, other tab</Link>
      <button type="button" onClick={() => setParams({ page: "2" })}>Next page</button>
      <button type="button" onClick={() => { window.location.hash = "#x"; }}>Jump</button>
    </div>
  );
}

function mount(save?: Save, ownSave = false, neverClean = false) {
  const router = createMemoryRouter(
    [
      { path: "/editor", element: <Editor save={save} ownSave={ownSave} neverClean={neverClean} /> },
      { path: "/other", element: <output data-testid="where">/other</output> }
    ],
    { initialEntries: ["/editor"] }
  );
  render(<UnsavedChangesProvider><RouterProvider router={router} /></UnsavedChangesProvider>);
  return router;
}

const type = (text: string) => fireEvent.change(screen.getByLabelText("Folder"), { target: { value: text } });
const where = () => screen.getByTestId("where").textContent;
const prompt = () => screen.queryByRole("dialog") ?? screen.queryByRole("alertdialog");

afterEach(() => { vi.useRealTimers(); });

describe("the unsaved-changes prompt", () => {
  it("does not appear when nothing was changed, and the move goes straight through", async () => {
    mount();
    fireEvent.click(screen.getByText("Go to another screen"));
    await waitFor(() => expect(where()).toBe("/other"));
    expect(prompt()).toBeNull();
  });

  it("appears when an edited page is left for another screen, and the person stays until they choose", async () => {
    mount();
    type("D:/Movies");
    fireEvent.click(screen.getByText("Go to another screen"));

    expect(await screen.findByText("Unsaved changes")).toBeTruthy();
    expect(screen.getByText("Your changes to this page are not saved.")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Save and continue" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Discard and continue" })).toBeTruthy();
    expect(where()).toBe("/editor");
  });

  it("Save and continue uses the editor's own save when it has one, instead of submitting a form", async () => {
    const save = vi.fn().mockResolvedValue(undefined);
    mount(save, true);
    type("D:/Movies");
    fireEvent.click(screen.getByText("Go to another screen"));
    fireEvent.click(await screen.findByRole("button", { name: "Save and continue" }));

    await waitFor(() => expect(where()).toBe("/other"), { timeout: 4000 });
    expect(save).toHaveBeenCalledTimes(1);
  });

  it("a save that fails keeps the prompt open and the person on the page with their edits", async () => {
    const save = vi.fn().mockRejectedValue(new Error("That folder does not exist."));
    mount(save, true);
    type("D:/Nowhere");
    fireEvent.click(screen.getByText("Go to another screen"));
    fireEvent.click(await screen.findByRole("button", { name: "Save and continue" }));

    await waitFor(() => expect(save).toHaveBeenCalled());
    await act(async () => { await new Promise((resolve) => setTimeout(resolve, 150)); });
    expect(prompt()).toBeTruthy();
    expect(where()).toBe("/editor");
    expect((screen.getByLabelText("Folder") as HTMLInputElement).value).toBe("D:/Nowhere");
  });

  it("a form that never clears its edits is waited for no more than 8 seconds, and the person is still not moved", async () => {
    // The save is answered, but the page never turns clean (the edit was refused after all).
    mount(vi.fn().mockResolvedValue(undefined), true, true);
    type("D:/Movies");
    fireEvent.click(screen.getByText("Go to another screen"));
    const button = await screen.findByRole("button", { name: "Save and continue" });

    vi.useFakeTimers();
    fireEvent.click(button);
    await act(async () => { await vi.advanceTimersByTimeAsync(7000); });
    expect(where()).toBe("/editor");
    await act(async () => { await vi.advanceTimersByTimeAsync(2000); });

    expect(where()).toBe("/editor");
    expect(prompt()).toBeTruthy();
  });

  it("Discard and continue leaves the page and drops the edits", async () => {
    mount();
    type("D:/Movies");
    fireEvent.click(screen.getByText("Go to another screen"));
    fireEvent.click(await screen.findByRole("button", { name: "Discard and continue" }));

    await waitFor(() => expect(where()).toBe("/other"));
  });

  it("a move that was blocked goes ahead by itself once the editor turns clean, without a second click", async () => {
    const save = vi.fn().mockResolvedValue(undefined);
    mount(save);
    type("D:/Movies");
    fireEvent.click(screen.getByText("Go to another screen"));
    await screen.findByRole("button", { name: "Save and continue" });
    expect(where()).toBe("/editor");

    // The person saves with the page's own Save button, behind the prompt, instead of using the prompt.
    fireEvent.click(screen.getByText("Save", { selector: "button[type=submit]" }));

    await waitFor(() => expect(where()).toBe("/other"));
  });

  it("closing the prompt with Escape cancels the move: the person stays with their edits", async () => {
    mount();
    type("D:/Movies");
    fireEvent.click(screen.getByText("Go to another screen"));
    await screen.findByRole("button", { name: "Save and continue" });

    fireEvent.keyDown(document.activeElement ?? document.body, { key: "Escape", code: "Escape" });

    await waitFor(() => expect(screen.queryByRole("button", { name: "Save and continue" })).toBeNull());
    expect(where()).toBe("/editor");
    expect((screen.getByLabelText("Folder") as HTMLInputElement).value).toBe("D:/Movies");
  });

  it("after the prompt was closed, the next move asks again", async () => {
    mount();
    type("D:/Movies");
    fireEvent.click(screen.getByText("Go to another screen"));
    await screen.findByRole("button", { name: "Save and continue" });
    fireEvent.keyDown(document.activeElement ?? document.body, { key: "Escape", code: "Escape" });
    await waitFor(() => expect(screen.queryByRole("button", { name: "Save and continue" })).toBeNull());

    fireEvent.click(screen.getByText("Go to another screen"));

    expect(await screen.findByRole("button", { name: "Save and continue" })).toBeTruthy();
    expect(where()).toBe("/editor");
  });

  it("a change to the page's own query string or the same path never asks", async () => {
    mount();
    type("D:/Movies");

    fireEvent.click(screen.getByText("Next page"));
    await waitFor(() => expect(where()).toBe("/editor?page=2"));
    fireEvent.click(screen.getByText("Same screen, other tab"));
    await waitFor(() => expect(where()).toBe("/editor?tab=files"));

    expect(prompt()).toBeNull();
    expect((screen.getByLabelText("Folder") as HTMLInputElement).value).toBe("D:/Movies");
  });
});

describe("closing or reloading the tab with edits", () => {
  it("asks the browser to confirm while there are unsaved edits", () => {
    mount();
    type("D:/Movies");

    const event = new Event("beforeunload", { cancelable: true }) as BeforeUnloadEvent;
    window.dispatchEvent(event);

    expect(event.defaultPrevented).toBe(true);
  });

  it("does not ask when nothing was changed, nor after the edits are saved", async () => {
    mount(vi.fn().mockResolvedValue(undefined));
    const clean = new Event("beforeunload", { cancelable: true }) as BeforeUnloadEvent;
    window.dispatchEvent(clean);
    expect(clean.defaultPrevented).toBe(false);

    type("D:/Movies");
    fireEvent.click(screen.getByText("Save", { selector: "button[type=submit]" }));
    await waitFor(() => {
      const after = new Event("beforeunload", { cancelable: true }) as BeforeUnloadEvent;
      window.dispatchEvent(after);
      expect(after.defaultPrevented).toBe(false);
    });
  });
});
