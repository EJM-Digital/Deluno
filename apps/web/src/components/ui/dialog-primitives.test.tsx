import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import * as React from "react";
import { describe, expect, it } from "vitest";
import { ConfirmDialog } from "./confirm-dialog";
import { Drawer } from "./drawer";
import { Sheet, SheetContent } from "./sheet";

/** #1340: a dialog opened from state hands focus back to the control that opened it, however it closes. */

function DrawerHost() {
  const [open, setOpen] = React.useState(false);
  return (
    <>
      <button type="button" onClick={() => setOpen(true)}>Open drawer</button>
      <Drawer
        open={open}
        onOpenChange={setOpen}
        title="Edit thing"
        onSubmit={(event) => { event.preventDefault(); setOpen(false); }}
        footer={<button type="submit">Save thing</button>}
      >
        <input aria-label="Name" />
      </Drawer>
    </>
  );
}

describe("drawers return focus to what opened them (#1340)", () => {
  it.each([
    ["Escape", async (user: ReturnType<typeof userEvent.setup>) => { await user.keyboard("{Escape}"); }],
    ["the close button", async (user: ReturnType<typeof userEvent.setup>) => { await user.click(screen.getByRole("button", { name: "Close" })); }],
    ["saving", async (user: ReturnType<typeof userEvent.setup>) => { await user.click(screen.getByRole("button", { name: "Save thing" })); }]
  ])("after %s", async (_how, close) => {
    const user = userEvent.setup();
    render(<DrawerHost />);
    const opener = screen.getByRole("button", { name: "Open drawer" });
    opener.focus();
    await user.keyboard("{Enter}");
    expect(await screen.findByRole("dialog")).toBeInTheDocument();
    expect(opener).not.toHaveFocus();
    await close(user);
    await Promise.resolve();
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(opener).toHaveFocus();
  });

  it("gives a confirm dialog back to its opener too", async () => {
    function Host() {
      const [open, setOpen] = React.useState(false);
      return (
        <>
          <button type="button" onClick={() => setOpen(true)}>Ask me</button>
          <ConfirmDialog open={open} onOpenChange={setOpen} title="Sure?" description="Really?" confirmLabel="Yes" onConfirm={() => setOpen(false)} />
        </>
      );
    }
    const user = userEvent.setup();
    render(<Host />);
    const opener = screen.getByRole("button", { name: "Ask me" });
    opener.focus();
    await user.keyboard("{Enter}");
    expect(await screen.findByRole("dialog")).toBeInTheDocument();
    await user.keyboard("{Escape}");
    expect(opener).toHaveFocus();
  });

  it("gives a sheet back to its opener too", async () => {
    function Host() {
      const [open, setOpen] = React.useState(false);
      return (
        <>
          <button type="button" onClick={() => setOpen(true)}>Browse</button>
          <Sheet open={open} onOpenChange={setOpen}>
            <SheetContent side="right" aria-label="Folders" description="Pick a folder"><p>folders</p></SheetContent>
          </Sheet>
        </>
      );
    }
    const user = userEvent.setup();
    render(<Host />);
    const opener = screen.getByRole("button", { name: "Browse" });
    opener.focus();
    await user.keyboard("{Enter}");
    expect(await screen.findByRole("dialog")).toBeInTheDocument();
    await user.keyboard("{Escape}");
    expect(opener).toHaveFocus();
  });
});
