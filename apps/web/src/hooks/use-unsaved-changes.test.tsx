import { act, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { Link, RouterProvider, createMemoryRouter } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";
import { UnsavedChangesContext, useUnsavedChanges, type UnsavedChangesContextValue, type UnsavedChangesRegistration } from "./use-unsaved-changes";

function Editor({ dirty, description, save }: { dirty: boolean; description?: string; save?: () => void }) {
  const blocker = useUnsavedChanges(dirty, description, save);
  return (
    <div>
      <p>Editor is {blocker.state}</p>
      <Link to="/other">Leave</Link>
      <Link to="/?tab=2">Stay</Link>
    </div>
  );
}

function mount(editor: React.ReactElement, context: UnsavedChangesContextValue | null = null) {
  const router = createMemoryRouter(
    [
      { path: "/", element: <UnsavedChangesContext.Provider value={context}>{editor}</UnsavedChangesContext.Provider> },
      { path: "/other", element: <p>Other screen</p> }
    ],
    { initialEntries: ["/"] }
  );
  const view = render(<RouterProvider router={router} />);
  return { router, ...view };
}

describe("guarding edits against being lost", () => {
  it("lets a clean editor go anywhere", async () => {
    mount(<Editor dirty={false} />);
    await userEvent.click(screen.getByRole("link", { name: "Leave" }));
    expect(await screen.findByText("Other screen")).toBeInTheDocument();
  });

  it("blocks leaving the screen while there are unsaved edits, and not a change inside the same screen", async () => {
    const { router } = mount(<Editor dirty />);

    await userEvent.click(screen.getByRole("link", { name: "Stay" }));
    expect(router.state.location.search).toBe("?tab=2");
    expect(screen.getByText("Editor is unblocked")).toBeInTheDocument();

    await userEvent.click(screen.getByRole("link", { name: "Leave" }));
    expect(await screen.findByText("Editor is blocked")).toBeInTheDocument();
    expect(screen.queryByText("Other screen")).toBeNull();
  });

  it("carries on to where the person was going once the edits are saved", async () => {
    function SavingEditor() {
      const [dirty, setDirty] = useState(true);
      return (
        <>
          <Editor dirty={dirty} />
          <button type="button" onClick={() => setDirty(false)}>Save it</button>
        </>
      );
    }
    mount(<SavingEditor />);
    await userEvent.click(screen.getByRole("link", { name: "Leave" }));
    expect(await screen.findByText("Editor is blocked")).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Save it" }));

    expect(await screen.findByText("Other screen")).toBeInTheDocument();
  });

  it("asks the browser to confirm closing the tab while there are unsaved edits, and stops asking when saved", () => {
    const { unmount } = mount(<Editor dirty />);
    const event = new Event("beforeunload", { cancelable: true }) as BeforeUnloadEvent;
    const returned: unknown[] = [];
    Object.defineProperty(event, "returnValue", { set: (value) => returned.push(value), get: () => true, configurable: true });
    window.dispatchEvent(event);
    expect(event.defaultPrevented).toBe(true);
    expect(returned).toEqual([""]);

    unmount();
    const after = new Event("beforeunload", { cancelable: true }) as BeforeUnloadEvent;
    window.dispatchEvent(after);
    expect(after.defaultPrevented).toBe(false);
  });

  it("does not interfere with closing the tab when nothing is unsaved", () => {
    mount(<Editor dirty={false} />);
    const event = new Event("beforeunload", { cancelable: true }) as BeforeUnloadEvent;
    window.dispatchEvent(event);
    expect(event.defaultPrevented).toBe(false);
  });

  it("registers with the shared prompt, keeps its save, and unregisters when the editor goes", () => {
    const register = vi.fn<(registration: UnsavedChangesRegistration) => void>();
    const unregister = vi.fn<(id: string) => void>();
    const save = vi.fn();
    const { unmount } = mount(<Editor dirty description="Mind the gap." save={save} />, { register, unregister });

    expect(register).toHaveBeenCalledTimes(1);
    const registration = register.mock.calls[0][0];
    expect(registration).toMatchObject({ dirty: true, description: "Mind the gap." });
    expect(registration.id).toBeTruthy();
    act(() => { void registration.saveRef.current?.(); });
    expect(save).toHaveBeenCalledTimes(1);

    unmount();
    expect(unregister).toHaveBeenCalledWith(registration.id);
  });

  it("does not need a shared prompt to work", () => {
    expect(() => mount(<Editor dirty description="No provider here" />)).not.toThrow();
  });
});
