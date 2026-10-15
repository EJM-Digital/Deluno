import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { useState } from "react";
import { MemoryRouter, useLocation } from "react-router-dom";
import { afterEach, beforeAll, beforeEach, describe, expect, it } from "vitest";
import { navigationCommands, settingsCommands } from "../../lib/command-registry";
import { CommandPalette } from "./command-palette";

beforeAll(() => {
  // cmdk measures and scrolls its list; jsdom has neither.
  globalThis.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
  Element.prototype.scrollIntoView ??= () => {};
});

function Harness({ startOpen = true }: { startOpen?: boolean }) {
  const [open, setOpen] = useState(startOpen);
  const location = useLocation();
  return (
    <>
      <button type="button" onClick={() => setOpen(true)}>open palette</button>
      <p data-testid="where">{location.pathname}</p>
      <CommandPalette open={open} onOpenChange={setOpen} theme="dark" onToggleTheme={() => undefined} />
    </>
  );
}

function renderPalette(startOpen = true) {
  return render(<MemoryRouter initialEntries={["/somewhere"]}><Harness startOpen={startOpen} /></MemoryRouter>);
}

describe("the command palette", () => {
  beforeEach(() => localStorage.clear());
  afterEach(cleanup);

  it("opens as a dialog titled Command palette and clears its search box when it closes", async () => {
    renderPalette();
    const dialog = await screen.findByRole("dialog");
    expect(dialog.textContent).toContain("Command palette");
    const box = screen.getByPlaceholderText("Jump to area, run action…") as HTMLInputElement;
    fireEvent.change(box, { target: { value: "movies" } });
    expect(box.value).toBe("movies");

    fireEvent.keyDown(box, { key: "Escape" });
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    fireEvent.click(screen.getByRole("button", { name: "open palette" }));

    const reopened = (await screen.findByPlaceholderText("Jump to area, run action…")) as HTMLInputElement;
    expect(reopened.value).toBe("");
  });

  it("filters Navigation, Settings and Preferences entries by label and keywords, and says No matches when nothing fits", async () => {
    renderPalette();
    const box = (await screen.findByPlaceholderText("Jump to area, run action…")) as HTMLInputElement;
    expect(screen.getByText("Dashboard")).toBeTruthy();

    fireEvent.change(box, { target: { value: "dashboard" } });
    await waitFor(() => expect(screen.queryByText("Discover")).toBeNull());
    expect(screen.getByText("Dashboard")).toBeTruthy();

    // A keyword, not the label: the Dashboard entry is also found by "home".
    fireEvent.change(box, { target: { value: "home" } });
    await waitFor(() => expect(screen.getByText("Dashboard")).toBeTruthy());

    fireEvent.change(box, { target: { value: "zzzz-nothing-is-called-this" } });
    expect(await screen.findByText("No matches.")).toBeTruthy();
  });

  it.each(navigationCommands.filter((item) => item.to))("choosing the Navigation entry $label goes to $to and closes the palette", async (item) => {
    renderPalette();
    const box = (await screen.findByPlaceholderText("Jump to area, run action…")) as HTMLInputElement;
    fireEvent.change(box, { target: { value: item.label } });

    const entry = (await screen.findAllByText(item.label, { selector: "span" }))[0];
    fireEvent.click(entry);

    await waitFor(() => expect(screen.getByTestId("where").textContent).toBe(item.to));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });

  it.each(settingsCommands.filter((item) => item.to))("choosing the Settings entry $label goes to $to and closes the palette", async (item) => {
    renderPalette();
    const box = (await screen.findByPlaceholderText("Jump to area, run action…")) as HTMLInputElement;
    fireEvent.change(box, { target: { value: item.label } });

    const entry = (await screen.findAllByText(item.label, { selector: "span" }))[0];
    fireEvent.click(entry);

    await waitFor(() => expect(screen.getByTestId("where").textContent).toBe(item.to));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });
});
