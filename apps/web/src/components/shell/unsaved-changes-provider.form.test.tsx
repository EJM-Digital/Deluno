import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { useState } from "react";
import { createMemoryRouter, Link, RouterProvider, useLocation } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useUnsavedChanges } from "../../hooks/use-unsaved-changes";
import { UnsavedChangesProvider } from "./unsaved-changes-provider";

/**
 * Save and continue on an editor that has no save of its own: the prompt finds the form in front of the person and submits it
 * (a form inside a dialog first), and a page with no visible form simply stays put.
 */

function Editor({ description, hiddenForm, extraDialogForm }: { description?: string; hiddenForm?: boolean; extraDialogForm?: boolean }) {
  const [value, setValue] = useState("");
  const [saved, setSaved] = useState("");
  const location = useLocation();
  useUnsavedChanges(value !== saved, description);

  return (
    <div>
      <output data-testid="where">{location.pathname}</output>
      <div aria-hidden={hiddenForm ? "true" : undefined}>
        <form data-testid="page-form" onSubmit={(event) => { event.preventDefault(); submissions.push("page"); setSaved(value); }}>
          <input aria-label="Folder" value={value} onChange={(event) => setValue(event.target.value)} />
        </form>
      </div>
      {extraDialogForm ? (
        <div role="dialog" aria-label="Side editor">
          <form data-testid="drawer-form" onSubmit={(event) => { event.preventDefault(); submissions.push("drawer"); setSaved(value); }} />
        </div>
      ) : null}
      <Link to="/other">Go to another screen</Link>
    </div>
  );
}

function mount(editor: React.ReactElement) {
  const router = createMemoryRouter(
    [{ path: "/editor", element: editor }, { path: "/other", element: <output data-testid="where">/other</output> }],
    { initialEntries: ["/editor"] }
  );
  render(<UnsavedChangesProvider><RouterProvider router={router} /></UnsavedChangesProvider>);
}

/** jsdom has no layout, so every form reports "not shown" unless told otherwise. */
function showForms() {
  vi.spyOn(HTMLFormElement.prototype, "getClientRects").mockReturnValue([{}] as unknown as DOMRectList);
  // jsdom ships requestSubmit; make sure it fires submit handlers.
}

const type = (text: string) => fireEvent.change(screen.getByLabelText("Folder"), { target: { value: text } });
const where = () => screen.getByTestId("where").textContent;

const submissions: string[] = [];
afterEach(() => { vi.restoreAllMocks(); submissions.length = 0; });

describe("Save and continue on an editor with no save of its own", () => {
  it("asks with a plain fallback sentence when the editor gave none, and submits the visible form", async () => {
    showForms();
    mount(<Editor />);
    type("D:/Movies");
    fireEvent.click(screen.getByText("Go to another screen"));
    expect(await screen.findByText("You have unsaved changes. Choose how to handle them before leaving.")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Save and continue" }));

    await waitFor(() => expect(where()).toBe("/other"), { timeout: 4000 });
  });

  it("submits a form inside a dialog in preference to the page's form", async () => {
    showForms();
    mount(<Editor description="Mind the gap." extraDialogForm />);
    type("D:/Movies");
    fireEvent.click(screen.getByText("Go to another screen"));
    expect(await screen.findByText("Mind the gap.")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Save and continue" }));

    await waitFor(() => expect(submissions).toEqual(["drawer"]));
  });

  it("stays on the page, still asking, when no form is on screen to submit", async () => {
    mount(<Editor hiddenForm />);
    type("D:/Movies");
    fireEvent.click(screen.getByText("Go to another screen"));
    const save = await screen.findByRole("button", { name: "Save and continue" });
    fireEvent.click(save);

    await new Promise((resolve) => setTimeout(resolve, 100));
    expect(where()).toBe("/editor");
    expect((screen.getByRole("button", { name: "Save and continue" }) as HTMLButtonElement).disabled).toBe(false);
  });

  it("hides forms that are inside a hidden area even when they take up room", async () => {
    showForms();
    mount(<Editor hiddenForm />);
    type("D:/Movies");
    fireEvent.click(screen.getByText("Go to another screen"));
    fireEvent.click(await screen.findByRole("button", { name: "Save and continue" }));

    await new Promise((resolve) => setTimeout(resolve, 100));
    expect(where()).toBe("/editor");
    expect(submissions).toEqual([]);
  });
});
